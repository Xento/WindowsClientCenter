using System.Globalization;
using System.Text.Json;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Services.UsoStore;

public sealed class TimestampParser
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "O"
    ];

    public DateTime? ParseUnixMillisecondsToLocal(string? rawValue)
    {
        var value = ParseInteger(rawValue);
        if (!value.HasValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value.Value).ToLocalTime().DateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public DateTime? ParseFlexibleDateTime(string? rawValue, int? typeHint = null)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        if (typeHint == 3)
        {
            return ParseUnixMillisecondsToLocal(rawValue);
        }

        if (LooksLikeUnixMilliseconds(rawValue))
        {
            var unixValue = ParseUnixMillisecondsToLocal(rawValue);
            if (unixValue.HasValue)
            {
                return unixValue;
            }
        }

        if (DateTimeOffset.TryParseExact(rawValue, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var offset))
        {
            return offset.LocalDateTime;
        }

        if (DateTimeOffset.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out offset))
        {
            return offset.LocalDateTime;
        }

        return null;
    }

    public long? ParseInteger(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public bool? ParseBoolean(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return rawValue.Trim() switch
        {
            "1" => true,
            "0" => false,
            var text when bool.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    public string TypeToLabel(int type)
    {
        return type switch
        {
            0 => "Boolean / flag",
            2 => "Integer",
            3 => "Unix epoch milliseconds",
            4 => "String / enum / JSON",
            1 => "Integer / result code",
            _ => $"Type {type.ToString(CultureInfo.InvariantCulture)}"
        };
    }

    public string FormatParsedValue(string? rawValue, int type)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return string.Empty;
        }

        return type switch
        {
            0 => ParseBoolean(rawValue) switch
            {
                true => "True",
                false => "False",
                _ => rawValue
            },
            3 => FormatDateTime(ParseUnixMillisecondsToLocal(rawValue)),
            2 or 1 => ParseInteger(rawValue)?.ToString("N0", CultureInfo.InvariantCulture) ?? rawValue,
            _ => rawValue
        };
    }

    public string FormatVariableParsedValue(string key, string? rawValue, int type)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return string.Empty;
        }

        if (string.Equals(key, "UXRebootRecognitionTimeHistory", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "UXRebootTimeHistory", StringComparison.OrdinalIgnoreCase))
        {
            var values = ParseSerializedDateTimeArray(rawValue)
                .Select(FormatDateTime)
                .ToArray();
            return values.Length == 0 ? rawValue : string.Join(" | ", values);
        }

        return FormatParsedValue(rawValue, type);
    }

    public IReadOnlyList<string> ParseSerializedStringArray(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return [];
        }

        if (TryParseJsonArray(rawValue, out var elements))
        {
            return elements
                .Select(element => element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString() ?? string.Empty,
                    JsonValueKind.Number => element.ToString(),
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    _ => element.ToString()
                })
                .ToArray();
        }

        return rawValue
            .Trim('[', ']')
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim('"'))
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
    }

    public IReadOnlyList<DateTime?> ParseSerializedDateTimeArray(string? rawValue)
    {
        return ParseSerializedStringArray(rawValue)
            .Select(item => ParseFlexibleDateTime(item))
            .ToArray();
    }

    public string FormatDateTime(DateTime? value)
    {
        return value?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "Unknown";
    }

    public VariableRecord CreateVariableRecord(string key, string rawValue, int type)
    {
        return new VariableRecord
        {
            Key = key,
            RawValue = rawValue,
            Type = type,
            TypeLabel = TypeToLabel(type),
            ParsedValue = FormatVariableParsedValue(key, rawValue, type),
            ParsedInteger = type is 1 or 2 or 3 ? ParseInteger(rawValue) : null,
            ParsedBoolean = type == 0 ? ParseBoolean(rawValue) : null,
            ParsedDateTimeLocal = type == 3 ? ParseUnixMillisecondsToLocal(rawValue) : ParseFlexibleDateTime(rawValue, type)
        };
    }

    private static bool TryParseJsonArray(string rawValue, out IReadOnlyList<JsonElement> elements)
    {
        elements = [];

        try
        {
            using var document = JsonDocument.Parse(rawValue);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            elements = document.RootElement
                .EnumerateArray()
                .Select(static element => element.Clone())
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeUnixMilliseconds(string rawValue)
    {
        if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        return parsed > 1_000_000_000_000 && parsed < 9_999_999_999_999;
    }
}
