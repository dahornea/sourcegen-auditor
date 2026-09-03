namespace SourceGenAuditor.Core.Canonicalization;

public enum GeneratedSourceChangeKind
{
    Added,
    Removed,
    Modified,
}

public sealed record GeneratedSourceChange(string HintName, GeneratedSourceChangeKind Kind);

public static class GeneratedSourceDiff
{
    public static IReadOnlyList<GeneratedSourceChange> Compare(CanonicalSourceSet before, CanonicalSourceSet after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        Dictionary<string, CanonicalSourceRecord> beforeByHint = before.Records.ToDictionary(record => record.HintName, StringComparer.Ordinal);
        Dictionary<string, CanonicalSourceRecord> afterByHint = after.Records.ToDictionary(record => record.HintName, StringComparer.Ordinal);
        SortedSet<string> names = new(beforeByHint.Keys, StringComparer.Ordinal);
        names.UnionWith(afterByHint.Keys);
        List<GeneratedSourceChange> changes = [];
        foreach (string name in names)
        {
            bool existed = beforeByHint.TryGetValue(name, out CanonicalSourceRecord? oldRecord);
            bool exists = afterByHint.TryGetValue(name, out CanonicalSourceRecord? newRecord);
            if (!existed)
            {
                changes.Add(new GeneratedSourceChange(name, GeneratedSourceChangeKind.Added));
            }
            else if (!exists)
            {
                changes.Add(new GeneratedSourceChange(name, GeneratedSourceChangeKind.Removed));
            }
            else if (!StringComparer.Ordinal.Equals(oldRecord!.TextHash, newRecord!.TextHash))
            {
                changes.Add(new GeneratedSourceChange(name, GeneratedSourceChangeKind.Modified));
            }
        }

        return changes;
    }
}
