using System.Text;

namespace WindowsClientCenter.Shared.Diagnostics;

public static class FileTailReader
{
    private const int TailReadBufferSize = 16 * 1024;
    private const int WatcherDebounceDelayMilliseconds = 150;

    public static bool CanFollowDirectly(string path)
    {
        return TryGetWatchContext(path, out _);
    }

    public static async Task<IReadOnlyList<string>> ReadTailLinesAsync(string path, int tailLineCount, CancellationToken cancellationToken)
    {
        var snapshot = await ReadTailSnapshotAsync(path, tailLineCount, cancellationToken);
        return snapshot.Lines;
    }

    public static async Task<TailReadResult> ReadTailSnapshotAsync(string path, int tailLineCount, CancellationToken cancellationToken)
    {
        if (tailLineCount <= 0)
        {
            return new TailReadResult([], 0);
        }

        await using var stream = SharedTextFileReader.OpenReadShared(path, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length == 0)
        {
            return new TailReadResult([], 0);
        }

        var encodingInfo = await DetectEncodingAsync(stream, cancellationToken);
        var lines = await ReadTailLinesBackwardAsync(stream, tailLineCount, encodingInfo, cancellationToken);
        return new TailReadResult(lines, stream.Length);
    }

    public static async Task FollowLinesAsync(
        string path,
        long? startPosition,
        Action<string> onLineRead,
        Action<long>? onPositionChanged,
        int pollDelayMilliseconds,
        CancellationToken cancellationToken)
    {
        if (TryGetWatchContext(path, out var context))
        {
            try
            {
                await FollowLinesWithWatcherAsync(context, startPosition, onLineRead, onPositionChanged, cancellationToken);
                return;
            }
            catch (WatcherUnavailableException) when (!cancellationToken.IsCancellationRequested)
            {
                // Fall back to polling when watcher-based following is unavailable.
            }
        }

        await FollowLinesWithPollingAsync(path, startPosition, onLineRead, onPositionChanged, pollDelayMilliseconds, cancellationToken);
    }

    private static async Task FollowLinesWithWatcherAsync(
        WatchContext context,
        long? startPosition,
        Action<string> onLineRead,
        Action<long>? onPositionChanged,
        CancellationToken cancellationToken)
    {
        using var signal = new SemaphoreSlim(0);
        Exception? watcherError = null;
        var resetRequested = 0;

        void ReleaseSignal()
        {
            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Ignore duplicate notifications.
            }
        }

        void OnChanged(object? sender, FileSystemEventArgs e)
        {
            if (IsTargetFileEvent(context.FileName, e.Name))
            {
                ReleaseSignal();
            }
        }

        void OnCreated(object? sender, FileSystemEventArgs e)
        {
            if (!IsTargetFileEvent(context.FileName, e.Name))
            {
                return;
            }

            Interlocked.Exchange(ref resetRequested, 1);
            ReleaseSignal();
        }

        void OnRenamed(object? sender, RenamedEventArgs e)
        {
            if (!IsTargetFileEvent(context.FileName, e.Name) &&
                !IsTargetFileEvent(context.FileName, e.OldName))
            {
                return;
            }

            Interlocked.Exchange(ref resetRequested, 1);
            ReleaseSignal();
        }

        void OnError(object? sender, ErrorEventArgs e)
        {
            watcherError = e.GetException() ?? new IOException($"File watcher failed for '{context.Path}'.");
            ReleaseSignal();
        }

