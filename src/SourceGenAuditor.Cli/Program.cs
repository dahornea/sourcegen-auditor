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

        if (args.Length == 2 && args[0] == "audit" && args[1] is "--help" or "-h")
        {
            WriteAuditHelp();
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
        if (args.Length < 2 || args[0] != "audit" || args[1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("Usage: sourcegen-auditor audit <scenario.json> [options]");
        }

        string format = "console";
        string? output = null;
        int timeout = 30;
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 2; index < args.Length; index += 2)
        {
            string option = args[index];
            if (!seen.Add(option))
            {
                throw new ArgumentException($"Option '{option}' may only be specified once.");
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Option '{option}' requires a value.");
            }

            string value = args[index + 1];
            switch (option)
            {
                case "--format":
                    if (value is not ("console" or "json"))
                    {
                        throw new ArgumentException($"Invalid value '{value}' for --format. Accepted values: console, json.");
                    }

                    format = value;
                    break;
                case "--output":
                    if (value.Length == 0)
                    {
                        throw new ArgumentException("Invalid value for --output. Accepted value: a non-empty file path.");
                    }

                    output = value;
                    break;
                case "--timeout":
                    if (!int.TryParse(value, out int parsed) || parsed is < 1 or > 600)
                    {
                        throw new ArgumentException($"Invalid value '{value}' for --timeout. Accepted range: 1-600 seconds.");
                    }

                    timeout = parsed;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{option}'. Accepted options: --format, --output, --timeout.");
            }
        }

        return new CliOptions(args[1], format, output, timeout);
    }

    private static void WriteHelp(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine("SourceGen Auditor audits what an incremental generator recomputes and what Roslyn reuses.");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  sourcegen-auditor audit <scenario.json> [options]");
        writer.WriteLine("  sourcegen-auditor --help");
        writer.WriteLine("  sourcegen-auditor --version");
        writer.WriteLine();
        writer.WriteLine("Command:");
        writer.WriteLine("  audit    Audit one selected generator under a declared controlled scenario.");
        writer.WriteLine();
        writer.WriteLine("Run 'sourcegen-auditor audit --help' for arguments, options, verdicts, and exit codes.");
        writer.WriteLine("Every result is bounded to the selected generator, declared scenario, and recorded environment.");
    }

    private static void WriteAuditHelp()
    {
        Console.Out.WriteLine("Audit one selected incremental generator under a declared controlled scenario.");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage:");
        Console.Out.WriteLine("  sourcegen-auditor audit <scenario.json> [options]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Argument:");
        Console.Out.WriteLine("  <scenario.json>       Manifest declaring the generator, inputs, mutation, and expectations.");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  --format <value>      Report format: console or json. Default: console.");
        Console.Out.WriteLine("  --output <path>       Atomically write the report to a file instead of stdout.");
        Console.Out.WriteLine("  --timeout <seconds>   Timeout for each worker checkpoint, 1-600. Default: 30.");
        Console.Out.WriteLine("  -h, --help            Show audit help.");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Verdicts: PASS=all required assertions passed; FAIL=complete evidence contradicted an expectation;");
        Console.Out.WriteLine("          UNKNOWN=required public evidence was unavailable; ERROR=an operational/evidence failure.");
        Console.Out.WriteLine("Exit codes: 0 PASS, 1 FAIL, 2 UNKNOWN, 3 ERROR, 64 invalid input, 130 canceled.");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Generator code runs with your permissions. Worker isolation is not a security sandbox.");
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
