using System.Globalization;
using WindowsClientCenter.Intune.Services.Runtime;

namespace WindowsClientCenter.Plugins.PowerShellScripts;

public enum PowerShellScriptExecutionMode
{
    RemotingWindow,
    DirectComputerName,
    PromptForComputerNameParameters,
    Unsupported
}

public enum PowerShellScriptParameterKind
{
    String,
    Char,
    Boolean,
    Switch,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Single,
    Double,
    Decimal,
    Guid,
    DateTime,
    TimeSpan,
    Enum
}

public sealed record PowerShellScriptParameterDefinition(
    string Name,
    string TargetTypeName,
    string DisplayTypeName,
    PowerShellScriptParameterKind Kind,
    bool IsArray = false,
    bool IsNullable = false,
    IReadOnlyList<string>? EnumValues = null);

public sealed record PowerShellScriptCatalogEntry(
    string ItemId,
    string DisplayName,
    string RelativePath,
    string FullPath,
    PowerShellScriptExecutionMode ExecutionMode,
    IReadOnlyList<PowerShellScriptParameterDefinition> RequiredParameters,
    string? ErrorMessage = null);

public interface IPowerShellScriptMetadataProvider
{
    ValueTask<IReadOnlyList<PowerShellScriptCatalogEntry>> LoadAsync(string scriptDirectory, CancellationToken cancellationToken);
}

public interface IPowerShellScriptLauncher
{
    ValueTask<WindowsClientCenter.Plugin.Abstractions.Models.PluginActionResult> LaunchAsync(
        string host,
        PowerShellScriptCatalogEntry script,
        IReadOnlyDictionary<string, string> parameterLiterals,
        IPowerShellExecutor? executor,
        CancellationToken cancellationToken);
}

internal static class PowerShellScriptLiteralBuilder
{
    public static bool TryCreateLiteral(
        PowerShellScriptParameterDefinition definition,
        IReadOnlyList<string> rawValues,
        out string literal,
        out string? error)
    {
        if (definition.IsArray)
        {
            if (rawValues.Count == 0)
            {
                literal = "@()";
                error = null;
                return true;
            }

            var items = new List<string>(rawValues.Count);
            foreach (var rawValue in rawValues)
            {
                if (!TryCreateScalarLiteral(definition, rawValue, out var itemLiteral, out error))
                {
                    literal = string.Empty;
                    return false;
                }

                items.Add(itemLiteral);
            }

            literal = $"@({string.Join(", ", items)})";
            error = null;
            return true;
        }

        var value = rawValues.FirstOrDefault() ?? string.Empty;
        return TryCreateScalarLiteral(definition, value, out literal, out error);
    }

