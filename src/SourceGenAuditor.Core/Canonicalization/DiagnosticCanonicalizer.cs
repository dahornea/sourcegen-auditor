using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SourceGenAuditor.Core.Canonicalization;

public sealed class CanonicalPathContext
{
    private readonly IReadOnlyDictionary<SyntaxTree, string> controlledTrees;
    private readonly IReadOnlyDictionary<SyntaxTree, string> generatedTrees;

    public CanonicalPathContext(
        IReadOnlyDictionary<SyntaxTree, string>? controlledTrees = null,
        IReadOnlyDictionary<SyntaxTree, string>? generatedTrees = null)
    {
        this.controlledTrees = controlledTrees ?? new Dictionary<SyntaxTree, string>(ReferenceEqualityComparer.Instance);
        this.generatedTrees = generatedTrees ?? new Dictionary<SyntaxTree, string>(ReferenceEqualityComparer.Instance);
        if (this.controlledTrees.Keys.Any(tree => this.generatedTrees.ContainsKey(tree)))
        {
            throw new CanonicalizationException("A syntax tree cannot be both controlled and generated.");
        }
    }

    public UnmappedPathValue ResolveUnmapped(SyntaxTree? tree, string path)
    {
        if (tree is not null && controlledTrees.TryGetValue(tree, out string? logicalPath))
        {
            try
            {
                return UnmappedPathValue.Controlled(logicalPath);
            }
            catch (ArgumentException exception)
            {
                throw new CanonicalizationException("A controlled logical path is malformed.", exception);
            }
        }

        if (tree is not null && generatedTrees.TryGetValue(tree, out string? hintName))
        {
            if (string.IsNullOrEmpty(hintName))
            {
                throw new CanonicalizationException("A generated-source hint name is empty.");
            }

            try
            {
                return UnmappedPathValue.Generated(hintName);
            }
            catch (ArgumentException exception)
            {
                throw new CanonicalizationException("A generated-source hint name is malformed.", exception);
            }
        }

        return ResolveExternal(path);
    }

    public MappedPathPayload ResolveMapped(string mappedPath)
    {
        ArgumentNullException.ThrowIfNull(mappedPath);
        if (mappedPath.Length == 0)
        {
            return MappedPathPayload.Empty;
        }

        return MappedPathPayload.External(HashExternalPath(mappedPath));
    }

    private static UnmappedPathValue ResolveExternal(string path)
        => UnmappedPathValue.External(HashExternalPath(path));

    private static string HashExternalPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new CanonicalizationException("A source-file location has an empty required path.");
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            return CanonicalWriter.HashHex(CanonicalWriter.Encode(fullPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CanonicalizationException("A source-file location path could not be resolved.", exception);
        }
    }

}

public sealed record CanonicalDiagnosticEntry(string RecordSha256, ulong OccurrenceCount);

public sealed record CanonicalDiagnosticSet(byte[] Bytes, string Sha256, IReadOnlyList<CanonicalDiagnosticEntry> Entries);

public static class DiagnosticCanonicalizer
{
    public static CanonicalDiagnosticSet Canonicalize(
        IEnumerable<Diagnostic> diagnostics,
        CanonicalPathContext? pathContext = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        pathContext ??= new CanonicalPathContext();

        byte[][] records = diagnostics.Select(diagnostic => CanonicalizeRecord(diagnostic, pathContext)).ToArray();
        Array.Sort(records, CanonicalWriter.CompareBytes);

        List<(byte[] Record, ulong Count)> unique = [];
        foreach (byte[] record in records)
        {
            if (unique.Count > 0 && unique[^1].Record.AsSpan().SequenceEqual(record))
            {
                (byte[] existing, ulong count) = unique[^1];
                unique[^1] = (existing, checked(count + 1));
            }
            else
            {
                unique.Add((record, 1));
            }
        }

        CanonicalWriter writer = new();
        writer.WriteFrame("sga-diagnostic-set-v1");
        writer.WriteUInt64(checked((ulong)unique.Count));

        List<CanonicalDiagnosticEntry> entries = new(unique.Count);
        foreach ((byte[] record, ulong count) in unique)
        {
            writer.WriteFrame(record);
            writer.WriteUInt64(count);
            entries.Add(new CanonicalDiagnosticEntry(CanonicalWriter.HashHex(record), count));
        }

        byte[] bytes = writer.ToArray();
        return new CanonicalDiagnosticSet(bytes, CanonicalWriter.HashHex(bytes), entries);
    }

