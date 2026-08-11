using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WindowsClientCenter.Shared.Diagnostics;

public static partial class ErrorCodeResolver
{
    private const string UnknownErrorText = "Unknown error code.";

    public static string Normalize(string? rawCode)
    {
        return TryParse(rawCode, out var numericCode)
            ? $"0x{numericCode:X8}"
            : rawCode?.Trim() ?? string.Empty;
    }

    public static ResolvedErrorCode? Lookup(string? rawCode)
    {
        if (!TryParse(rawCode, out var numericCode))
        {
            return null;
        }

        var descriptions = new List<string>();
        ErrorCatalogEntry? catalogEntry = null;

        if (TryGetCatalogEntry(numericCode, out var entry))
        {
            catalogEntry = entry;
            descriptions.Add(entry.Description);
        }

        AppendRuntimeDescriptions(numericCode, descriptions);

        if (catalogEntry is null)
        {
            if (descriptions.Count == 0)
            {
                return null;
            }

            var distinctRuntime = DistinctDescriptions(descriptions);
            return new ResolvedErrorCode(
                $"0x{numericCode:X8}",
                unchecked((int)numericCode),
                null,
                distinctRuntime[0],
                ErrorCodeCategory.Windows,
                distinctRuntime.Count > 1 ? ErrorCodeSource.RuntimeHResult : ErrorCodeSource.RuntimeWin32,
                ErrorCodeConfidence.Runtime,
                distinctRuntime);
        }

        var distinctDescriptions = DistinctDescriptions(descriptions);
        return new ResolvedErrorCode(
            $"0x{numericCode:X8}",
            unchecked((int)numericCode),
            catalogEntry.Value.Symbol,
            distinctDescriptions[0],
            catalogEntry.Value.Category,
            catalogEntry.Value.Source,
            catalogEntry.Value.Confidence,
            distinctDescriptions);
    }

    public static string ResolveDescription(string? rawCode)
    {
        var resolved = Lookup(rawCode);
        return resolved is null
            ? string.Empty
            : string.Join(Environment.NewLine, resolved.Descriptions);
    }

    public static IReadOnlyList<string> ResolveDescriptions(string? rawCode)
    {
        return Lookup(rawCode)?.Descriptions ?? [];
    }

    public static string FormatForDisplay(string? rawCode)
    {
        var resolved = Lookup(rawCode);
        if (resolved is null)
        {
            return Normalize(rawCode);
        }

        return string.IsNullOrWhiteSpace(resolved.Symbol)
            ? $"{resolved.NormalizedCode} - {resolved.Description}"
            : $"{resolved.NormalizedCode} - {resolved.Symbol} - {resolved.Description}";
    }

    public static IReadOnlyList<DetectedErrorCode> FindInText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var results = new List<DetectedErrorCode>();
        foreach (Match match in EmbeddedHexCodeRegex().Matches(text))
        {
            var code = match.Value;
            var resolved = Lookup(code);
            if (resolved is null)
            {
                continue;
            }

            results.Add(new DetectedErrorCode(match.Index, match.Length, code, resolved));
        }

        return results;
    }

    private static bool TryGetCatalogEntry(uint code, out ErrorCatalogEntry entry)
    {
        if (ErrorCatalog.TryGetEntry(code, out entry))
        {
            return true;
        }

        if ((code & 0xFFFF0000u) == 0x80070000u)
        {
            var win32Code = code & 0x0000FFFFu;
            if (ErrorCatalog.TryGetEntry(win32Code, out entry))
            {
                return true;
            }
        }

        if (code <= ushort.MaxValue)
        {
            var hresult = 0x80070000u | code;
            if (ErrorCatalog.TryGetEntry(hresult, out entry))
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendRuntimeDescriptions(uint numericCode, List<string> descriptions)
    {
        if (numericCode <= ushort.MaxValue)
        {
            var win32Message = TryResolveWin32Message(unchecked((int)numericCode));
            if (!string.IsNullOrWhiteSpace(win32Message))
            {
                descriptions.Add(win32Message);
            }
        }

        if ((numericCode & 0xFFFF0000u) == 0x80070000u)
        {
            var win32Message = TryResolveWin32Message(unchecked((int)(numericCode & 0x0000FFFFu)));
            if (!string.IsNullOrWhiteSpace(win32Message))
            {
                descriptions.Add(win32Message);
            }
        }

        var hresultMessage = TryResolveHResultMessage(unchecked((int)numericCode));
        if (!string.IsNullOrWhiteSpace(hresultMessage))
        {
            descriptions.Add(hresultMessage);
        }
    }

    private static IReadOnlyList<string> DistinctDescriptions(IEnumerable<string> descriptions)
    {
        var distinct = descriptions
            .Where(description => !string.IsNullOrWhiteSpace(description))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return distinct.Length == 0
            ? [UnknownErrorText]
            : distinct;
    }

    private static bool TryParse(string? rawCode, out uint numericCode)
    {
        numericCode = 0;
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return false;
        }

        var trimmed = rawCode.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber, null, out numericCode);
        }

        if (int.TryParse(trimmed, out var signedValue))
        {
            numericCode = unchecked((uint)signedValue);
            return true;
        }

        if (uint.TryParse(trimmed, out numericCode))
        {
            return true;
        }

        return uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out numericCode);
    }

    private static string TryResolveWin32Message(int win32Code)
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            var message = new Win32Exception(win32Code).Message?.Trim();
            return string.IsNullOrWhiteSpace(message) || string.Equals(message, UnknownErrorText, StringComparison.Ordinal)
                ? string.Empty
                : message;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryResolveHResultMessage(int hresult)
    {
        try
        {
            var message = Marshal.GetExceptionForHR(hresult)?.Message?.Trim();
            return IsGenericHResultMessage(message, hresult)
                ? string.Empty
                : message ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsGenericHResultMessage(string? message, int hresult)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        return message.StartsWith("Exception from HRESULT", StringComparison.OrdinalIgnoreCase) ||
               message.Contains($"0x{unchecked((uint)hresult):X8}", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"0[xX][0-9A-Fa-f]{1,8}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedHexCodeRegex();
}
