using System.Text;

namespace WindowsClientCenter.Shared.Diagnostics;

public static class SharedTextFileReader
{
    private const int DefaultBufferSize = 16 * 1024;

    public static FileStream OpenReadShared(string path, FileOptions options = FileOptions.Asynchronous)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = options,
                BufferSize = DefaultBufferSize
            });
    }

    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenReadShared(path, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