    public static byte[] CanonicalizeLocation(Location location, CanonicalPathContext? pathContext = null)
    {
        if (location is null)
        {
            throw new CanonicalizationException("A diagnostic location collection contains null.");
        }
        pathContext ??= new CanonicalPathContext();

        if (location.Kind == LocationKind.None)
        {
            CanonicalWriter noneWriter = new();
            noneWriter.WriteFrame("none");
            return noneWriter.ToArray();
        }

        if (location.Kind != LocationKind.SourceFile)
        {
            throw new UnsupportedLocationKindException($"Location kind '{location.Kind}' is not comparable in report V1.");
        }

        CanonicalSourceLocation sourceLocation = CreateSourceLocation(location, pathContext);
        return CanonicalizeSourceLocation(sourceLocation);
    }

    public static byte[] CanonicalizeDiagnosticRecord(Diagnostic diagnostic, CanonicalPathContext? pathContext = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return CanonicalizeRecord(diagnostic, pathContext ?? new CanonicalPathContext());
    }

    public static byte[] CanonicalizeSourceLocation(CanonicalSourceLocation sourceLocation)
    {
        ArgumentNullException.ThrowIfNull(sourceLocation);
        CanonicalWriter writer = new();
        writer.WriteFrame("source");
        WriteUnmappedPath(writer, sourceLocation.UnmappedPath);
        writer.WriteUInt64(sourceLocation.Utf16SpanStart);
        writer.WriteUInt64(sourceLocation.Utf16SpanLength);
        WriteMappedPath(writer, sourceLocation.MappedPath);
        writer.WriteUInt64(sourceLocation.MappedStartLine);
        writer.WriteUInt64(sourceLocation.MappedStartColumn);
        writer.WriteUInt64(sourceLocation.MappedEndLine);
        writer.WriteUInt64(sourceLocation.MappedEndColumn);
        writer.WriteByte(sourceLocation.LineVisibility switch
        {
            CanonicalLineVisibility.Visible => 0,
            CanonicalLineVisibility.Hidden => 1,
            CanonicalLineVisibility.BeforeFirstLineDirective => 2,
            _ => throw new CanonicalizationException("An unknown line-visibility value was observed."),
        });
        return writer.ToArray();
    }

    public static CanonicalSourceLocation CreateSourceLocation(
        Location location,
        CanonicalPathContext? pathContext = null)
    {
        if (location is null)
        {
            throw new CanonicalizationException("A diagnostic location collection contains null.");
        }

        if (location.Kind != LocationKind.SourceFile)
        {
            throw new UnsupportedLocationKindException($"Location kind '{location.Kind}' is not a source-file location.");
        }

        pathContext ??= new CanonicalPathContext();
        FileLinePositionSpan mapped;
        FileLinePositionSpan unmapped;
        try
        {
            mapped = location.GetMappedLineSpan();
            unmapped = location.GetLineSpan();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new CanonicalizationException("A source-file location could not produce line-span evidence.", exception);
        }
        if (!unmapped.IsValid || !mapped.IsValid)
        {
            throw new CanonicalizationException("A source-file location exposes an invalid line span.");
        }

        TextSpan span = location.SourceSpan;
        ValidateCoordinate(span.Start, nameof(span.Start));
        ValidateCoordinate(span.Length, nameof(span.Length));
        ValidateCoordinate(mapped.StartLinePosition.Line, "mapped start line");
        ValidateCoordinate(mapped.StartLinePosition.Character, "mapped start column");
        ValidateCoordinate(mapped.EndLinePosition.Line, "mapped end line");
        ValidateCoordinate(mapped.EndLinePosition.Character, "mapped end column");

        UnmappedPathValue unmappedPath = pathContext.ResolveUnmapped(location.SourceTree, unmapped.Path);
        MappedPathValue mappedPath = mapped.HasMappedPath
            ? new MappedPathValue.Mapped(pathContext.ResolveMapped(mapped.Path))
            : new MappedPathValue.Unmapped();

        if (location.SourceTree is null)
        {
            throw new CanonicalizationException("A source-file location does not expose a source tree.");
        }

        CanonicalLineVisibility visibility = location.SourceTree.GetLineVisibility(span.Start) switch
        {
            LineVisibility.Visible => CanonicalLineVisibility.Visible,
            LineVisibility.Hidden => CanonicalLineVisibility.Hidden,
            LineVisibility.BeforeFirstLineDirective => CanonicalLineVisibility.BeforeFirstLineDirective,
            _ => throw new CanonicalizationException("An unknown public line-visibility value was observed."),
        };

        return new CanonicalSourceLocation(
            unmappedPath,
            checked((ulong)span.Start),
            checked((ulong)span.Length),
            mappedPath,
            checked((ulong)mapped.StartLinePosition.Line),
            checked((ulong)mapped.StartLinePosition.Character),
            checked((ulong)mapped.EndLinePosition.Line),
            checked((ulong)mapped.EndLinePosition.Character),
            visibility);
    }

