using SourceGenAuditor.Cli;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Evaluation;
using SourceGenAuditor.Core.Execution;
using SourceGenAuditor.Core.Model;
using SourceGenAuditor.Core.Scenario;
using Xunit;

namespace SourceGenAuditor.Tests.Evaluation;

public sealed class EvaluationContractTests
{
    [Theory]
    [InlineData("relevant")]
    [InlineData("irrelevant")]
    public void ApprovedScenariosProduceSixPassingAssertions(string scenarioName)
    {
        (ScenarioDefinition scenario, WorkerRunEvidence cold, WorkerRunEvidence transition) = Execute(scenarioName);
        AuditResult result = AuditEvaluator.Evaluate(scenario, cold, transition);

        Assert.Equal(6, result.Assertions.Count);
        Assert.All(result.Assertions, assertion => Assert.Equal(AssertionOutcome.PASS, assertion.Result));
        Assert.Equal(AssertionOutcome.PASS, result.Verdict);
        Assert.Equal(0, ExitCodeMapper.FromVerdict(result.Verdict));
    }

    [Fact]
    public void UnchangedIsNotCachedForAnIrrelevantMutation()
    {
        (ScenarioDefinition scenario, WorkerRunEvidence cold, WorkerRunEvidence transition) = Execute("irrelevant");
        WorkerRunEvidence changed = ReplaceMutationReasons(transition, ["Unchanged"]);

        AuditResult result = AuditEvaluator.Evaluate(scenario, cold, changed);
        AssertionResult assertion = result.Assertions.Single(item => item.Id == "declared-invalidation");

        Assert.Equal(AssertionOutcome.FAIL, assertion.Result);
        Assert.Equal(AssertionOutcome.FAIL, result.Verdict);
        Assert.Equal(1, ExitCodeMapper.FromVerdict(result.Verdict));
    }

    [Fact]
    public void EmptyFinalOutputReasonsAreUnknownAndNeverPass()
    {
        (ScenarioDefinition scenario, WorkerRunEvidence cold, WorkerRunEvidence transition) = Execute("irrelevant");
        WorkerRunEvidence changed = ReplaceMutationReasons(transition, []);

        AuditResult result = AuditEvaluator.Evaluate(scenario, cold, changed);
        AssertionResult assertion = result.Assertions.Single(item => item.Id == "declared-invalidation");

        Assert.Equal(AssertionOutcome.UNKNOWN, assertion.Result);
        Assert.Equal(AssertionOutcome.UNKNOWN, result.Verdict);
        Assert.Equal(2, ExitCodeMapper.FromVerdict(result.Verdict));
    }

    [Theory]
    [InlineData("mutatedB", "declared-invalidation")]
    [InlineData("stableA", "stable-restored-cache")]
    public void MissingPublicTrackingEvidenceMakesTheDependentAssertionUnknown(
        string runId,
        string assertionId)
    {
        (ScenarioDefinition scenario, WorkerRunEvidence cold, WorkerRunEvidence transition) = Execute("irrelevant");
        WorkerRunEvidence changed = ReplaceTrackingWithUnavailable(transition, runId);

        AuditResult result = AuditEvaluator.Evaluate(scenario, cold, changed);
        AssertionResult assertion = result.Assertions.Single(item => item.Id == assertionId);

        Assert.Equal(AssertionOutcome.UNKNOWN, assertion.Result);
        Assert.Equal("TRACKING_UNAVAILABLE", assertion.ReasonCode);
        Assert.Equal(AssertionOutcome.UNKNOWN, result.Verdict);
        Assert.Equal(2, ExitCodeMapper.FromVerdict(result.Verdict));
    }

    [Fact]
    public void ExecutionFailureOverridesOtherVerdicts()
    {
        (ScenarioDefinition scenario, WorkerRunEvidence cold, WorkerRunEvidence transition) = Execute("relevant");
        WorkerRunEvidence failed = transition with
        {
            Checkpoints = transition.Checkpoints.Take(1).ToArray(),
            FailureKind = "WorkerCrash",
            FailureMessage = "crashed",
            ActiveCheckpointId = "mutatedB",
        };

        AuditResult result = AuditEvaluator.Evaluate(scenario, cold, failed);

        Assert.Equal(AssertionOutcome.ERROR, result.Verdict);
        Assert.True(result.PartialEvidence);
        Assert.Equal(3, ExitCodeMapper.FromVerdict(result.Verdict));
    }

    [Fact]
    public void CrossWorkerAdmissionMismatchIsDetected()
    {
        CompatibilityEvidence left = new([], AggregateAdmissionDecision.Admitted, FixtureCoverage.Covered, []);
        CompatibilityEvidence right = new([], AggregateAdmissionDecision.Admitted, FixtureCoverage.NotFixtureCovered, []);

        Assert.False(CompatibilityEvidenceComparer.MatchesAdmission(left, right));
        Assert.True(CompatibilityEvidenceComparer.MatchesAdmission(left, left));
    }

    private static WorkerRunEvidence ReplaceMutationReasons(WorkerRunEvidence transition, IReadOnlyList<string> reasons)
    {
        CheckpointEvidence mutated = transition.Checkpoints.Single(checkpoint => checkpoint.RunId == "mutatedB");
        TrackedStepObservation sourceOutput = mutated.TrackedSteps.Steps.Single(step => step.Name == "SourceOutput");
        CheckpointEvidence replacement = mutated with
        {
            TrackedSteps = mutated.TrackedSteps with
            {
                Steps = mutated.TrackedSteps.Steps.Select(step => step == sourceOutput
                    ? step with
                    {
                        Outputs = reasons.Select((reason, index) => new TrackedOutputObservation(checked((ulong)index), reason)).ToArray(),
                    }
                    : step).ToArray(),
            },
        };
        return transition with
        {
            Checkpoints = transition.Checkpoints.Select(checkpoint => checkpoint.RunId == "mutatedB" ? replacement : checkpoint).ToArray(),
        };
    }

    private static WorkerRunEvidence ReplaceTrackingWithUnavailable(WorkerRunEvidence transition, string runId)
        => transition with
        {
            Checkpoints = transition.Checkpoints.Select(checkpoint => checkpoint.RunId == runId
                ? checkpoint with
                {
                    TrackedSteps = new TrackedStepsSnapshot(
                        SnapshotAvailability.Unavailable,
                        "MissingPublicEvidence",
                        []),
                }
                : checkpoint).ToArray(),
        };

    private static (ScenarioDefinition Scenario, WorkerRunEvidence Cold, WorkerRunEvidence Transition) Execute(string scenarioName)
    {
        string manifest = Path.Combine(FindRepositoryRoot(), "tests", "scenarios", scenarioName, "scenario.json");
        ScenarioDefinition scenario = ScenarioLoader.Load(manifest);
        LoadedGenerator coldGenerator = GeneratorAssemblyLoader.Load(scenario.Generator);
        WorkerRunEvidence cold = new RoslynAuditRunner(scenario, coldGenerator.Generator, coldGenerator.Compatibility)
            .RunCold(TestContext.Current.CancellationToken);
        LoadedGenerator transitionGenerator = GeneratorAssemblyLoader.Load(scenario.Generator);
        WorkerRunEvidence transition = new RoslynAuditRunner(scenario, transitionGenerator.Generator, transitionGenerator.Compatibility)
            .RunTransition(TestContext.Current.CancellationToken);
        return (scenario, cold, transition);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SourceGenAuditor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
