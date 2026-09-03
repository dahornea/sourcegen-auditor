using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Evaluation;
using SourceGenAuditor.Core.Execution;
using SourceGenAuditor.Core.Model;

namespace SourceGenAuditor.Core.Reporting;

public sealed record ToolReportV1(string Version, string Runtime, IReadOnlyList<RoslynHostReportV1> Roslyn);

public sealed record RoslynHostReportV1(string SimpleName, string AssemblyVersion, string ModuleVersionId);

public sealed record GeneratorReportV1(string AssemblyToken, string Sha256, string TypeName);

public sealed record ControlledSourceReportV1(string LogicalPath, string Sha256);

public sealed record ControlledReferenceReportV1(string AssemblyIdentity, string Sha256);

public sealed record ControlledInputsReportV1(
    string AssemblyName,
    IReadOnlyList<ControlledSourceReportV1> Sources,
    IReadOnlyList<ControlledReferenceReportV1> References,
    ParseOptionsDefinition ParseOptions,
    CompilationOptionsDefinition CompilationOptions);

public sealed record MutationReportV1(
    string Id,
    string Kind,
    string TargetLogicalPath,
    string ReplacementSha256,
    string Relevance,
    MutationExpectationsReportV1 Expectations);

public sealed record MutationExpectationsReportV1(string GeneratedSources, string GeneratorDiagnostics);

public sealed record ScenarioReportV1(
    string Id,
    string ManifestHash,
    GeneratorReportV1 Generator,
    ControlledInputsReportV1 ControlledInputs,
    MutationReportV1 Mutation);

public sealed record RoslynReferenceDecisionV1(
    string ReferencingAssemblySha256,
    string SimpleName,
    string RequestedVersion,
    string? HostVersion,
    string AdmissionDecision);

public sealed record CompatibilityReportV1(
    IReadOnlyList<RoslynReferenceDecisionV1> RoslynReferences,
    string AggregateAdmissionDecision,
    string FixtureCoverage);

public sealed record PrivateDependencyReportV1(string SimpleName, string PathToken, string Sha256);

public sealed record EnvironmentReportV1(
    string RuntimeVersion,
    string OsDescription,
    string ProcessArchitecture,
    string Culture,
    string UiCulture,
    string TimeZoneId,
    IReadOnlyList<RoslynHostReportV1> RoslynHost,
    IReadOnlyList<PrivateDependencyReportV1> PrivateDependencies);

public sealed record GeneratedSourceReportV1(
    string HintName,
    ulong Utf16Length,
    string? EncodingName,
    ulong PreambleLength,
    string ChecksumAlgorithm,
    string RoslynChecksumHex,
    string ContentSha256);

public sealed record SourceSnapshotReportV1(
    string Availability,
    string? UnavailableReason,
    IReadOnlyList<GeneratedSourceReportV1> Records,
    string? SetSha256);

public sealed record DiagnosticSnapshotReportV1(
    string Availability,
    string? UnavailableReason,
    IReadOnlyList<DiagnosticRecordObservation> Records,
    string? SetSha256);

public sealed record TrackedStepsReportV1(
    string Availability,
    string? UnavailableReason,
    IReadOnlyList<TrackedStepObservation> Steps);

public sealed record RunEvidenceReportV1(
    string RunId,
    string Completion,
    EnvironmentReportV1 Environment,
    SourceSnapshotReportV1 Sources,
    DiagnosticSnapshotReportV1 GeneratorDiagnostics,
    DiagnosticSnapshotReportV1 RoslynFailureDiagnostics,
    DiagnosticSnapshotReportV1 InputCompilationDiagnostics,
    DiagnosticSnapshotReportV1 PostGenerationCompilationDiagnostics,
    TrackedStepsReportV1 TrackedSteps,
    GeneratorExceptionObservation? GeneratorException,
    FailureReportV1? Failure,
    WorkerLogReportV1 WorkerStdout,
    WorkerLogReportV1 WorkerStderr);

public sealed record WorkerLogReportV1(
    ulong TotalBytes,
    ulong CapturedBytes,
    ulong DiscardedBytes,
    bool Truncated,
    string CapturedBase64,
    string Sha256);

public sealed record ObservedFactReportV1(string Id, string Value, IReadOnlyList<string> EvidenceIds);

public sealed record AssertionResultReportV1(
    string Id,
    string Result,
    string ReasonCode,
    string Message,
    IReadOnlyList<string> EvidenceIds);

public sealed record FailureReportV1(string Kind, string Message, string? ActiveCheckpointId);