    private static byte[] CanonicalizeRecord(Diagnostic diagnostic, CanonicalPathContext pathContext)
    {
        CanonicalWriter writer = new();
        writer.WriteFrame("sga-diagnostic-v1");
        writer.WriteNullableString(diagnostic.Id);
        writer.WriteNullableString(diagnostic.Severity.ToString());
        writer.WriteBoolean(diagnostic.IsWarningAsError);
        writer.WriteBoolean(diagnostic.IsSuppressed);
        ValidateCoordinate(diagnostic.WarningLevel, nameof(diagnostic.WarningLevel));
        writer.WriteUInt64(checked((ulong)diagnostic.WarningLevel));
        writer.WriteNonNullString(diagnostic.GetMessage(CultureInfo.InvariantCulture), "Diagnostic.GetMessage(InvariantCulture)");
        writer.WriteNullableString(diagnostic.Descriptor.Category);
        writer.WriteNullableString(diagnostic.Descriptor.DefaultSeverity.ToString());
        writer.WriteNonNullString(diagnostic.Descriptor.HelpLinkUri, "DiagnosticDescriptor.HelpLinkUri");

        string[] tags = diagnostic.Descriptor.CustomTags.OrderBy(tag => tag, StringComparer.Ordinal).ToArray();
        writer.WriteSequence(tags, EncodeNullableString);
        writer.WriteFrame(CanonicalizeLocation(diagnostic.Location, pathContext));

        byte[][] additionalLocations = diagnostic.AdditionalLocations
            .Select(location => CanonicalizeLocation(location, pathContext))
            .OrderBy(bytes => bytes, Comparer<byte[]>.Create(CanonicalWriter.CompareBytes))
            .ToArray();
        writer.WriteSequence(additionalLocations, bytes => bytes);

        KeyValuePair<string, string?>[] properties = diagnostic.Properties
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, NullFirstOrdinalComparer.Instance)
            .ToArray();
        writer.WriteSequence(properties, EncodeProperty);
        return writer.ToArray();
    }

    private static byte[] EncodeNullableString(string value)
    {
        CanonicalWriter writer = new();
        writer.WriteNullableString(value);
        return writer.ToArray();
    }

    private static void WriteUnmappedPath(CanonicalWriter writer, UnmappedPathValue path)
    {
        writer.WriteNonNullString(path.Token, "canonical path token");
        writer.WriteByte(path.Kind switch
        {
            CanonicalPathKind.Controlled => 0,
            CanonicalPathKind.Generated => 1,
            CanonicalPathKind.External => 2,
            _ => throw new CanonicalizationException("An unknown path identity kind was observed."),
        });
    }

    private static void WriteMappedPayload(CanonicalWriter writer, MappedPathPayload path)
    {
        writer.WriteNonNullString(path.Token, "canonical mapped-path token");
        writer.WriteByte(path.Kind switch
        {
            CanonicalPathKind.External => 2,
            CanonicalPathKind.Empty => 3,
            _ => throw new CanonicalizationException("An unknown mapped-path payload kind was observed."),
        });
    }

    private static void WriteMappedPath(CanonicalWriter writer, MappedPathValue mappedPath)
    {
        switch (mappedPath)
        {
            case MappedPathValue.Unmapped:
                writer.WriteByte(0);
                break;
            case MappedPathValue.Mapped mapped:
                writer.WriteByte(1);
                WriteMappedPayload(writer, mapped.Path);
                break;
            default:
                throw new CanonicalizationException("An unknown mapped-path state was observed.");
        }
    }

    private static byte[] EncodeProperty(KeyValuePair<string, string?> property)
    {
        CanonicalWriter writer = new();
        writer.WriteNullableString(property.Key);
        writer.WriteNullableString(property.Value);
        return writer.ToArray();
    }

    private static void ValidateCoordinate(int coordinate, string name)
    {
        if (coordinate < 0)
        {
            throw new CanonicalizationException($"The {name} coordinate is negative.");
        }
    }

    private sealed class NullFirstOrdinalComparer : IComparer<string?>
    {
        public static NullFirstOrdinalComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (left is null)
            {
                return right is null ? 0 : -1;
            }

            return right is null ? 1 : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
