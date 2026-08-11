using System.Text.RegularExpressions;
using WindowsClientCenter.Shared.Diagnostics;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models;

public sealed partial record InstallProgressEntry(string Text, string ErrorCode, string ErrorDescription)
{
    public bool HasErrorDescription => !string.IsNullOrWhiteSpace(ErrorDescription);

    public static InstallProgressEntry FromLogLine(string line)
    {
        var code = ExtractErrorCode(line);
        if (string.IsNullOrWhiteSpace(code))
        {
            return new InstallProgressEntry(line, string.Empty, string.Empty);
        }

        return new InstallProgressEntry(
            line,
            ErrorCodeResolver.Normalize(code),
            ErrorCodeResolver.ResolveDescription(code));
    }

    private static string ExtractErrorCode(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var hresultMatch = HResultRegex().Match(line);
        if (hresultMatch.Success)
        {
            return hresultMatch.Groups["code"].Value;
        }

        var hexMatch = HexCodeRegex().Match(line);
        return hexMatch.Success
            ? hexMatch.Value
            : string.Empty;
    }

    [GeneratedRegex(@"HResult=(?<code>-?\d+|0x[0-9A-Fa-f]{1,8})", RegexOptions.CultureInvariant)]
    private static partial Regex HResultRegex();

    [GeneratedRegex(@"0x[0-9A-Fa-f]{8}", RegexOptions.CultureInvariant)]
    private static partial Regex HexCodeRegex();
}
