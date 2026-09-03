using SourceGenAuditor.Core.Execution;
using SourceGenAuditor.Core.Model;

namespace SourceGenAuditor.Core.Evaluation;

public enum AssertionOutcome
{
    PASS,
    FAIL,
    UNKNOWN,
    ERROR,
}

public sealed record ObservedFact(string Id, string Value, IReadOnlyList<string> EvidenceIds);

public sealed record AssertionResult(
    string Id,
    AssertionOutcome Result,
    string ReasonCode,
    string Message,
    IReadOnlyList<string> EvidenceIds);

public sealed record FailureRecord(string Kind, string Message, string? ActiveCheckpointId);

public sealed record AuditResult(
    IReadOnlyList<ObservedFact> Observations,
    IReadOnlyList<AssertionResult> Assertions,
    AssertionOutcome Verdict,
    bool PartialEvidence,
    FailureRecord? Failure);

public static class AuditEvaluator
{
    public static AuditResult Evaluate(
        ScenarioDefinition scenario,
        WorkerRunEvidence cold,
        WorkerRunEvidence transition)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(cold);
        ArgumentNullException.ThrowIfNull(transition);

        Dictionary<string, CheckpointEvidence> checkpoints = cold.Checkpoints
            .Concat(transition.Checkpoints)
            .ToDictionary(checkpoint => checkpoint.RunId, StringComparer.Ordinal);
        FailureRecord? failure = CreateFailure(cold) ?? CreateFailure(transition);
        List<AssertionResult> assertions =
        [
            CompareCold(checkpoints, failure),
            CompareEffect("declared-source-effect", "Sources", scenario.Mutation.Expectations.GeneratedSources, checkpoints, failure),
            CompareEffect("declared-diagnostic-effect", "GeneratorDiagnostics", scenario.Mutation.Expectations.GeneratorDiagnostics, checkpoints, failure),
            EvaluateInvalidation(scenario.Mutation.Relevance, checkpoints, failure),
            EvaluateRestoration(checkpoints, failure),
            EvaluateStableCache(checkpoints, failure),
        ];
        AssertionOutcome verdict = assertions.Select(assertion => assertion.Result).Aggregate(AssertionOutcome.PASS, Higher);
        if (failure is not null)
        {
            verdict = AssertionOutcome.ERROR;
        }

