using System.Text;
using SourceGenAuditor.Cli;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Execution;
using SourceGenAuditor.Core.Model;
using SourceGenAuditor.Core.Scenario;
using Xunit;

namespace SourceGenAuditor.Tests.Execution;

public sealed class ImmutableInputTests
{
    [Fact]
    public async Task ScenarioLeasePreventsDeclaredFilesChangingAcrossWorkers()
    {
        string manifest = CopyScenario();

        using ScenarioLease lease = ScenarioLoader.AcquireLease(manifest);
        string sourcePath = lease.Scenario.Baseline.Sources[0].PhysicalPath;
        Assert.Throws<IOException>(() => File.WriteAllText(sourcePath, "changed", new UTF8Encoding(false)));

        SupervisedWorkerResult cold = await new WorkerSupervisor().RunAsync(
            lease.Scenario.ManifestPath,
            "cold",
            10,
            TestContext.Current.CancellationToken);
        SupervisedWorkerResult transition = await new WorkerSupervisor().RunAsync(
            lease.Scenario.ManifestPath,
            "transition",
            10,
            TestContext.Current.CancellationToken);

        Assert.Null(cold.Evidence.FailureKind);
        Assert.Null(transition.Evidence.FailureKind);
    }

    [Fact]
    public void LoadedScenarioExecutesOnlyTheBytesThatWereHashed()
    {
        string manifest = CopyScenario();
        ScenarioDefinition scenario = ScenarioLoader.Load(manifest);

        File.WriteAllText(scenario.Baseline.Sources[0].PhysicalPath, "this is not the hashed source", new UTF8Encoding(false));
        File.WriteAllText(scenario.Mutation.ReplacementPath, "this is not the hashed replacement", new UTF8Encoding(false));
        File.WriteAllBytes(scenario.Baseline.References[0].PhysicalPath, [0, 1, 2, 3]);
        File.WriteAllBytes(scenario.Generator.AssemblyPath, [0, 1, 2, 3]);

        LoadedGenerator loaded = GeneratorAssemblyLoader.Load(scenario.Generator);
        WorkerRunEvidence evidence = new RoslynAuditRunner(scenario, loaded.Generator, loaded.Compatibility)
            .RunTransition(TestContext.Current.CancellationToken);

        Assert.Null(evidence.FailureKind);
        Assert.Equal(4, evidence.Checkpoints.Count);
        Assert.All(evidence.Checkpoints, checkpoint => Assert.Equal(CheckpointCompletion.Complete, checkpoint.Completion));
    }

    private static string CopyScenario()
    {
        string repository = FindRepositoryRoot();
        string source = Path.Combine(repository, "tests", "scenarios", "relevant");
        string target = Path.Combine(repository, "artifacts", "immutable-inputs", Guid.NewGuid().ToString("N"));
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, target, StringComparison.OrdinalIgnoreCase));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = file.Replace(source, target, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        return Path.Combine(target, "scenario.json");
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