public sealed record AuditReportV1(
    int SchemaVersion,
    ToolReportV1 Tool,
    ScenarioReportV1 Scenario,
    CompatibilityReportV1 Compatibility,
    IReadOnlyList<RunEvidenceReportV1> Runs,
    IReadOnlyList<ObservedFactReportV1> Observations,
    IReadOnlyList<AssertionResultReportV1> Assertions,
    string Verdict,
    bool PartialEvidence,
    FailureReportV1? Failure);

public sealed class ReportWriteException : Exception
{
    public ReportWriteException(string message)
        : base(message)
    {
    }

    public ReportWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class AuditReportMapper
{
    private static readonly WorkerLogReportV1 EmptyLog = new(
        0,
        0,
        0,
        false,
        string.Empty,
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

    public static AuditReportV1 Create(
        ScenarioDefinition scenario,
        WorkerRunEvidence cold,
        WorkerRunEvidence transition,
        AuditResult result,
        WorkerLogReportV1? coldStdout = null,
        WorkerLogReportV1? coldStderr = null,
        WorkerLogReportV1? transitionStdout = null,
        WorkerLogReportV1? transitionStderr = null)
    {
        CompatibilityEvidence compatibility = cold.Compatibility;
        RoslynHostReportV1[] host = new[]
        {
            typeof(Microsoft.CodeAnalysis.ISourceGenerator).Assembly,
            typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly,
        }
        .Select(assembly => new RoslynHostReportV1(
            assembly.GetName().Name ?? string.Empty,
            assembly.GetName().Version?.ToString() ?? string.Empty,
            assembly.ManifestModule.ModuleVersionId.ToString("D").ToLowerInvariant()))
        .OrderBy(item => item.SimpleName, StringComparer.Ordinal)
        .ToArray();
        ToolReportV1 tool = new(
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.1.0",
            RuntimeInformation.FrameworkDescription,
            host);
        ScenarioReportV1 scenarioReport = new(
            scenario.Id,
            scenario.ManifestSha256,
            new GeneratorReportV1("generator:" + scenario.Generator.Sha256, scenario.Generator.Sha256, scenario.Generator.TypeName),
            new ControlledInputsReportV1(
                scenario.Baseline.AssemblyName,
                scenario.Baseline.Sources.Select(source => new ControlledSourceReportV1(source.LogicalPath, source.Sha256)).ToArray(),
                scenario.Baseline.References.Select(reference => new ControlledReferenceReportV1(reference.AssemblyIdentity, reference.Sha256)).ToArray(),
                scenario.Baseline.ParseOptions,
                scenario.Baseline.CompilationOptions),
            new MutationReportV1(
                scenario.Mutation.Id,
                "replaceSourceText",
                scenario.Mutation.TargetLogicalPath,
                scenario.Mutation.ReplacementSha256,
                scenario.Mutation.Relevance == MutationRelevance.Relevant ? "relevant" : "irrelevant",
                new MutationExpectationsReportV1(
                    scenario.Mutation.Expectations.GeneratedSources == ExpectedEffect.Changed ? "changed" : "unchanged",
                    scenario.Mutation.Expectations.GeneratorDiagnostics == ExpectedEffect.Changed ? "changed" : "unchanged")));
        CompatibilityReportV1 compatibilityReport = new(
            compatibility.RoslynReferences.Select(reference => new RoslynReferenceDecisionV1(
                reference.ReferencingAssemblySha256,
                reference.SimpleName,
                reference.RequestedVersion,
                reference.HostVersion,
                reference.AdmissionDecision.ToString())).ToArray(),
            compatibility.AggregateAdmissionDecision.ToString(),
            compatibility.FixtureCoverage.ToString());

        List<RunEvidenceReportV1> runs = [];
        runs.AddRange(cold.Checkpoints.Select((checkpoint, index) => MapRun(
            checkpoint,
            cold,
            index == cold.Checkpoints.Count - 1,
            coldStdout ?? EmptyLog,
            coldStderr ?? EmptyLog)));
        runs.AddRange(transition.Checkpoints.Select((checkpoint, index) => MapRun(
            checkpoint,
            transition,
            index == transition.Checkpoints.Count - 1,
            transitionStdout ?? EmptyLog,
            transitionStderr ?? EmptyLog)));
        return new AuditReportV1(
            1,
            tool,
            scenarioReport,
            compatibilityReport,
            runs,
            result.Observations.Select(observation => new ObservedFactReportV1(
                observation.Id,
                observation.Value,
                observation.EvidenceIds)).ToArray(),
            result.Assertions.Select(assertion => new AssertionResultReportV1(
                assertion.Id,
                assertion.Result.ToString(),
                assertion.ReasonCode,
                assertion.Message,
                assertion.EvidenceIds)).ToArray(),
            result.Verdict.ToString(),
            result.PartialEvidence,
            result.Failure is null ? null : new FailureReportV1(
                result.Failure.Kind,
                result.Failure.Message,
                result.Failure.ActiveCheckpointId));
    }

    private static RunEvidenceReportV1 MapRun(
        CheckpointEvidence checkpoint,
        WorkerRunEvidence worker,
        bool isLastCheckpoint,
        WorkerLogReportV1 stdout,
        WorkerLogReportV1 stderr)
        => new(
            checkpoint.RunId,
            checkpoint.Completion.ToString(),
            new EnvironmentReportV1(
                checkpoint.Environment.RuntimeVersion,
                checkpoint.Environment.OsDescription,
                checkpoint.Environment.ProcessArchitecture,
                checkpoint.Environment.Culture,
                checkpoint.Environment.UiCulture,
                checkpoint.Environment.TimeZoneId,
                checkpoint.Environment.RoslynHost.Select(item => new RoslynHostReportV1(
                    item.SimpleName,
                    item.AssemblyVersion,
                    item.ModuleVersionId)).ToArray(),
                checkpoint.Environment.PrivateDependencies.Select(item => new PrivateDependencyReportV1(
                    item.SimpleName,
                    item.PathToken,
                    item.Sha256)).ToArray()),
            new SourceSnapshotReportV1(
                checkpoint.Sources.Availability.ToString(),
                checkpoint.Sources.UnavailableReason,
                checkpoint.Sources.Records.Select(record => new GeneratedSourceReportV1(
                    record.HintName,
                    record.Utf16Length,
                    record.EncodingName,
                    record.PreambleLength,
                    record.ChecksumAlgorithm,
                    record.RoslynChecksumHex,
                    record.ContentSha256)).ToArray(),
                checkpoint.Sources.SetSha256),
            MapDiagnostics(checkpoint.GeneratorDiagnostics),
            MapDiagnostics(checkpoint.RoslynFailureDiagnostics),
            MapDiagnostics(checkpoint.InputCompilationDiagnostics),
            MapDiagnostics(checkpoint.PostGenerationCompilationDiagnostics),
            new TrackedStepsReportV1(
                checkpoint.TrackedSteps.Availability.ToString(),
                checkpoint.TrackedSteps.UnavailableReason,
                checkpoint.TrackedSteps.Steps),
            checkpoint.GeneratorException,
            worker.FailureKind is not null &&
                (worker.ActiveCheckpointId == checkpoint.RunId || worker.ActiveCheckpointId is null && isLastCheckpoint)
                ? new FailureReportV1(worker.FailureKind, worker.FailureMessage ?? string.Empty, worker.ActiveCheckpointId)
                : null,
            stdout,
            stderr);

    private static DiagnosticSnapshotReportV1 MapDiagnostics(DiagnosticSnapshot snapshot)
        => new(snapshot.Availability.ToString(), snapshot.UnavailableReason, snapshot.Records, snapshot.SetSha256);
}

public static class ReportRenderer
{
    public const int MaximumReportBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static byte[] RenderJson(AuditReportV1 report)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        if (bytes.Length > MaximumReportBytes)
        {
            throw new ReportWriteException("The public report exceeds 32 MiB.");
        }

        return bytes;
    }

    public static string RenderConsole(AuditReportV1 report, AuditResult result)
    {
        if (!StringComparer.Ordinal.Equals(report.Verdict, result.Verdict.ToString()) ||
            report.PartialEvidence != result.PartialEvidence)
        {
            throw new InvalidOperationException("The console projection does not match the domain result.");
        }

        StringBuilder builder = new();
        builder.AppendLine($"SourceGen Auditor {report.Tool.Version}");
        builder.AppendLine("Observed behavior under one declared controlled scenario.");
        builder.AppendLine($"Scenario: {report.Scenario.Id} manifest-sha256={report.Scenario.ManifestHash}");
        builder.AppendLine($"Generator: {report.Scenario.Generator.AssemblyToken} type={report.Scenario.Generator.TypeName}");
        builder.AppendLine($"Mutation: {report.Scenario.Mutation.Id} relevance={report.Scenario.Mutation.Relevance} target={report.Scenario.Mutation.TargetLogicalPath}");
        builder.AppendLine($"Compatibility: {report.Compatibility.AggregateAdmissionDecision} fixture={report.Compatibility.FixtureCoverage}");
        foreach (RoslynReferenceDecisionV1 reference in report.Compatibility.RoslynReferences)
        {
            builder.AppendLine($"  roslyn-reference {reference.SimpleName} requested={reference.RequestedVersion} host={reference.HostVersion ?? "null"} decision={reference.AdmissionDecision} assembly-sha256={reference.ReferencingAssemblySha256}");
        }

        foreach (RunEvidenceReportV1 run in report.Runs)
        {
            builder.AppendLine($"[run:{run.RunId}] completion={run.Completion}");
            builder.AppendLine($"  environment runtime={run.Environment.RuntimeVersion} architecture={run.Environment.ProcessArchitecture} culture={run.Environment.Culture} ui-culture={run.Environment.UiCulture} timezone={run.Environment.TimeZoneId}");
            AppendSourceSnapshot(builder, run.Sources);
            AppendDiagnosticSnapshot(builder, "generator-diagnostics", run.GeneratorDiagnostics);
            AppendDiagnosticSnapshot(builder, "roslyn-failure-diagnostics", run.RoslynFailureDiagnostics);
            AppendDiagnosticSnapshot(builder, "input-compilation-diagnostics", run.InputCompilationDiagnostics);
            AppendDiagnosticSnapshot(builder, "post-generation-compilation-diagnostics", run.PostGenerationCompilationDiagnostics);
            builder.AppendLine($"  tracked availability={run.TrackedSteps.Availability} reason={run.TrackedSteps.UnavailableReason ?? "null"}");
            foreach (TrackedStepObservation step in run.TrackedSteps.Steps)
            {
                string inputs = string.Join(',', step.Inputs.Select(input => $"{input.SourceStepName}#{input.SourceOccurrence}[{input.OutputIndex}]"));
                string outputs = string.Join(',', step.Outputs.Select(output => $"{output.Index}:{output.Reason}"));
                builder.AppendLine($"    step {step.Name}#{step.Occurrence} inputs=[{inputs}] outputs=[{outputs}]");
            }

            if (run.Failure is not null)
            {
                builder.AppendLine($"  run-failure {run.Failure.Kind} active={run.Failure.ActiveCheckpointId ?? "null"} message={run.Failure.Message}");
            }

            AppendLog(builder, "stdout", run.WorkerStdout);
            AppendLog(builder, "stderr", run.WorkerStderr);
        }

        foreach (ObservedFact observation in result.Observations)
        {
            builder.AppendLine($"[OBSERVED] {observation.Id}: {observation.Value} evidence=[{string.Join(',', observation.EvidenceIds)}]");
        }

        foreach (AssertionResult assertion in result.Assertions)
        {
            builder.AppendLine($"[{assertion.Result}] {assertion.Id}: {assertion.Message} ({assertion.ReasonCode}) evidence=[{string.Join(',', assertion.EvidenceIds)}]");
        }

        builder.AppendLine($"Partial evidence: {result.PartialEvidence.ToString().ToLowerInvariant()}");
        if (result.Failure is not null)
        {
            builder.AppendLine($"Failure: {result.Failure.Kind} active={result.Failure.ActiveCheckpointId ?? "null"} message={result.Failure.Message}");
        }

        builder.AppendLine($"Verdict: {result.Verdict}");
        return builder.ToString();
    }

    private static void AppendSourceSnapshot(StringBuilder builder, SourceSnapshotReportV1 snapshot)
    {
        builder.AppendLine($"  sources availability={snapshot.Availability} reason={snapshot.UnavailableReason ?? "null"} source-set-sha256={snapshot.SetSha256 ?? "null"}");
        foreach (GeneratedSourceReportV1 source in snapshot.Records)
        {
            builder.AppendLine($"    source {source.HintName} utf16={source.Utf16Length} encoding={source.EncodingName ?? "null"} preamble={source.PreambleLength} checksum={source.ChecksumAlgorithm}:{source.RoslynChecksumHex} content-sha256={source.ContentSha256}");
        }
    }

    private static void AppendDiagnosticSnapshot(StringBuilder builder, string label, DiagnosticSnapshotReportV1 snapshot)
    {
        builder.AppendLine($"  {label} availability={snapshot.Availability} reason={snapshot.UnavailableReason ?? "null"} set-sha256={snapshot.SetSha256 ?? "null"}");
        foreach (DiagnosticRecordObservation diagnostic in snapshot.Records)
        {
            builder.AppendLine($"    diagnostic {diagnostic.Id} severity={diagnostic.Severity} count={diagnostic.OccurrenceCount} canonical-base64={diagnostic.CanonicalRecordBase64}");
        }
    }

    private static void AppendLog(StringBuilder builder, string label, WorkerLogReportV1 log)
        => builder.AppendLine($"  worker-{label} total={log.TotalBytes} captured={log.CapturedBytes} discarded={log.DiscardedBytes} truncated={log.Truncated.ToString().ToLowerInvariant()} sha256={log.Sha256}");

    public static void WriteAtomically(string targetPath, byte[] bytes)
    {
        string fullTarget = Path.GetFullPath(targetPath);
        string? directory = Path.GetDirectoryName(fullTarget);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new ReportWriteException("The report output directory does not exist.");
        }

        string temporaryPath = $"{fullTarget}.sga-tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullTarget, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ReportWriteException("The report could not be written atomically.", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
