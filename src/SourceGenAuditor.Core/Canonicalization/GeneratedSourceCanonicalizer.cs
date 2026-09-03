namespace SourceGenAuditor.Core.Canonicalization;

public sealed record GeneratedSourceValue(
    string HintName,
    string Text,
    string? EncodingName = null,
    int EncodingPreambleLength = 0,
    string? RoslynChecksum = null);

public sealed record CanonicalSourceRecord(
    string HintName,
    string TextHash,
    string? EncodingName,
    int EncodingPreambleLength,
    string? RoslynChecksum);

public sealed record CanonicalSourceSet(byte[] Bytes, string Sha256, IReadOnlyList<CanonicalSourceRecord> Records);

public static class GeneratedSourceCanonicalizer
{
    public static CanonicalSourceSet Canonicalize(IEnumerable<GeneratedSourceValue> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        GeneratedSourceValue[] ordered = sources.OrderBy(source => source.HintName, StringComparer.Ordinal).ToArray();
        for (int index = 1; index < ordered.Length; index++)
        {
            if (StringComparer.Ordinal.Equals(ordered[index - 1].HintName, ordered[index].HintName))
            {
                throw new CanonicalizationException($"Duplicate generated-source hint name: {ordered[index].HintName}");
            }
        }

        CanonicalWriter setWriter = new();
        setWriter.WriteFrame("sga-source-set-v1");
        setWriter.WriteUInt64(checked((ulong)ordered.Length));

        List<CanonicalSourceRecord> records = new(ordered.Length);
        foreach (GeneratedSourceValue source in ordered)
        {
            byte[] record = CanonicalizeRecord(source);
            setWriter.WriteFrame(record);
            byte[] textBytes = CanonicalWriter.Encode(source.Text);
            records.Add(new CanonicalSourceRecord(
                source.HintName,
                CanonicalWriter.HashHex(textBytes),
                source.EncodingName,
                source.EncodingPreambleLength,
                source.RoslynChecksum));
        }

        byte[] bytes = setWriter.ToArray();
        return new CanonicalSourceSet(bytes, CanonicalWriter.HashHex(bytes), records);
    }

    private static byte[] CanonicalizeRecord(GeneratedSourceValue source)
    {
        CanonicalWriter writer = new();
        writer.WriteFrame("sga-source-v1");
        writer.WriteFrame(source.HintName);
        writer.WriteFrame(source.Text);
        return writer.ToArray();
    }
}
