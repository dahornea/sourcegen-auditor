using System.Buffers.Binary;
using SourceGenAuditor.Cli;
using SourceGenAuditor.Core.Evaluation;
using SourceGenAuditor.Core.Protocol;
using SourceGenAuditor.Core.Reporting;
using SourceGenAuditor.Core.Scenario;
using Xunit;

namespace SourceGenAuditor.Tests.Reporting;

public sealed class BoundedArtifactTests
{
    [Fact]
    public async Task ApprovedFixtureFramesAndReportStayUnderLockedLimits()
    {
        string manifest = Path.Combine(FindRepositoryRoot(), "tests", "scenarios", "relevant", "scenario.json");
        Core.Model.ScenarioDefinition scenario = ScenarioLoader.Load(manifest);
        WorkerSupervisor supervisor = new();
        SupervisedWorkerResult cold = await supervisor.RunAsync(manifest, "cold", 10, TestContext.Current.CancellationToken);
        SupervisedWorkerResult transition = await supervisor.RunAsync(manifest, "transition", 10, TestContext.Current.CancellationToken);
        AuditResult result = AuditEvaluator.Evaluate(scenario, cold.Evidence, transition.Evidence);
        AuditReportV1 report = AuditReportMapper.Create(scenario, cold.Evidence, transition.Evidence, result);
        byte[] reportBytes = ReportRenderer.RenderJson(report);

        Assert.True(reportBytes.Length < ReportRenderer.MaximumReportBytes);
        Assert.Equal("PASS", report.Verdict);
        AssertFrames(cold.Evidence, "cold", ["coldA"]);
        AssertFrames(transition.Evidence, "transition", ["transitionA", "mutatedB", "restoredA", "stableA"]);
    }

    [Fact]
    public async Task ReportOverflowUsesTypedFailure()
    {
        string manifest = Path.Combine(FindRepositoryRoot(), "tests", "scenarios", "relevant", "scenario.json");
        Core.Model.ScenarioDefinition scenario = ScenarioLoader.Load(manifest);
        WorkerSupervisor supervisor = new();
        SupervisedWorkerResult cold = await supervisor.RunAsync(manifest, "cold", 10, TestContext.Current.CancellationToken);
        SupervisedWorkerResult transition = await supervisor.RunAsync(manifest, "transition", 10, TestContext.Current.CancellationToken);
        AuditResult result = AuditEvaluator.Evaluate(scenario, cold.Evidence, transition.Evidence);
        AuditReportV1 report = AuditReportMapper.Create(scenario, cold.Evidence, transition.Evidence, result) with
        {
            Failure = new FailureReportV1("ReportWriteFailure", new string('x', ReportRenderer.MaximumReportBytes), null),
        };

        Assert.Throws<ReportWriteException>(() => ReportRenderer.RenderJson(report));
    }

    private static void AssertFrames(Core.Execution.WorkerRunEvidence evidence, string workerKind, IReadOnlyList<string> ids)
    {
        using MemoryStream stream = new();
        WorkerProtocolEmitter emitter = new(stream);
        emitter.WriteHello(workerKind, ids);
        emitter.WriteAdmission(evidence.Compatibility);
        foreach (Core.Execution.CheckpointEvidence checkpoint in evidence.Checkpoints)
        {
            emitter.WriteCheckpoint(checkpoint);
        }

        emitter.WriteCompleted(ids);
        byte[] bytes = stream.ToArray();
        int offset = 0;
        int bodyTotal = 0;
        while (offset < bytes.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            offset += 4;
            Assert.InRange(length, 1, WorkerProtocolEmitter.MaximumFrameBytes);
            bodyTotal += length;
            offset += length;
        }

        Assert.Equal(bytes.Length, offset);
        Assert.InRange(bodyTotal, 1, WorkerProtocolEmitter.MaximumWorkerBytes);
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
