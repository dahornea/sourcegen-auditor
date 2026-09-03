using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SourceGenAuditor.Cli;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Execution;
using SourceGenAuditor.Core.Model;
using SourceGenAuditor.Core.Protocol;
using SourceGenAuditor.Core.Scenario;
using Xunit;

namespace SourceGenAuditor.Tests.Execution;

public sealed class WorkerFailureRetentionTests
{
    [Fact]
    public async Task GeneratorExceptionPreservesCompleteAAndPartialB()
    {
        string manifest = CreateScenario("// SGA_MODE_THROW_AFTER\nnamespace Scenario; public sealed class Beta;");
        SupervisedWorkerResult result = await new WorkerSupervisor().RunAsync(
            manifest,
            "transition",
            10,
            TestContext.Current.CancellationToken);

        Assert.Equal("GeneratorException", result.Evidence.FailureKind);
        Assert.Equal(2, result.Evidence.Checkpoints.Count);
        Assert.Equal(CheckpointCompletion.Complete, result.Evidence.Checkpoints[0].Completion);
        Assert.Equal(CheckpointCompletion.Partial, result.Evidence.Checkpoints[1].Completion);
        Assert.Equal("mutatedB", result.Evidence.ActiveCheckpointId);
    }

    [Fact]
    public async Task TimeoutPreservesLastCompleteCheckpointAndCleansRoot()
    {
        string manifest = CreateScenario("// SGA_MODE_HANG\nnamespace Scenario; public sealed class Beta;");
        SupervisedWorkerResult result = await new WorkerSupervisor().RunAsync(
            manifest,
            "transition",
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal("Timeout", result.Evidence.FailureKind);
        Assert.Equal(2, result.Evidence.Checkpoints.Count);
        Assert.Equal("transitionA", result.Evidence.Checkpoints[0].RunId);
        Assert.Equal("mutatedB", result.Evidence.Checkpoints[1].RunId);
        Assert.Equal(CheckpointCompletion.Unavailable, result.Evidence.Checkpoints[1].Completion);
        Assert.Equal("mutatedB", result.Evidence.ActiveCheckpointId);
        Assert.True(result.RootCleanupCompleted);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task CrashPreservesLastCompleteCheckpoint()
    {
        string manifest = CreateScenario("// SGA_MODE_CRASH\nnamespace Scenario; public sealed class Beta;");
        SupervisedWorkerResult result = await new WorkerSupervisor().RunAsync(
            manifest,
            "transition",
            10,
            TestContext.Current.CancellationToken);

        Assert.Equal("WorkerCrash", result.Evidence.FailureKind);
        Assert.Equal(2, result.Evidence.Checkpoints.Count);
        Assert.Equal("transitionA", result.Evidence.Checkpoints[0].RunId);
        Assert.Equal(CheckpointCompletion.Unavailable, result.Evidence.Checkpoints[1].Completion);
        Assert.Equal("mutatedB", result.Evidence.ActiveCheckpointId);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task UserCancellationMapsTo130WhenRootCleanupCompletes()
    {
        string manifest = CreateScenario("// SGA_MODE_HANG\nnamespace Scenario; public sealed class Beta;");
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(1));
        SupervisedWorkerResult result = await new WorkerSupervisor().RunAsync(
            manifest,
            "transition",
            10,
            cancellation.Token);

        Assert.Equal("Canceled", result.Evidence.FailureKind);
        Assert.True(result.RootCleanupCompleted);
        Assert.Equal(130, result.ExitCode);
        Assert.Equal(2, result.Evidence.Checkpoints.Count);
        Assert.Equal("transitionA", result.Evidence.Checkpoints[0].RunId);
        Assert.Equal(CheckpointCompletion.Unavailable, result.Evidence.Checkpoints[1].Completion);
        Assert.Equal("mutatedB", result.Evidence.ActiveCheckpointId);
    }

    [Fact]
    public async Task CooperativeCancellationMapsTo130WithoutForcedTermination()
    {
        string manifest = CreateScenario("// SGA_MODE_COOPERATIVE_CANCEL\nnamespace Scenario; public sealed class Beta;");
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(1));
        DateTimeOffset started = DateTimeOffset.UtcNow;
        SupervisedWorkerResult result = await new WorkerSupervisor().RunAsync(
            manifest,
            "transition",
            10,
            cancellation.Token);

        Assert.Equal("Canceled", result.Evidence.FailureKind);
        Assert.True(result.RootCleanupCompleted);
        Assert.Equal(130, result.ExitCode);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void CleanupFailureOverridesPriorEvidenceAndExitCode()
    {
        WorkerRunEvidence prior = new(
            new([], AggregateAdmissionDecision.Unavailable, FixtureCoverage.NotFixtureCovered, []),
            [],
            "Timeout",
            "prior",
            "mutatedB");

        (WorkerRunEvidence evidence, int exitCode) = WorkerSupervisor.OverrideForCleanupFailure(prior);

        Assert.Equal("InternalFailure", evidence.FailureKind);
        Assert.Contains("did not terminate", evidence.FailureMessage, StringComparison.Ordinal);
        Assert.Null(evidence.ActiveCheckpointId);
        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task NonzeroExitAfterCompletedProtocolIsAWorkerCrash()
    {
        string manifest = CreateScenario(
            "// SGA_MODE_EXIT_AFTER_COMPLETED\nnamespace Scenario; public sealed class Alpha;",
            mutateBaseline: true);
        SupervisedWorkerResult result = await new WorkerSupervisor().RunAsync(
            manifest,
            "cold",
            10,
            TestContext.Current.CancellationToken);

        Assert.Equal("WorkerCrash", result.Evidence.FailureKind);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(CheckpointCompletion.Complete, Assert.Single(result.Evidence.Checkpoints).Completion);
    }

    [Fact]
    public async Task AggregateEvidenceOverflowHasTypedTerminalAndRetainsCompletedCheckpoints()
    {
        string manifest = CreateScenario(
            "// SGA_MODE_EVIDENCE_OVERFLOW\nnamespace Scenario; public sealed class Beta;",
            baselineSource: "// SGA_MODE_EVIDENCE_OVERFLOW\nnamespace Scenario; public sealed class Alpha;");
        ScenarioDefinition scenario = ScenarioLoader.Load(manifest);
        LoadedGenerator loaded = GeneratorAssemblyLoader.Load(scenario.Generator);
        WorkerRunEvidence direct = new RoslynAuditRunner(scenario, loaded.Generator, loaded.Compatibility)
            .RunTransition(TestContext.Current.CancellationToken);
        int[] frameSizes = direct.Checkpoints.Select((checkpoint, index) =>
            WorkerProtocolEmitter.MeasureCheckpointBodyBytes(checkpoint, checked((ulong)index + 2))).ToArray();
        using MemoryStream preludeBytes = new();
        WorkerProtocolEmitter prelude = new(preludeBytes);
        prelude.WriteHello("transition", ["transitionA", "mutatedB", "restoredA", "stableA"]);
        prelude.WriteAdmission(loaded.Compatibility);
        long preludeBodyBytes = preludeBytes.Length - 8;
        Assert.True(
            frameSizes.All(size => size <= WorkerProtocolEmitter.MaximumFrameBytes) &&
            frameSizes.Sum(value => checked((long)value)) + preludeBodyBytes > WorkerProtocolEmitter.MaximumWorkerBytes,
            $"Expected four individually valid frames whose aggregate exceeds 32 MiB; sizes={string.Join(',', frameSizes)}.");

        SupervisedWorkerResult result = await new WorkerSupervisor().RunAsync(
            manifest,
            "transition",
            30,
            TestContext.Current.CancellationToken);

        Assert.Equal("EvidenceLimitExceeded", result.Evidence.FailureKind);
        Assert.Equal("Worker evidence exceeds 32 MiB.", result.Evidence.FailureMessage);
        Assert.True(result.Evidence.Checkpoints.Count >= 3);
        Assert.All(result.Evidence.Checkpoints.Take(result.Evidence.Checkpoints.Count - 1), checkpoint =>
            Assert.Equal(CheckpointCompletion.Complete, checkpoint.Completion));
        Assert.Equal(CheckpointCompletion.Unavailable, result.Evidence.Checkpoints[^1].Completion);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task CompletedProtocolCannotOutliveAbsoluteWorkerDeadline()
    {
        string manifest = CreateScenario(
            "// SGA_MODE_LINGER\nnamespace Scenario; public sealed class Alpha;",
            mutateBaseline: true);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        SupervisedWorkerResult result = await new WorkerSupervisor().RunAsync(
            manifest,
            "cold",
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal("Timeout", result.Evidence.FailureKind);
        Assert.Equal("coldA", Assert.Single(result.Evidence.Checkpoints).RunId);
        Assert.True(result.RootCleanupCompleted);
        Assert.Equal(3, result.ExitCode);
        TimeSpan elapsed = DateTimeOffset.UtcNow - started;
        Assert.True(elapsed < TimeSpan.FromSeconds(8), $"Elapsed {elapsed}; failure={result.Evidence.FailureKind ?? "null"}.");
    }

    [Fact]
    public async Task DescendantInheritedStreamsCannotHoldParentOpen()
    {
        string manifest = CreateScenario(
            "// SGA_MODE_SPAWN_DESCENDANT\nnamespace Scenario; public sealed class Alpha;",
            mutateBaseline: true);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        SupervisedWorkerResult result = await new WorkerSupervisor().RunAsync(
            manifest,
            "cold",
            1,
            TestContext.Current.CancellationToken);

        TimeSpan elapsed = DateTimeOffset.UtcNow - started;
        Assert.True(elapsed < TimeSpan.FromSeconds(8), $"Elapsed {elapsed}; failure={result.Evidence.FailureKind ?? "null"}.");
        Assert.True(result.RootCleanupCompleted);
        Assert.Contains(result.Evidence.FailureKind, new string?[] { null, "Timeout" });
    }

    [Fact]
    public void WorkerHandleInheritanceSetupRequiresFourValidSuccessfulClears()
    {
        List<nint> observed = [];
        WorkerHost.RequireInheritanceCleared(
            [new nint(1), new nint(2), new nint(3), new nint(4)],
            handle =>
            {
                observed.Add(handle);
                return true;
            });

        Assert.Equal([new nint(1), new nint(2), new nint(3), new nint(4)], observed);
        Assert.Throws<InvalidOperationException>(() => WorkerHost.RequireInheritanceCleared(
            [new nint(1), nint.Zero, new nint(3), new nint(4)],
            _ => true));
        Assert.Throws<InvalidOperationException>(() => WorkerHost.RequireInheritanceCleared(
            [new nint(1), new nint(2), new nint(3), new nint(4)],
            handle => handle != new nint(3)));
    }

    [Fact]
    public async Task StdoutAndStderrAreSeparatedAndLargeOutputIsTruncated()
    {
        string smallManifest = CreateScenario(
            "// SGA_MODE_STDOUT\nnamespace Scenario; public sealed class Alpha;",
            mutateBaseline: true);
        SupervisedWorkerResult small = await new WorkerSupervisor().RunAsync(
            smallManifest,
            "cold",
            10,
            TestContext.Current.CancellationToken);
        Assert.Equal("fixture-stdout", Encoding.UTF8.GetString(Convert.FromBase64String(small.Stdout.CapturedBase64)));
        Assert.Equal("fixture-stderr", Encoding.UTF8.GetString(Convert.FromBase64String(small.Stderr.CapturedBase64)));

        string largeManifest = CreateScenario(
            "// SGA_MODE_STDOUT_LARGE\nnamespace Scenario; public sealed class Alpha;",
            mutateBaseline: true);
        SupervisedWorkerResult large = await new WorkerSupervisor().RunAsync(
            largeManifest,
            "cold",
            10,
            TestContext.Current.CancellationToken);
        Assert.True(large.Stdout.Truncated);
        Assert.Equal(1024UL * 1024UL, large.Stdout.CapturedBytes);
        Assert.Equal(4096UL, large.Stdout.DiscardedBytes);
        Assert.Equal((1024UL * 1024UL) + 4096UL, large.Stdout.TotalBytes);
    }

    private static string CreateScenario(string source, bool mutateBaseline = false, string? baselineSource = null)
    {
        string repository = FindRepositoryRoot();
        string sourceScenario = Path.Combine(repository, "tests", "scenarios", "relevant");
        string target = Path.Combine(repository, "artifacts", "test-scenarios", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        CopyDirectory(sourceScenario, target);
        string fileName = mutateBaseline ? "Input.A.cs" : "Input.B.cs";
        string sourcePath = Path.Combine(target, "inputs", fileName);
        File.WriteAllText(sourcePath, source, new UTF8Encoding(false));

        string manifestPath = Path.Combine(target, "scenario.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourcePath)));
        if (mutateBaseline)
        {
            document["baseline"]!["sources"]![0]!["sha256"] = hash;
        }
        else
        {
            document["mutation"]!["replacementSha256"] = hash;
        }

        if (baselineSource is not null)
        {
            string baselinePath = Path.Combine(target, "inputs", "Input.A.cs");
            File.WriteAllText(baselinePath, baselineSource, new UTF8Encoding(false));
            document["baseline"]!["sources"]![0]!["sha256"] = Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(baselinePath)));
        }

        File.WriteAllText(manifestPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return manifestPath;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destinationPath = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
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
