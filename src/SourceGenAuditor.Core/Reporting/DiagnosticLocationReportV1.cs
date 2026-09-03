using System.Text.Json;
using System.Text.Json.Serialization;
using SourceGenAuditor.Core.Canonicalization;

namespace SourceGenAuditor.Core.Reporting;

public sealed class UnmappedPathValueV1
{
    private UnmappedPathValueV1(string kind, string token)
    {
        Kind = kind;
        Token = token;
    }

    [JsonPropertyName("kind")]
    [JsonPropertyOrder(0)]
    public string Kind { get; }

    [JsonPropertyName("token")]
    [JsonPropertyOrder(1)]
    public string Token { get; }

    internal static UnmappedPathValueV1 From(UnmappedPathValue value) => new(value.Kind switch
    {
        CanonicalPathKind.Controlled => "Controlled",
        CanonicalPathKind.Generated => "Generated",
        CanonicalPathKind.External => "External",
        _ => throw new CanonicalizationException("An unmapped report path has an invalid path kind."),
    }, value.Token);

}

public sealed class MappedPathPayloadV1
{
    private MappedPathPayloadV1(string kind, string token)
    {
        Kind = kind;
        Token = token;
    }

    [JsonPropertyName("kind")]
    [JsonPropertyOrder(0)]
    public string Kind { get; }

    [JsonPropertyName("token")]
    [JsonPropertyOrder(1)]
    public string Token { get; }

    internal static MappedPathPayloadV1 From(MappedPathPayload value) => new(value.Kind switch
    {
        CanonicalPathKind.Empty => "Empty",
        CanonicalPathKind.External => "External",
        _ => throw new CanonicalizationException("A mapped report path has an invalid path kind."),
    }, value.Token);
}

public sealed class MappedPathV1
{
    private MappedPathV1(bool hasMappedPath, MappedPathPayloadV1? value)
    {
        HasMappedPath = hasMappedPath;
        Value = value;
    }

    [JsonPropertyName("hasMappedPath")]
    [JsonPropertyOrder(0)]
    public bool HasMappedPath { get; }

    [JsonPropertyName("value")]
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MappedPathPayloadV1? Value { get; }

    internal static MappedPathV1 From(MappedPathValue value) => value switch
    {
        MappedPathValue.Unmapped => new MappedPathV1(false, null),
        MappedPathValue.Mapped mapped => new MappedPathV1(true, MappedPathPayloadV1.From(mapped.Path)),
        _ => throw new CanonicalizationException("An unknown mapped-path state was observed while creating report V1."),
    };
}

[JsonConverter(typeof(LocationV1JsonConverter))]
public sealed class LocationV1
{
    private LocationV1()
    {
        Kind = "None";
    }

    private LocationV1(CanonicalSourceLocation value)
    {
        Kind = "SourceFile";
        UnmappedPath = UnmappedPathValueV1.From(value.UnmappedPath);
        Utf16SpanStart = value.Utf16SpanStart;
        Utf16SpanLength = value.Utf16SpanLength;
        MappedPath = MappedPathV1.From(value.MappedPath);
        MappedStartLine = value.MappedStartLine;
        MappedStartColumn = value.MappedStartColumn;
        MappedEndLine = value.MappedEndLine;
        MappedEndColumn = value.MappedEndColumn;
        LineVisibility = value.LineVisibility switch
        {
            CanonicalLineVisibility.Visible => "Visible",
            CanonicalLineVisibility.Hidden => "Hidden",
            CanonicalLineVisibility.BeforeFirstLineDirective => "BeforeFirstLineDirective",
            _ => throw new CanonicalizationException("An unknown line-visibility value was observed while creating report V1."),
        };
    }

    [JsonPropertyName("kind")]
    [JsonPropertyOrder(0)]
    public string Kind { get; }

    [JsonPropertyName("unmappedPath")]
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UnmappedPathValueV1? UnmappedPath { get; }

    [JsonPropertyName("utf16SpanStart")]
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Utf16SpanStart { get; }

    [JsonPropertyName("utf16SpanLength")]
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Utf16SpanLength { get; }

    [JsonPropertyName("mappedPath")]
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MappedPathV1? MappedPath { get; }

    [JsonPropertyName("mappedStartLine")]
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? MappedStartLine { get; }

    [JsonPropertyName("mappedStartColumn")]
    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? MappedStartColumn { get; }