        List<ObservedFact> observations = checkpoints.Values
            .OrderBy(checkpoint => RunOrder(checkpoint.RunId))
            .SelectMany(checkpoint => new[]
            {
                new ObservedFact($"{checkpoint.RunId}.sources", checkpoint.Sources.SetSha256 ?? "Unavailable", [$"run:{checkpoint.RunId}"]),
                new ObservedFact($"{checkpoint.RunId}.generatorDiagnostics", checkpoint.GeneratorDiagnostics.SetSha256 ?? "Unavailable", [$"run:{checkpoint.RunId}"]),
            })
            .ToList();
        bool partial = failure is not null || checkpoints.Count != 5 || checkpoints.Values.Any(checkpoint =>
            checkpoint.Completion != CheckpointCompletion.Complete ||
            checkpoint.Sources.Availability != SnapshotAvailability.Available ||
            checkpoint.GeneratorDiagnostics.Availability != SnapshotAvailability.Available ||
            checkpoint.TrackedSteps.Availability != SnapshotAvailability.Available);
        return new AuditResult(observations, assertions, verdict, partial, failure);
    }

    private static AssertionResult CompareCold(
        IReadOnlyDictionary<string, CheckpointEvidence> checkpoints,
        FailureRecord? failure)
    {
        if (!TryGetComplete(checkpoints, "coldA", out CheckpointEvidence cold) ||
            !TryGetComplete(checkpoints, "transitionA", out CheckpointEvidence transition))
        {
            return Missing("cold-output-determinism", failure, "COLD_EVIDENCE_INCOMPLETE", "Both fresh A checkpoints are required.");
        }

        if (!SnapshotsAvailable(cold) || !SnapshotsAvailable(transition))
        {
            return Unknown(
                "cold-output-determinism",
                "SNAPSHOT_UNAVAILABLE",
                "A required fresh-A public snapshot is unavailable.",
                ["run:coldA", "run:transitionA"]);
        }

        bool equal = EqualSnapshot(cold.Sources, transition.Sources) &&
            EqualSnapshot(cold.GeneratorDiagnostics, transition.GeneratorDiagnostics);
        return Result(
            "cold-output-determinism",
            equal,
            "COLD_SNAPSHOTS_EQUAL",
            "COLD_SNAPSHOTS_DIFFER",
            equal ? "The two fresh A observations are canonically equal." : "The two fresh A observations differ.",
            ["run:coldA", "run:transitionA"]);
    }

    private static AssertionResult CompareEffect(
        string id,
        string snapshotName,
        ExpectedEffect expected,
        IReadOnlyDictionary<string, CheckpointEvidence> checkpoints,
        FailureRecord? failure)
    {
        if (!TryGetComplete(checkpoints, "transitionA", out CheckpointEvidence baseline) ||
            !TryGetComplete(checkpoints, "mutatedB", out CheckpointEvidence mutated))
        {
            return Missing(id, failure, "MUTATION_EVIDENCE_INCOMPLETE", "Transition A and mutation B evidence are required.");
        }

        SourceSnapshot? baselineSources = snapshotName == "Sources" ? baseline.Sources : null;
        SourceSnapshot? mutatedSources = snapshotName == "Sources" ? mutated.Sources : null;
        DiagnosticSnapshot? baselineDiagnostics = snapshotName == "GeneratorDiagnostics" ? baseline.GeneratorDiagnostics : null;
        DiagnosticSnapshot? mutatedDiagnostics = snapshotName == "GeneratorDiagnostics" ? mutated.GeneratorDiagnostics : null;
        bool available = baselineSources is not null
            ? baselineSources.Availability == SnapshotAvailability.Available && mutatedSources!.Availability == SnapshotAvailability.Available
            : baselineDiagnostics!.Availability == SnapshotAvailability.Available && mutatedDiagnostics!.Availability == SnapshotAvailability.Available;
        if (!available)
        {
            return Unknown(id, "SNAPSHOT_UNAVAILABLE", "The required public snapshot is unavailable.", ["run:transitionA", "run:mutatedB"]);
        }

        bool equal = baselineSources is not null
            ? StringComparer.Ordinal.Equals(baselineSources.SetSha256, mutatedSources!.SetSha256)
            : StringComparer.Ordinal.Equals(baselineDiagnostics!.SetSha256, mutatedDiagnostics!.SetSha256);
        bool matched = expected == ExpectedEffect.Changed ? !equal : equal;
        return Result(
            id,
            matched,
            "DECLARED_EFFECT_OBSERVED",
            "DECLARED_EFFECT_MISMATCH",
            matched ? "The observed canonical effect matches the declaration." : "The observed canonical effect does not match the declaration.",
            ["run:transitionA", "run:mutatedB"]);
    }

    private static AssertionResult EvaluateInvalidation(
        MutationRelevance relevance,
        IReadOnlyDictionary<string, CheckpointEvidence> checkpoints,
        FailureRecord? failure)
    {
        const string id = "declared-invalidation";
        if (!TryGetComplete(checkpoints, "mutatedB", out CheckpointEvidence mutated))
        {
            return Missing(id, failure, "MUTATION_EVIDENCE_INCOMPLETE", "Mutation B tracking evidence is required.");
        }

        if (mutated.TrackedSteps.Availability != SnapshotAvailability.Available)
        {
            return Unknown(id, "TRACKING_UNAVAILABLE", "Registered output tracking is unavailable.", ["run:mutatedB"]);
        }

        string[] reasons = FinalOutputReasons(mutated);
        if (reasons.Length == 0)
        {
            return Unknown(id, "FINAL_OUTPUT_REASONS_EMPTY", "No registered final-output reason was recorded.", ["run:mutatedB"]);
        }

        bool passed = relevance == MutationRelevance.Relevant
            ? reasons.Any(reason => reason != "Cached")
            : reasons.All(reason => reason == "Cached");
        return Result(
            id,
            passed,
            relevance == MutationRelevance.Relevant ? "RELEVANT_OUTPUT_INVALIDATED" : "IRRELEVANT_OUTPUT_CACHED",
            relevance == MutationRelevance.Relevant ? "RELEVANT_OUTPUT_NOT_INVALIDATED" : "IRRELEVANT_OUTPUT_REGENERATED",
            passed ? "Registered final-output reasons match declared relevance." : "Registered final-output reasons contradict declared relevance.",
            ["run:mutatedB"]);
    }

    private static AssertionResult EvaluateRestoration(
        IReadOnlyDictionary<string, CheckpointEvidence> checkpoints,
        FailureRecord? failure)
    {
        const string id = "restoration";
        if (!TryGetComplete(checkpoints, "transitionA", out CheckpointEvidence baseline) ||
            !TryGetComplete(checkpoints, "restoredA", out CheckpointEvidence restored))
        {
            return Missing(id, failure, "RESTORATION_EVIDENCE_INCOMPLETE", "Initial and restored A evidence are required.");
        }

        if (!SnapshotsAvailable(baseline) || !SnapshotsAvailable(restored))
        {
            return Unknown(
                id,
                "SNAPSHOT_UNAVAILABLE",
                "A required restoration public snapshot is unavailable.",
                ["run:transitionA", "run:restoredA"]);
        }

        bool equal = EqualSnapshot(baseline.Sources, restored.Sources) &&
            EqualSnapshot(baseline.GeneratorDiagnostics, restored.GeneratorDiagnostics);
        return Result(
            id,
            equal,
            "RESTORED_A_MATCHES",
            "RESTORED_A_DIFFERS",
            equal ? "Restored A matches the transition worker's initial A." : "Restored A does not match the transition worker's initial A.",
            ["run:transitionA", "run:restoredA"]);
    }

    private static AssertionResult EvaluateStableCache(
        IReadOnlyDictionary<string, CheckpointEvidence> checkpoints,
        FailureRecord? failure)
    {
        const string id = "stable-restored-cache";
        if (!TryGetComplete(checkpoints, "stableA", out CheckpointEvidence stable))
        {
            return Missing(id, failure, "STABLE_EVIDENCE_INCOMPLETE", "The unchanged restored-A checkpoint is required.");
        }

        if (stable.TrackedSteps.Availability != SnapshotAvailability.Available)
        {
            return Unknown(id, "TRACKING_UNAVAILABLE", "Registered output tracking is unavailable.", ["run:stableA"]);
        }

        string[] reasons = FinalOutputReasons(stable);
        if (reasons.Length == 0)
        {
            return Unknown(id, "FINAL_OUTPUT_REASONS_EMPTY", "No registered final-output reason was recorded.", ["run:stableA"]);
        }

        bool passed = reasons.All(reason => reason == "Cached");
        return Result(
            id,
            passed,
            "STABLE_OUTPUT_CACHED",
            "STABLE_OUTPUT_REGENERATED",
            passed ? "Every registered final output was cached." : "At least one registered final output was not cached.",
            ["run:stableA"]);
    }

    private static bool TryGetComplete(
        IReadOnlyDictionary<string, CheckpointEvidence> checkpoints,
        string id,
        out CheckpointEvidence checkpoint)
    {
        if (checkpoints.TryGetValue(id, out CheckpointEvidence? value) && value.Completion == CheckpointCompletion.Complete)
        {
            checkpoint = value;
            return true;
        }

        checkpoint = null!;
        return false;
    }

    private static bool EqualSnapshot(SourceSnapshot left, SourceSnapshot right)
        => left.Availability == SnapshotAvailability.Available && right.Availability == SnapshotAvailability.Available &&
           StringComparer.Ordinal.Equals(left.SetSha256, right.SetSha256);

    private static bool EqualSnapshot(DiagnosticSnapshot left, DiagnosticSnapshot right)
        => left.Availability == SnapshotAvailability.Available && right.Availability == SnapshotAvailability.Available &&
           StringComparer.Ordinal.Equals(left.SetSha256, right.SetSha256);

    private static bool SnapshotsAvailable(CheckpointEvidence checkpoint)
        => checkpoint.Sources.Availability == SnapshotAvailability.Available &&
           checkpoint.GeneratorDiagnostics.Availability == SnapshotAvailability.Available;

    private static string[] FinalOutputReasons(CheckpointEvidence checkpoint)
        => checkpoint.TrackedSteps.Steps
            .Where(step => step.Name is "SourceOutput" or "ImplementationSourceOutput" or "PreCompilationSourceOutput")
            .SelectMany(step => step.Outputs)
            .OrderBy(output => output.Index)
            .Select(output => output.Reason)
            .ToArray();

    private static AssertionResult Missing(string id, FailureRecord? failure, string reason, string message)
        => failure is null
            ? Unknown(id, reason, message, [])
            : new AssertionResult(id, AssertionOutcome.ERROR, "EXECUTION_ERROR", message, []);

    private static AssertionResult Unknown(string id, string reason, string message, IReadOnlyList<string> evidence)
        => new(id, AssertionOutcome.UNKNOWN, reason, message, evidence);

    private static AssertionResult Result(
        string id,
        bool passed,
        string passReason,
        string failReason,
        string message,
        IReadOnlyList<string> evidence)
        => new(id, passed ? AssertionOutcome.PASS : AssertionOutcome.FAIL, passed ? passReason : failReason, message, evidence);

    private static FailureRecord? CreateFailure(WorkerRunEvidence worker)
        => worker.FailureKind is null ? null : new FailureRecord(worker.FailureKind, worker.FailureMessage ?? string.Empty, worker.ActiveCheckpointId);

    private static AssertionOutcome Higher(AssertionOutcome left, AssertionOutcome right)
        => Rank(right) > Rank(left) ? right : left;

    private static int Rank(AssertionOutcome outcome) => outcome switch
    {
        AssertionOutcome.PASS => 0,
        AssertionOutcome.UNKNOWN => 1,
        AssertionOutcome.FAIL => 2,
        AssertionOutcome.ERROR => 3,
        _ => 3,
    };

    private static int RunOrder(string id) => id switch
    {
        "coldA" => 0,
        "transitionA" => 1,
        "mutatedB" => 2,
        "restoredA" => 3,
        "stableA" => 4,
        _ => 5,
    };
}
