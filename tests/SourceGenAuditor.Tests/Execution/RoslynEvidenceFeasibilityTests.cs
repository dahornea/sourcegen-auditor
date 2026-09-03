using SourceGenAuditor.Cli;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Execution;
using SourceGenAuditor.Core.Model;
using SourceGenAuditor.Core.Scenario;
using Xunit;

namespace SourceGenAuditor.Tests.Execution;

public sealed class RoslynEvidenceFeasibilityTests
{
    [Fact]
    public void F2RelevantScenarioHasRequiredPublicOutputReasons()
    {
        (WorkerRunEvidence cold, WorkerRunEvidence transition) = Execute("relevant");

        AssertReasons(cold.Checkpoints.Single(), "New");
        AssertReasons(Find(transition, "transitionA"), "New");
        AssertReasons(Find(transition, "mutatedB"), "Modified");
        AssertReasons(Find(transition, "restoredA"), "Modified");
        AssertReasons(Find(transition, "stableA"), "Cached");
    }

    [Fact]
    public void F2IrrelevantScenarioHasRequiredPublicOutputReasons()
    {
        (WorkerRunEvidence cold, WorkerRunEvidence transition) = Execute("irrelevant");

        AssertReasons(cold.Checkpoints.Single(), "New");
        AssertReasons(Find(transition, "transitionA"), "New");
        AssertReasons(Find(transition, "mutatedB"), "Cached");
        AssertReasons(Find(transition, "restoredA"), "Cached");
        AssertReasons(Find(transition, "stableA"), "Cached");
    }

    [Theory]
    [InlineData("relevant")]
    [InlineData("irrelevant")]
    public async Task F3FreshProcessesAgreeAndTransitionRestoresA(string scenarioName)
    {
        string manifest = Path.Combine(FindRepositoryRoot(), "tests", "scenarios", scenarioName, "scenario.json");
        WorkerSupervisor supervisor = new();
        SupervisedWorkerResult coldProcess = await supervisor.RunAsync(
            manifest,
            "cold",
            20,
            TestContext.Current.CancellationToken);
        SupervisedWorkerResult transitionProcess = await supervisor.RunAsync(
            manifest,
            "transition",
            20,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(coldProcess.ProcessId, transitionProcess.ProcessId);
        Assert.Equal(0, coldProcess.ExitCode);
        Assert.Equal(0, transitionProcess.ExitCode);
        WorkerRunEvidence cold = coldProcess.Evidence;
        WorkerRunEvidence transition = transitionProcess.Evidence;
        CheckpointEvidence coldA = cold.Checkpoints.Single();
        CheckpointEvidence transitionA = Find(transition, "transitionA");
        CheckpointEvidence restoredA = Find(transition, "restoredA");

        Assert.Equal(coldA.Sources.SetSha256, transitionA.Sources.SetSha256);
        Assert.Equal(coldA.GeneratorDiagnostics.SetSha256, transitionA.GeneratorDiagnostics.SetSha256);
        Assert.Equal(transitionA.Sources.SetSha256, restoredA.Sources.SetSha256);
        Assert.Equal(transitionA.GeneratorDiagnostics.SetSha256, restoredA.GeneratorDiagnostics.SetSha256);
        Assert.All(cold.Checkpoints.Concat(transition.Checkpoints), checkpoint =>
            Assert.Equal(CheckpointCompletion.Complete, checkpoint.Completion));
        Assert.Equal(FixtureCoverage.Covered, cold.Compatibility.FixtureCoverage);
        Assert.Equal(FixtureCoverage.Covered, transition.Compatibility.FixtureCoverage);
    }

    [Fact]
    public void TrackedInputRelationshipsUsePublicReferenceIdentityAndOccurrence()
    {
        ScenarioDefinition loadedScenario = ScenarioLoader.Load(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "scenarios",
            "relevant",
            "scenario.json"));
        ScenarioDefinition scenario = loadedScenario with
        {
            Baseline = loadedScenario.Baseline with
            {
                Sources = [loadedScenario.Baseline.Sources[0] with
                {
                    Text = "namespace Scenario; public sealed class Alpha; public sealed class Beta;",
                }],
            },
            Mutation = loadedScenario.Mutation with
            {
                ReplacementText = "namespace Scenario; public sealed class Gamma; public sealed class Delta;",
            },
        };
        LoadedGenerator generator = GeneratorAssemblyLoader.Load(scenario.Generator);

        WorkerRunEvidence evidence = new RoslynAuditRunner(scenario, generator.Generator, generator.Compatibility)
            .RunTransition(TestContext.Current.CancellationToken);
        TrackedStepObservation collection = Find(evidence, "mutatedB").TrackedSteps.Steps
            .Single(step => step.Name == "FixtureCollection");
        TrackedInputObservation[] inputs = collection.Inputs
            .Where(input => input.SourceStepName == "FixtureClass")
            .ToArray();

        Assert.Equal(new ulong[] { 0, 1 }, inputs.Select(input => input.SourceOccurrence));
        Assert.All(inputs, input => Assert.Equal(0UL, input.OutputIndex));
    }

    private static (WorkerRunEvidence Cold, WorkerRunEvidence Transition) Execute(string scenarioName)
    {
        string manifest = Path.Combine(FindRepositoryRoot(), "tests", "scenarios", scenarioName, "scenario.json");
        ScenarioDefinition scenario = ScenarioLoader.Load(manifest);

        LoadedGenerator coldGenerator = GeneratorAssemblyLoader.Load(scenario.Generator);
        WorkerRunEvidence cold = new RoslynAuditRunner(
            scenario,
            coldGenerator.Generator,
            coldGenerator.Compatibility).RunCold(TestContext.Current.CancellationToken);

        LoadedGenerator transitionGenerator = GeneratorAssemblyLoader.Load(scenario.Generator);
        WorkerRunEvidence transition = new RoslynAuditRunner(
            scenario,
            transitionGenerator.Generator,
            transitionGenerator.Compatibility).RunTransition(TestContext.Current.CancellationToken);
        return (cold, transition);
    }

    private static CheckpointEvidence Find(WorkerRunEvidence evidence, string runId)
        => evidence.Checkpoints.Single(checkpoint => checkpoint.RunId == runId);

    private static void AssertReasons(CheckpointEvidence checkpoint, params string[] expected)
    {
        TrackedStepObservation[] outputs = checkpoint.TrackedSteps.Steps
            .Where(step => step.Name == "SourceOutput")
            .ToArray();
        Assert.NotEmpty(outputs);
        string[] reasons = outputs.SelectMany(step => step.Outputs).Select(output => output.Reason).ToArray();
        Assert.Equal(expected, reasons);
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