    [JsonPropertyName("mappedEndLine")]
    [JsonPropertyOrder(7)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? MappedEndLine { get; }

    [JsonPropertyName("mappedEndColumn")]
    [JsonPropertyOrder(8)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? MappedEndColumn { get; }

    [JsonPropertyName("lineVisibility")]
    [JsonPropertyOrder(9)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LineVisibility { get; }

    public static LocationV1 None { get; } = new();

    public static LocationV1 FromCanonical(CanonicalSourceLocation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LocationV1(value);
    }
}

public sealed class LocationV1JsonConverter : JsonConverter<LocationV1>
{
    public override LocationV1 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out JsonElement kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("LocationV1 requires an object kind.");
        }

        string kind = kindElement.GetString()!;
        if (kind == "None")
        {
            RequireProperties(root, "kind");
            return LocationV1.None;
        }

        if (kind != "SourceFile")
        {
            throw new JsonException("LocationV1 has an unsupported kind.");
        }

        RequireProperties(
            root,
            "kind",
            "unmappedPath",
            "utf16SpanStart",
            "utf16SpanLength",
            "mappedPath",
            "mappedStartLine",
            "mappedStartColumn",
            "mappedEndLine",
            "mappedEndColumn",
            "lineVisibility");
        UnmappedPathValue unmapped = ReadUnmapped(root.GetProperty("unmappedPath"));
        MappedPathValue mapped = ReadMapped(root.GetProperty("mappedPath"));
        CanonicalLineVisibility visibility = root.GetProperty("lineVisibility").GetString() switch
        {
            "Visible" => CanonicalLineVisibility.Visible,
            "Hidden" => CanonicalLineVisibility.Hidden,
            "BeforeFirstLineDirective" => CanonicalLineVisibility.BeforeFirstLineDirective,
            _ => throw new JsonException("LocationV1 has an unsupported lineVisibility."),
        };
        CanonicalSourceLocation source = new(
            unmapped,
            ReadUInt64(root, "utf16SpanStart"),
            ReadUInt64(root, "utf16SpanLength"),
            mapped,
            ReadUInt64(root, "mappedStartLine"),
            ReadUInt64(root, "mappedStartColumn"),
            ReadUInt64(root, "mappedEndLine"),
            ReadUInt64(root, "mappedEndColumn"),
            visibility);
        return LocationV1.FromCanonical(source);
    }

    public override void Write(Utf8JsonWriter writer, LocationV1 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        if (value.Kind == "SourceFile")
        {
            writer.WritePropertyName("unmappedPath");
            JsonSerializer.Serialize(writer, value.UnmappedPath, options);
            writer.WriteNumber("utf16SpanStart", value.Utf16SpanStart!.Value);
            writer.WriteNumber("utf16SpanLength", value.Utf16SpanLength!.Value);
            writer.WritePropertyName("mappedPath");
            JsonSerializer.Serialize(writer, value.MappedPath, options);
            writer.WriteNumber("mappedStartLine", value.MappedStartLine!.Value);
            writer.WriteNumber("mappedStartColumn", value.MappedStartColumn!.Value);
            writer.WriteNumber("mappedEndLine", value.MappedEndLine!.Value);
            writer.WriteNumber("mappedEndColumn", value.MappedEndColumn!.Value);
            writer.WriteString("lineVisibility", value.LineVisibility);
        }

        writer.WriteEndObject();
    }

    private static UnmappedPathValue ReadUnmapped(JsonElement element)
    {
        RequireProperties(element, "kind", "token");
        string kind = element.GetProperty("kind").GetString() ?? throw new JsonException("Unmapped path kind is missing.");
        string token = element.GetProperty("token").GetString() ?? throw new JsonException("Unmapped path token is missing.");
        try
        {
            return kind switch
            {
                "Controlled" => UnmappedPathValue.Controlled(token),
                "Generated" when token.StartsWith("generated:", StringComparison.Ordinal) => UnmappedPathValue.Generated(token[10..]),
                "External" when token.StartsWith("external:", StringComparison.Ordinal) => UnmappedPathValue.External(token[9..]),
                _ => throw new JsonException("Unmapped path kind or token is invalid."),
            };
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Unmapped path token is invalid.", exception);
        }
    }

    private static MappedPathValue ReadMapped(JsonElement element)
    {
        if (!element.TryGetProperty("hasMappedPath", out JsonElement state) || state.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException("MappedPathV1 requires hasMappedPath.");
        }

        if (!state.GetBoolean())
        {
            RequireProperties(element, "hasMappedPath");
            return new MappedPathValue.Unmapped();
        }

        RequireProperties(element, "hasMappedPath", "value");
        JsonElement payload = element.GetProperty("value");
        RequireProperties(payload, "kind", "token");
        string kind = payload.GetProperty("kind").GetString() ?? throw new JsonException("Mapped path kind is missing.");
        string token = payload.GetProperty("token").GetString() ?? throw new JsonException("Mapped path token is missing.");
        try
        {
            MappedPathPayload mappedPayload = kind switch
            {
                "Empty" when token.Length == 0 => MappedPathPayload.Empty,
                "External" when token.StartsWith("external:", StringComparison.Ordinal) => MappedPathPayload.External(token[9..]),
                _ => throw new JsonException("Mapped path kind or token is invalid."),
            };
            return new MappedPathValue.Mapped(mappedPayload);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Mapped path token is invalid.", exception);
        }
    }

    private static ulong ReadUInt64(JsonElement element, string property)
    {
        if (!element.GetProperty(property).TryGetUInt64(out ulong value) || value > 9_007_199_254_740_991)
        {
            throw new JsonException($"LocationV1 {property} is outside the protocol integer range.");
        }

        return value;
    }

    private static void RequireProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A location value is not an object.");
        }

        string[] actual = element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        string[] wanted = expected.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal))
        {
            throw new JsonException("A location value has missing or unknown properties.");
        }
    }
}

public static class ReportV1Json
{
    public static byte[] SerializeLocation(LocationV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToUtf8Bytes(value);
    }
}
