using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsClientCenter.Intune.Services.Models;

public sealed class WindowsUpdateInstallMonitorSnapshot
{
    public string TaskStatus { get; set; } = string.Empty;
    public string TaskLastResult { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string CurrentTitle { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int CompletedCount { get; set; }
    public int InstalledCount { get; set; }
    public int FailedCount { get; set; }
    public bool RebootRequired { get; set; }
    public string LastUpdatedUtc { get; set; } = string.Empty;
    [JsonConverter(typeof(ProgressLineArrayJsonConverter))]
    public string[] ProgressLines { get; set; } = [];
    public long ProgressCursor { get; set; } = -1;

    private sealed class ProgressLineArrayJsonConverter : JsonConverter<string[]>
    {
        public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return [];
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                return [ReadSingleValue(ref reader)];
            }

            var lines = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return lines.ToArray();
                }

                var line = ReadSingleValue(ref reader);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }

            throw new JsonException("Unexpected end of JSON while reading progress lines.");
        }

        public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }

        private static string ReadSingleValue(ref Utf8JsonReader reader)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString() ?? string.Empty,
                JsonTokenType.Number => JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
                JsonTokenType.True => bool.TrueString,
                JsonTokenType.False => bool.FalseString,
                JsonTokenType.Null => string.Empty,
                JsonTokenType.StartObject or JsonTokenType.StartArray => JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
                _ => throw new JsonException($"Unsupported progress line token: {reader.TokenType}")
            };
        }
    }
}
