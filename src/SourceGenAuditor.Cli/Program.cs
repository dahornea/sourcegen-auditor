using System.Text;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Evaluation;
using SourceGenAuditor.Core.Execution;
using SourceGenAuditor.Core.Reporting;
using SourceGenAuditor.Core.Scenario;

namespace SourceGenAuditor.Cli;

internal static class Program
{
    private const string ToolVersion = "0.1.0";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "__worker")
        {
            return WorkerHost.Run(args);
        }

        if (args.Length == 1 && args[0] == "--version")
        {
            Console.Out.WriteLine(ToolVersion);
            return 0;
        }

        if (args.Length == 1 && args[0] == "--help")
        {
            WriteHelp();
            return 0;
        }

        CliOptions options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteHelp(Console.Error);
            return 64;
        }

        ScenarioLease scenarioLease;
        try
        {
            scenarioLease = ScenarioLoader.AcquireLease(options.ScenarioPath);
        }
        catch (ScenarioValidationException exception)
        {
            Console.Error.WriteLine($"Invalid scenario: {exception.Message}");
            return 64;
        }

        using (scenarioLease)
        {
            return await RunAuditAsync(scenarioLease.Scenario, options).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunAuditAsync(Core.Model.ScenarioDefinition scenario, CliOptions options)
    {

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            WorkerSupervisor supervisor = new();
            SupervisedWorkerResult cold = await supervisor.RunAsync(
                scenario.ManifestPath,
                "cold",
                options.TimeoutSeconds,
                cancellation.Token).ConfigureAwait(false);
            SupervisedWorkerResult transition = cold.Evidence.FailureKind is null
                ? await supervisor.RunAsync(
                    scenario.ManifestPath,
                    "transition",
                    options.TimeoutSeconds,
                    cancellation.Token).ConfigureAwait(false)
                : CreateSkippedTransition(cold.Evidence.Compatibility);

            WorkerRunEvidence transitionEvidence = transition.Evidence;
            if (!CompatibilityEvidenceComparer.MatchesAdmission(cold.Evidence.Compatibility, transition.Evidence.Compatibility))
            {
                transitionEvidence = transition.Evidence with
                {
                    FailureKind = "WorkerProtocolFailure",
                    FailureMessage = "Cold and transition worker admission evidence differ.",
                    ActiveCheckpointId = null,
                };
            }

            AuditResult result = AuditEvaluator.Evaluate(scenario, cold.Evidence, transitionEvidence);
            AuditReportV1 report = AuditReportMapper.Create(
                scenario,
                cold.Evidence,
                transitionEvidence,
                result,
                cold.Stdout,
                cold.Stderr,
                transition.Stdout,
                transition.Stderr);
            byte[] rendered = options.Format == "json"
                ? ReportRenderer.RenderJson(report)
                : new UTF8Encoding(false).GetBytes(ReportRenderer.RenderConsole(report, result));
            if (options.OutputPath is null)
            {
                Stream output = Console.OpenStandardOutput();
                await output.WriteAsync(rendered).ConfigureAwait(false);
                if (rendered.Length == 0 || rendered[^1] != (byte)'\n')
                {
                    await output.WriteAsync(new byte[] { (byte)'\n' }).ConfigureAwait(false);
                }
            }
            else
            {
                ReportRenderer.WriteAtomically(options.OutputPath, rendered);
            }

            if (cold.ExitCode == 130 || transition.ExitCode == 130)
            {
                return 130;
            }

            return ExitCodeMapper.FromVerdict(result.Verdict);
        }
        catch (ReportWriteException exception)
        {
            Console.Error.WriteLine($"ReportWriteFailure: {exception.Message}");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"InternalFailure: {exception.Message}");
            return 3;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static CliOptions Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != "audit" || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Usage: sourcegen-auditor audit <scenario.json> [options]");
        }

        string format = "console";
        string? output = null;
        int timeout = 30;
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !seen.Add(args[index]))
            {
                throw new ArgumentException("CLI options are missing values or duplicated.");
            }

            string value = args[index + 1];
            switch (args[index])
            {
                case "--format" when value is "console" or "json":
                    format = value;
                    break;
                case "--output" when value.Length > 0:
                    output = value;
                    break;
                case "--timeout" when int.TryParse(value, out int parsed) && parsed is >= 1 and <= 600:
                    timeout = parsed;
                    break;
                default:
                    throw new ArgumentException($"Unknown or invalid option '{args[index]}'.");
            }
        }

        return new CliOptions(args[1], format, output, timeout);
    }

    private static void WriteHelp(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine("SourceGen Auditor audits observed generator behavior under one declared controlled scenario.");
        writer.WriteLine("Usage: sourcegen-auditor audit <scenario.json> [--format console|json] [--output <path>] [--timeout <seconds>]");
        writer.WriteLine("       sourcegen-auditor --help");
        writer.WriteLine("       sourcegen-auditor --version");
    }

    private static SupervisedWorkerResult CreateSkippedTransition(CompatibilityEvidence compatibility)
    {
        const string emptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        WorkerLogReportV1 emptyLog = new(0, 0, 0, false, string.Empty, emptyHash);
        return new SupervisedWorkerResult(
            new WorkerRunEvidence(compatibility, [], null, null, null),
            emptyLog,
            emptyLog,
            0,
            true,
            0);
    }

    private sealed record CliOptions(string ScenarioPath, string Format, string? OutputPath, int TimeoutSeconds);
}