        using var watcher = CreateWatcher(context);
        watcher.Changed += OnChanged;
        watcher.Created += OnCreated;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;

        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new WatcherUnavailableException(context.Path, ex);
        }

        var currentPosition = await InitializeFollowPositionAsync(context.Path, startPosition, onPositionChanged, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref resetRequested, 0) == 1)
            {
                currentPosition = 0;
                onPositionChanged?.Invoke(currentPosition);
            }

            currentPosition = await DrainAvailableLinesAsync(
                context.Path,
                currentPosition,
                onLineRead,
                onPositionChanged,
                cancellationToken);

            await signal.WaitAsync(cancellationToken);
            if (watcherError is not null)
            {
                throw new WatcherUnavailableException(context.Path, watcherError);
            }

            while (signal.Wait(0))
            {
                // Coalesce bursts before the debounce delay.
            }

            await Task.Delay(WatcherDebounceDelayMilliseconds, cancellationToken);
        }
    }

    private static async Task FollowLinesWithPollingAsync(
        string path,
        long? startPosition,
        Action<string> onLineRead,
        Action<long>? onPositionChanged,
        int pollDelayMilliseconds,
        CancellationToken cancellationToken)
    {
        var currentPosition = await InitializeFollowPositionAsync(path, startPosition, onPositionChanged, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            currentPosition = await DrainAvailableLinesAsync(
                path,
                currentPosition,
                onLineRead,
                onPositionChanged,
                cancellationToken);

            await Task.Delay(pollDelayMilliseconds, cancellationToken);
        }
    }

    private static async Task<long> InitializeFollowPositionAsync(
        string path,
        long? startPosition,
        Action<long>? onPositionChanged,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(path))
        {
            var missingPosition = Math.Max(0, startPosition ?? 0);
            onPositionChanged?.Invoke(missingPosition);
            return missingPosition;
        }

        await using var stream = SharedTextFileReader.OpenReadShared(path, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var targetPosition = startPosition.HasValue
            ? Math.Clamp(startPosition.Value, 0, stream.Length)
            : stream.Length;
        onPositionChanged?.Invoke(targetPosition);
        return targetPosition;
    }

    private static async Task<long> DrainAvailableLinesAsync(
        string path,
        long currentPosition,
        Action<string> onLineRead,
        Action<long>? onPositionChanged,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(path))
        {
            if (currentPosition != 0)
            {
                onPositionChanged?.Invoke(0);
            }

            return 0;
        }

        await using var stream = SharedTextFileReader.OpenReadShared(path, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < currentPosition)
        {
            currentPosition = 0;
            onPositionChanged?.Invoke(currentPosition);
        }

        stream.Seek(currentPosition, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                onLineRead(line);
            }

            currentPosition = stream.Position;
            onPositionChanged?.Invoke(currentPosition);
        }

        return currentPosition;
    }

    private static FileSystemWatcher CreateWatcher(WatchContext context)
    {
        return new FileSystemWatcher(context.DirectoryPath, context.FileName)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
    }

    private static bool TryGetWatchContext(string path, out WatchContext context)
    {
        context = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var directoryPath = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(directoryPath) ||
            string.IsNullOrWhiteSpace(fileName) ||
            !Directory.Exists(directoryPath))
        {
            return false;
        }

        context = new WatchContext(path, directoryPath, fileName);
        return true;
    }

    private static bool IsTargetFileEvent(string expectedFileName, string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName) &&
               string.Equals(fileName, expectedFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<string>> ReadTailLinesBackwardAsync(
        FileStream stream,
        int tailLineCount,
        TailEncodingInfo encodingInfo,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>(tailLineCount);
        var remainder = Array.Empty<byte>();
        var skipTrailingEmptyLine = true;
        var position = stream.Length;

        while (position > encodingInfo.PreambleLength && lines.Count < tailLineCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkStart = GetChunkStart(position, encodingInfo.PreambleLength, encodingInfo.CodeUnitSize);
            var chunkLength = checked((int)(position - chunkStart));
            var chunk = new byte[chunkLength];

            stream.Seek(chunkStart, SeekOrigin.Begin);
            await ReadExactlyAsync(stream, chunk, cancellationToken);

            var combined = Combine(chunk, remainder);
            var segmentEnd = combined.Length;

            while (TryFindLastLineBreak(combined, segmentEnd, encodingInfo, out var lineBreakStart, out var lineBreakLength))
            {
                var contentStart = lineBreakStart + lineBreakLength;
                var contentLength = segmentEnd - contentStart;
                if (contentLength > 0 || !skipTrailingEmptyLine)
                {
                    lines.Add(encodingInfo.Encoding.GetString(combined, contentStart, contentLength));
                }

                skipTrailingEmptyLine = false;
                segmentEnd = lineBreakStart;
                if (lines.Count == tailLineCount)
                {
                    break;
                }
            }

            remainder = CopySegment(combined, 0, segmentEnd);
            position = chunkStart;
        }

        if (lines.Count < tailLineCount && remainder.Length > 0)
        {
            lines.Add(encodingInfo.Encoding.GetString(remainder));
        }

        lines.Reverse();
        return lines;
    }

    private static async Task<TailEncodingInfo> DetectEncodingAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Seek(0, SeekOrigin.Begin);

        var bom = new byte[4];
        var bytesRead = await stream.ReadAsync(bom.AsMemory(0, bom.Length), cancellationToken);

        if (bytesRead >= 3 &&
            bom[0] == 0xEF &&
            bom[1] == 0xBB &&
            bom[2] == 0xBF)
        {
            return new TailEncodingInfo(Encoding.UTF8, 3, 1, TailEncodingKind.SingleByte);
        }

        if (bytesRead >= 2 &&
            bom[0] == 0xFF &&
            bom[1] == 0xFE)
        {
            return new TailEncodingInfo(Encoding.Unicode, 2, 2, TailEncodingKind.Utf16LittleEndian);
        }

        if (bytesRead >= 2 &&
            bom[0] == 0xFE &&
            bom[1] == 0xFF)
        {
            return new TailEncodingInfo(Encoding.BigEndianUnicode, 2, 2, TailEncodingKind.Utf16BigEndian);
        }

        return new TailEncodingInfo(Encoding.UTF8, 0, 1, TailEncodingKind.SingleByte);
    }

    private static long GetChunkStart(long position, int preambleLength, int codeUnitSize)
    {
        var chunkStart = Math.Max((long)preambleLength, position - TailReadBufferSize);
        var distance = position - chunkStart;
        var misalignment = distance % codeUnitSize;
        if (misalignment != 0)
        {
            chunkStart += misalignment;
        }

        if (chunkStart >= position)
        {
            chunkStart = Math.Max((long)preambleLength, position - codeUnitSize);
        }

        return chunkStart;
    }

    private static bool TryFindLastLineBreak(
        byte[] buffer,
        int searchLength,
        TailEncodingInfo encodingInfo,
        out int lineBreakStart,
        out int lineBreakLength)
    {
        if (encodingInfo.Kind == TailEncodingKind.SingleByte)
        {
            for (var index = searchLength - 1; index >= 0; index--)
            {
                var value = buffer[index];
                if (value == 0x0A)
                {
                    lineBreakStart = index > 0 && buffer[index - 1] == 0x0D ? index - 1 : index;
                    lineBreakLength = index > 0 && buffer[index - 1] == 0x0D ? 2 : 1;
                    return true;
                }

                if (value == 0x0D)
                {
                    lineBreakStart = index;
                    lineBreakLength = 1;
                    return true;
                }
            }
        }
        else
        {
            for (var index = searchLength - encodingInfo.CodeUnitSize; index >= 0; index -= encodingInfo.CodeUnitSize)
            {
                var value = ReadCodeUnit(buffer, index, encodingInfo.Kind);
                if (value == '\n')
                {
                    if (index >= encodingInfo.CodeUnitSize &&
                        ReadCodeUnit(buffer, index - encodingInfo.CodeUnitSize, encodingInfo.Kind) == '\r')
                    {
                        lineBreakStart = index - encodingInfo.CodeUnitSize;
                        lineBreakLength = encodingInfo.CodeUnitSize * 2;
                        return true;
                    }

                    lineBreakStart = index;
                    lineBreakLength = encodingInfo.CodeUnitSize;
                    return true;
                }

                if (value == '\r')
                {
                    lineBreakStart = index;
                    lineBreakLength = encodingInfo.CodeUnitSize;
                    return true;
                }
            }
        }

        lineBreakStart = -1;
        lineBreakLength = 0;
        return false;
    }

    private static int ReadCodeUnit(byte[] buffer, int index, TailEncodingKind encodingKind)
    {
        return encodingKind == TailEncodingKind.Utf16LittleEndian
            ? buffer[index] | (buffer[index + 1] << 8)
            : (buffer[index] << 8) | buffer[index + 1];
    }

    private static async Task ReadExactlyAsync(FileStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading log tail.");
            }

            offset += bytesRead;
        }
    }

    private static byte[] Combine(byte[] first, byte[] second)
    {
        if (first.Length == 0)
        {
            return second;
        }

        if (second.Length == 0)
        {
            return first;
        }

        var combined = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
        return combined;
    }

    private static byte[] CopySegment(byte[] buffer, int start, int length)
    {
        if (length <= 0)
        {
            return [];
        }

        var result = new byte[length];
        Buffer.BlockCopy(buffer, start, result, 0, length);
        return result;
    }

    private enum TailEncodingKind
    {
        SingleByte,
        Utf16LittleEndian,
        Utf16BigEndian
    }

    private readonly record struct TailEncodingInfo(
        Encoding Encoding,
        int PreambleLength,
        int CodeUnitSize,
        TailEncodingKind Kind);

    private readonly record struct WatchContext(string Path, string DirectoryPath, string FileName);

    private sealed class WatcherUnavailableException(string path, Exception innerException)
        : IOException($"File watcher unavailable for '{path}'.", innerException);
}

public readonly record struct TailReadResult(IReadOnlyList<string> Lines, long EndPosition);
