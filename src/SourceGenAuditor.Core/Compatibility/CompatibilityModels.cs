namespace SourceGenAuditor.Core.Compatibility;

public enum RoslynAdmissionDecision
{
    EqualHost,
    LowerThanHost,
    RejectedNewer,
    RejectedUnsupportedComponent,
}

public enum AggregateAdmissionDecision
{
    Admitted,
    Rejected,
    Unavailable,
}

public enum FixtureCoverage
{
    Covered,
    NotFixtureCovered,
}

public sealed record RoslynReferenceDecision(
    string ReferencingAssemblySha256,
    string SimpleName,
    string RequestedVersion,
    string? HostVersion,
    RoslynAdmissionDecision AdmissionDecision);

public sealed record PrivateDependencyEvidence(string SimpleName, string PathToken, string Sha256, string PhysicalPath);

public sealed record CompatibilityEvidence(
    IReadOnlyList<RoslynReferenceDecision> RoslynReferences,
    AggregateAdmissionDecision AggregateAdmissionDecision,
    FixtureCoverage FixtureCoverage,
    IReadOnlyList<PrivateDependencyEvidence> PrivateDependencies);

public static class CompatibilityEvidenceComparer
{
    public static bool MatchesAdmission(CompatibilityEvidence left, CompatibilityEvidence right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.AggregateAdmissionDecision == right.AggregateAdmissionDecision &&
            left.FixtureCoverage == right.FixtureCoverage &&
            left.RoslynReferences.SequenceEqual(right.RoslynReferences);
    }
}

public sealed class GeneratorLoadException : Exception
{
    public GeneratorLoadException(string message)
        : base(message)
    {
    }

    public GeneratorLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class GeneratorCompatibilityException : Exception
{
    public GeneratorCompatibilityException(string message, CompatibilityEvidence evidence)
        : base(message)
    {
        Evidence = evidence;
    }

    public CompatibilityEvidence Evidence { get; }
}
