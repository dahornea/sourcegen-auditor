namespace SourceGenAuditor.Core.Canonicalization;

public enum CanonicalPathKind
{
    Controlled,
    Generated,
    External,
    Empty,
}

public sealed record UnmappedPathValue
{
    private UnmappedPathValue(CanonicalPathKind kind, string token)
    {
        Kind = kind;
        Token = token;
    }

    public CanonicalPathKind Kind { get; }

    public string Token { get; }

    public static UnmappedPathValue Controlled(string logicalPath)
    {
        ArgumentNullException.ThrowIfNull(logicalPath);
        string normalized = logicalPath.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalized) || IsDriveRooted(normalized) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(
                "A controlled logical path is empty, rooted, or contains an invalid segment.",
                nameof(logicalPath));
        }

        _ = CanonicalWriter.Encode(normalized);
        return new UnmappedPathValue(CanonicalPathKind.Controlled, normalized);
    }

    public static UnmappedPathValue Generated(string observableHintName)
    {
        ArgumentNullException.ThrowIfNull(observableHintName);
        ValidateNonEmpty(observableHintName, "A generated-source hint name is empty.");
        return new UnmappedPathValue(CanonicalPathKind.Generated, $"generated:{observableHintName}");
    }

    public static UnmappedPathValue External(string lowercaseSha256)
    {
        ValidateLowercaseSha256(lowercaseSha256);
        return new UnmappedPathValue(CanonicalPathKind.External, $"external:{lowercaseSha256}");
    }

    internal static void ValidateLowercaseSha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("An external path identity requires exactly 64 lowercase hexadecimal SHA-256 characters.", nameof(value));
        }
    }

    private static void ValidateNonEmpty(string value, string message)
    {
        if (value.Length == 0)
        {
            throw new ArgumentException(message, nameof(value));
        }

        _ = CanonicalWriter.Encode(value);
    }

    private static bool IsDriveRooted(string value)
        => value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';
}

public sealed record MappedPathPayload
{
    private MappedPathPayload(CanonicalPathKind kind, string token)
    {
        Kind = kind;
        Token = token;
    }

    public CanonicalPathKind Kind { get; }

    public string Token { get; }

    public static MappedPathPayload Empty { get; } = new(CanonicalPathKind.Empty, string.Empty);

    public static MappedPathPayload External(string lowercaseSha256)
    {
        UnmappedPathValue.ValidateLowercaseSha256(lowercaseSha256);
        return new MappedPathPayload(CanonicalPathKind.External, $"external:{lowercaseSha256}");
    }
}

public abstract record MappedPathValue
{
    private MappedPathValue()
    {
    }

    public sealed record Unmapped : MappedPathValue;

    public sealed record Mapped : MappedPathValue
    {
        public Mapped(MappedPathPayload path)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public MappedPathPayload Path { get; }
    }
}

public enum CanonicalLineVisibility
{
    Visible,
    Hidden,
    BeforeFirstLineDirective,
}

public sealed record CanonicalSourceLocation
{
    public CanonicalSourceLocation(
        UnmappedPathValue unmappedPath,
        ulong utf16SpanStart,
        ulong utf16SpanLength,
        MappedPathValue mappedPath,
        ulong mappedStartLine,
        ulong mappedStartColumn,
        ulong mappedEndLine,
        ulong mappedEndColumn,
        CanonicalLineVisibility lineVisibility)
    {
        if (!Enum.IsDefined(lineVisibility))
        {
            throw new ArgumentOutOfRangeException(nameof(lineVisibility));
        }

        UnmappedPath = unmappedPath ?? throw new ArgumentNullException(nameof(unmappedPath));
        Utf16SpanStart = utf16SpanStart;
        Utf16SpanLength = utf16SpanLength;
        MappedPath = mappedPath ?? throw new ArgumentNullException(nameof(mappedPath));
        MappedStartLine = mappedStartLine;
        MappedStartColumn = mappedStartColumn;
        MappedEndLine = mappedEndLine;
        MappedEndColumn = mappedEndColumn;
        LineVisibility = lineVisibility;
    }

    public UnmappedPathValue UnmappedPath { get; }

    public ulong Utf16SpanStart { get; }

    public ulong Utf16SpanLength { get; }

    public MappedPathValue MappedPath { get; }

    public ulong MappedStartLine { get; }

    public ulong MappedStartColumn { get; }

    public ulong MappedEndLine { get; }

    public ulong MappedEndColumn { get; }

    public CanonicalLineVisibility LineVisibility { get; }
}
