using SourceGenAuditor.Core.Canonicalization;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Reporting;

namespace SourceGenAuditor.Core.Execution;

public enum SnapshotAvailability
{
    Available,
    Unavailable,
}

public enum CheckpointCompletion
{
    Complete,
    Partial,
    Unavailable,
}

public sealed record RoslynHostEvidence(string SimpleName, string AssemblyVersion, string ModuleVersionId);

public sealed record EnvironmentEvidence(
    string RuntimeVersion,
    string OsDescription,
    string ProcessArchitecture,
    string Culture,
    string UiCulture,
    string TimeZoneId,
    IReadOnlyList<RoslynHostEvidence> RoslynHost,
    IReadOnlyList<PrivateDependencyEvidence> PrivateDependencies);

public sealed record GeneratedSourceObservation(
    string HintName,
    string Text,
    string TextUtf8Base64,
    ulong Utf16Length,
    string? EncodingName,
    ulong PreambleLength,
    string ChecksumAlgorithm,
    string RoslynChecksumHex,
    string ContentSha256);

public sealed record SourceSnapshot(
    SnapshotAvailability Availability,
    string? UnavailableReason,
    IReadOnlyList<GeneratedSourceObservation> Records,
    string? SetSha256);

public sealed record DiagnosticPropertyObservation(string Key, string? Value);

public sealed record DiagnosticRecordObservation(
    string Id,
    string Severity,
    bool IsWarningAsError,
    bool IsSuppressed,
    ulong WarningLevel,
    string InvariantMessage,
    string DescriptorCategory,
    string DescriptorDefaultSeverity,
    string HelpLinkUri,
    IReadOnlyList<string?> CustomTags,
    LocationV1 PrimaryLocation,
    IReadOnlyList<LocationV1> AdditionalLocations,
    IReadOnlyList<DiagnosticPropertyObservation> Properties,
    ulong OccurrenceCount,
    string CanonicalRecordBase64);

public sealed record DiagnosticSnapshot(
    SnapshotAvailability Availability,
    string? UnavailableReason,
    IReadOnlyList<DiagnosticRecordObservation> Records,
    string? SetSha256);

public sealed record TrackedInputObservation(string SourceStepName, ulong SourceOccurrence, ulong OutputIndex);

public sealed record TrackedOutputObservation(ulong Index, string Reason);

public sealed record TrackedStepObservation(
    string Name,
    ulong Occurrence,
    IReadOnlyList<TrackedInputObservation> Inputs,
    IReadOnlyList<TrackedOutputObservation> Outputs);

public sealed record TrackedStepsSnapshot(
    SnapshotAvailability Availability,
    string? UnavailableReason,
    IReadOnlyList<TrackedStepObservation> Steps);

public sealed record GeneratorExceptionObservation(string TypeName, string Message, string? StackTrace);

public sealed record CheckpointEvidence(
    string RunId,
    CheckpointCompletion Completion,
    EnvironmentEvidence Environment,
    SourceSnapshot Sources,
    DiagnosticSnapshot GeneratorDiagnostics,
    DiagnosticSnapshot RoslynFailureDiagnostics,
    DiagnosticSnapshot InputCompilationDiagnostics,
    DiagnosticSnapshot PostGenerationCompilationDiagnostics,
    TrackedStepsSnapshot TrackedSteps,
    GeneratorExceptionObservation? GeneratorException);

public sealed record WorkerRunEvidence(
    CompatibilityEvidence Compatibility,
    IReadOnlyList<CheckpointEvidence> Checkpoints,
    string? FailureKind,
    string? FailureMessage,
    string? ActiveCheckpointId);