    public static string CreateStringLiteral(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static bool TryCreateScalarLiteral(
        PowerShellScriptParameterDefinition definition,
        string rawValue,
        out string literal,
        out string? error)
    {
        error = null;
        switch (definition.Kind)
        {
            case PowerShellScriptParameterKind.String:
                literal = CreateStringLiteral(rawValue);
                return true;
            case PowerShellScriptParameterKind.Char:
                if (rawValue.Length != 1)
                {
                    literal = string.Empty;
                    error = $"Parameter '{definition.Name}' expects exactly one character.";
                    return false;
                }

                literal = CreateStringLiteral(rawValue);
                return true;
            case PowerShellScriptParameterKind.Boolean:
            case PowerShellScriptParameterKind.Switch:
                if (!bool.TryParse(rawValue, out var boolValue))
                {
                    literal = string.Empty;
                    error = $"Parameter '{definition.Name}' expects 'True' or 'False'.";
                    return false;
                }

                literal = boolValue ? "$true" : "$false";
                return true;
            case PowerShellScriptParameterKind.Int16:
                return TryCreateNumericLiteral<short>(definition.Name, rawValue, short.TryParse, out literal, out error);
            case PowerShellScriptParameterKind.UInt16:
                return TryCreateNumericLiteral<ushort>(definition.Name, rawValue, ushort.TryParse, out literal, out error);
            case PowerShellScriptParameterKind.Int32:
                return TryCreateNumericLiteral<int>(definition.Name, rawValue, int.TryParse, out literal, out error);
            case PowerShellScriptParameterKind.UInt32:
                return TryCreateNumericLiteral<uint>(definition.Name, rawValue, uint.TryParse, out literal, out error);
            case PowerShellScriptParameterKind.Int64:
                return TryCreateNumericLiteral<long>(definition.Name, rawValue, long.TryParse, out literal, out error);
            case PowerShellScriptParameterKind.UInt64:
                return TryCreateNumericLiteral<ulong>(definition.Name, rawValue, ulong.TryParse, out literal, out error);
            case PowerShellScriptParameterKind.Single:
                return TryCreateFloatingLiteral<float>(definition.Name, rawValue, float.TryParse, out literal, out error);
            case PowerShellScriptParameterKind.Double:
                return TryCreateFloatingLiteral<double>(definition.Name, rawValue, double.TryParse, out literal, out error);
            case PowerShellScriptParameterKind.Decimal:
                return TryCreateFloatingLiteral<decimal>(definition.Name, rawValue, decimal.TryParse, out literal, out error);
            case PowerShellScriptParameterKind.Guid:
                if (!System.Guid.TryParse(rawValue, out var guidValue))
                {
                    literal = string.Empty;
                    error = $"Parameter '{definition.Name}' expects a GUID value.";
                    return false;
                }

                literal = CreateStringLiteral(guidValue.ToString());
                return true;
            case PowerShellScriptParameterKind.DateTime:
                if (!System.DateTime.TryParse(rawValue, CultureInfo.CurrentCulture, DateTimeStyles.RoundtripKind, out var dateTimeValue))
                {
                    literal = string.Empty;
                    error = $"Parameter '{definition.Name}' expects a valid date/time value.";
                    return false;
                }

                literal = CreateStringLiteral(dateTimeValue.ToString("o", CultureInfo.InvariantCulture));
                return true;
            case PowerShellScriptParameterKind.TimeSpan:
                if (!System.TimeSpan.TryParse(rawValue, CultureInfo.CurrentCulture, out var timeSpanValue))
                {
                    literal = string.Empty;
                    error = $"Parameter '{definition.Name}' expects a valid time span value.";
                    return false;
                }

                literal = CreateStringLiteral(timeSpanValue.ToString("c", CultureInfo.InvariantCulture));
                return true;
            case PowerShellScriptParameterKind.Enum:
                if (definition.EnumValues is not { Count: > 0 } ||
                    !definition.EnumValues.Any(value => value.Equals(rawValue, StringComparison.OrdinalIgnoreCase)))
                {
                    literal = string.Empty;
                    error = $"Parameter '{definition.Name}' expects one of: {string.Join(", ", definition.EnumValues ?? [])}.";
                    return false;
                }

                literal = CreateStringLiteral(rawValue);
                return true;
            default:
                literal = string.Empty;
                error = $"Parameter '{definition.Name}' is not supported.";
                return false;
        }
    }

    private static bool TryCreateNumericLiteral<T>(
        string parameterName,
        string rawValue,
        TryParseDelegate<T> tryParse,
        out string literal,
        out string? error)
        where T : struct, ISpanFormattable
    {
        if (!tryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
        {
            literal = string.Empty;
            error = $"Parameter '{parameterName}' expects an integer value.";
            return false;
        }

        literal = parsedValue.ToString(null, CultureInfo.InvariantCulture);
        error = null;
        return true;
    }

    private static bool TryCreateFloatingLiteral<T>(
        string parameterName,
        string rawValue,
        TryParseDelegate<T> tryParse,
        out string literal,
        out string? error)
        where T : struct, ISpanFormattable
    {
        if (!tryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsedValue))
        {
            literal = string.Empty;
            error = $"Parameter '{parameterName}' expects a numeric value.";
            return false;
        }

        literal = parsedValue.ToString(null, CultureInfo.InvariantCulture);
        error = null;
        return true;
    }

    private delegate bool TryParseDelegate<T>(string s, NumberStyles style, IFormatProvider? provider, out T result);
}
