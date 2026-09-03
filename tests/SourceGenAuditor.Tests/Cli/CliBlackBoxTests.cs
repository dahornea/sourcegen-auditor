using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace SourceGenAuditor.Tests.Cli;

public sealed class CliBlackBoxTests
{
    [Fact]
    public async Task VersionAndInvalidInvocationHaveExactExits()
    {
        ProcessResult version = await RunAsync("--version");
        Assert.Equal(0, version.ExitCode);
        Assert.Equal("0.1.0", version.Stdout.Trim());
        Assert.Equal(string.Empty, version.Stderr);

        ProcessResult help = await RunAsync("--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("sourcegen-auditor audit <scenario.json> [options]", help.Stdout, StringComparison.Ordinal);
        Assert.Contains("sourcegen-auditor audit --help", help.Stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, help.Stderr);

        ProcessResult invalid = await RunAsync();
        Assert.Equal(64, invalid.ExitCode);
        Assert.Contains("Usage:", invalid.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditHelpAliasesDescribeArgumentsDefaultsResultsAndExits()
    {
        ProcessResult longHelp = await RunAsync("audit", "--help");
        ProcessResult shortHelp = await RunAsync("audit", "-h");

        Assert.Equal(0, longHelp.ExitCode);
        Assert.Equal(longHelp.Stdout, shortHelp.Stdout);
        Assert.Equal(string.Empty, longHelp.Stderr);
        Assert.Equal(string.Empty, shortHelp.Stderr);
        Assert.Contains("Manifest declaring the generator, inputs, mutation, and expectations.", longHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("Report format: console or json. Default: console.", longHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("Timeout for each worker checkpoint, 1-600. Default: 30.", longHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("Atomically write the report to a file instead of stdout.", longHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("PASS=all required assertions passed", longHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("Exit codes: 0 PASS, 1 FAIL, 2 UNKNOWN, 3 ERROR, 64 invalid input, 130 canceled.", longHelp.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidOptionValuesNameAcceptedValuesOrRange()
    {
        ProcessResult timeout = await RunAsync("audit", "scenario.json", "--timeout", "601");
        ProcessResult format = await RunAsync("audit", "scenario.json", "--format", "yaml");
        ProcessResult unknown = await RunAsync("audit", "scenario.json", "--verbose", "true");

        Assert.Equal(64, timeout.ExitCode);
        Assert.Contains("Invalid value '601' for --timeout. Accepted range: 1-600 seconds.", timeout.Stderr, StringComparison.Ordinal);
        Assert.Equal(64, format.ExitCode);
        Assert.Contains("Invalid value 'yaml' for --format. Accepted values: console, json.", format.Stderr, StringComparison.Ordinal);
        Assert.Equal(64, unknown.ExitCode);
        Assert.Contains("Unknown option '--verbose'. Accepted options: --format, --output, --timeout.", unknown.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonStdoutIsCleanDeterministicAndAgreesWithConsole()
    {
        string manifest = Path.Combine(FindRepositoryRoot(), "tests", "scenarios", "relevant", "scenario.json");
        ProcessResult first = await RunAsync("audit", manifest, "--format", "json", "--timeout", "10");
        ProcessResult second = await RunAsync("audit", manifest, "--format", "json", "--timeout", "10");
        ProcessResult console = await RunAsync("audit", manifest, "--format", "console", "--timeout", "10");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(string.Empty, first.Stderr);
        Assert.Equal(first.Stdout, second.Stdout);
        using JsonDocument document = JsonDocument.Parse(first.Stdout);
        JsonElement root = document.RootElement;
        Assert.Equal("PASS", root.GetProperty("verdict").GetString());
        Assert.Equal(6, root.GetProperty("assertions").GetArrayLength());
        Assert.DoesNotContain("\"sourceText\"", first.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("textUtf8Base64", first.Stdout, StringComparison.Ordinal);
        Assert.Contains("Verdict: PASS", console.Stdout, StringComparison.Ordinal);
        Assert.Contains("Compatibility: Admitted fixture=Covered", console.Stdout, StringComparison.Ordinal);
        Assert.Contains("[run:coldA] completion=Complete", console.Stdout, StringComparison.Ordinal);
        Assert.Contains("source-set-sha256=", console.Stdout, StringComparison.Ordinal);
        Assert.Contains("tracked availability=Available", console.Stdout, StringComparison.Ordinal);
        Assert.Contains("worker-stdout total=", console.Stdout, StringComparison.Ordinal);
        Assert.True(
            console.Stdout.IndexOf("Verdict: PASS", StringComparison.Ordinal) <
            console.Stdout.IndexOf("[run:coldA] completion=Complete", StringComparison.Ordinal),
            "The console summary must precede detailed run evidence.");
        foreach (JsonElement assertion in root.GetProperty("assertions").EnumerateArray())
        {
            Assert.Contains($"[PASS] {assertion.GetProperty("id").GetString()}:", console.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OutputWriteFailureIsExit3AndOwnedTemporaryFileIsRemoved()
    {
        string repository = FindRepositoryRoot();
        string manifest = Path.Combine(repository, "tests", "scenarios", "relevant", "scenario.json");
        string directoryTarget = Path.Combine(repository, "artifacts");
        string temporaryPrefix = Path.GetFileName(directoryTarget) + ".sga-tmp-";

        ProcessResult result = await RunAsync("audit", manifest, "--format", "json", "--output", directoryTarget, "--timeout", "10");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("ReportWriteFailure", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(repository, temporaryPrefix + "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InvalidManifestAndAssertionFailureHaveExactPublicExits()
    {
        ProcessResult invalid = await RunAsync("audit", Path.Combine(FindRepositoryRoot(), "missing-scenario.json"), "--format", "json");
        Assert.Equal(64, invalid.ExitCode);
        Assert.Contains("Invalid scenario:", invalid.Stderr, StringComparison.Ordinal);

        string failing = CreateScenario(document =>
            document["mutation"]!["expectations"]!["generatedSources"] = "unchanged");
        ProcessResult failed = await RunAsync("audit", failing, "--format", "json", "--timeout", "10");
        Assert.Equal(1, failed.ExitCode);
        using JsonDocument failedReport = JsonDocument.Parse(failed.Stdout);
        Assert.Equal("FAIL", failedReport.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task UnsupportedPublicDiagnosticEvidenceMapsToUnknownExitTwo()
    {
        string manifest = CreateScenario(
            _ => { },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Input.A.cs"] = "// SGA_MODE_UNSUPPORTED_LOCATION\nnamespace Scenario; public sealed class Alpha;",
                ["Input.B.cs"] = "// SGA_MODE_UNSUPPORTED_LOCATION\nnamespace Scenario; public sealed class Beta;",
            });

        ProcessResult result = await RunAsync("audit", manifest, "--format", "json", "--timeout", "10");

        Assert.Equal(2, result.ExitCode);
        using JsonDocument report = JsonDocument.Parse(result.Stdout);
        Assert.Equal("UNKNOWN", report.RootElement.GetProperty("verdict").GetString());
        Assert.Contains(report.RootElement.GetProperty("runs").EnumerateArray(), run =>
            run.GetProperty("generatorDiagnostics").GetProperty("unavailableReason").GetString() == "UnsupportedLocationKind");
    }

    [Theory]
    [InlineData("load", "LoadFailure", 10)]
    [InlineData("generator", "GeneratorException", 10)]
    [InlineData("timeout", "Timeout", 1)]
    [InlineData("crash", "WorkerCrash", 10)]
    public async Task OperationalFailuresHaveExitThreeAndTypedReports(string mode, string expectedKind, int timeout)
    {
        string manifest = mode switch
        {
            "load" => CreateScenario(document =>
                document["generator"]!["typeName"] = "SourceGenAuditor.Fixtures.MissingGenerator"),
            "generator" => CreateScenario(
                _ => { },
                new Dictionary<string, string> { ["Input.B.cs"] = "// SGA_MODE_THROW_AFTER\nnamespace Scenario; public sealed class Beta;" }),
            "timeout" => CreateScenario(
                _ => { },
                new Dictionary<string, string> { ["Input.B.cs"] = "// SGA_MODE_HANG\nnamespace Scenario; public sealed class Beta;" }),
            "crash" => CreateScenario(
                _ => { },
                new Dictionary<string, string> { ["Input.B.cs"] = "// SGA_MODE_CRASH\nnamespace Scenario; public sealed class Beta;" }),
            _ => throw new InvalidOperationException(),
        };

        ProcessResult result = await RunAsync("audit", manifest, "--format", "json", "--timeout", timeout.ToString());

        Assert.Equal(3, result.ExitCode);
        using JsonDocument report = JsonDocument.Parse(result.Stdout);
        Assert.Equal("ERROR", report.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(expectedKind, report.RootElement.GetProperty("failure").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task PublicConsoleCancellationWritesErrorReportAndReturns130()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string manifest = CreateScenario(
            _ => { },
            new Dictionary<string, string> { ["Input.B.cs"] = "// SGA_MODE_HANG\nnamespace Scenario; public sealed class Beta;" });
        string reportPath = Path.Combine(Path.GetDirectoryName(manifest)!, "canceled.json");
        string cliPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SourceGenAuditor.Cli",
            "bin",
            "Release",
            "net10.0",
            "SourceGenAuditor.Cli.dll");
        string dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        string commandLine = string.Join(
            ' ',
            new[] { dotnetHost, cliPath, "audit", manifest, "--format", "json", "--output", reportPath, "--timeout", "30" }
                .Select(QuoteWindowsArgument));
        NativeMethods.StartupInfo startupInfo = new()
        {
            Size = checked((uint)Marshal.SizeOf<NativeMethods.StartupInfo>()),
        };
        bool created = NativeMethods.CreateProcess(
            null,
            new StringBuilder(commandLine),
            nint.Zero,
            nint.Zero,
            inheritHandles: false,
            NativeMethods.CreateNewProcessGroup,
            nint.Zero,
            FindRepositoryRoot(),
            ref startupInfo,
            out NativeMethods.ProcessInformation processInformation);
        Assert.True(created, $"CreateProcessW failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        NativeMethods.CloseHandle(processInformation.ThreadHandle);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.Equal(NativeMethods.WaitTimeout, NativeMethods.WaitForSingleObject(processInformation.ProcessHandle, 0));
            bool signaled = NativeMethods.GenerateConsoleCtrlEvent(
                NativeMethods.CtrlBreakEvent,
                processInformation.ProcessId);
            Assert.True(signaled, $"GenerateConsoleCtrlEvent failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            Assert.Equal(
                NativeMethods.WaitObject0,
                NativeMethods.WaitForSingleObject(processInformation.ProcessHandle, 15_000));
            Assert.True(NativeMethods.GetExitCodeProcess(processInformation.ProcessHandle, out uint exitCode));
            Assert.Equal(130, unchecked((int)exitCode));
            Assert.True(File.Exists(reportPath), "The best-effort cancellation report was not written.");
            using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
            Assert.Equal("ERROR", report.RootElement.GetProperty("verdict").GetString());
            Assert.Equal("Canceled", report.RootElement.GetProperty("failure").GetProperty("kind").GetString());
        }
        finally
        {
            if (NativeMethods.WaitForSingleObject(processInformation.ProcessHandle, 0) == NativeMethods.WaitTimeout)
            {
                using Process process = Process.GetProcessById(checked((int)processInformation.ProcessId));
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            NativeMethods.CloseHandle(processInformation.ProcessHandle);
        }
    }

    private static async Task<ProcessResult> RunAsync(params string[] arguments)
    {
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SourceGenAuditor.Cli",
            "bin",
            "Release",
            "net10.0",
            "SourceGenAuditor.Cli.dll"));
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static string QuoteWindowsArgument(string value)
    {
        StringBuilder quoted = new("\"");
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', checked((backslashes * 2) + 1));
                quoted.Append(character);
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes);
            quoted.Append(character);
            backslashes = 0;
        }

        quoted.Append('\\', checked(backslashes * 2));
        quoted.Append('"');
        return quoted.ToString();
    }

    private static string CreateScenario(
        Action<JsonObject> mutateManifest,
        IReadOnlyDictionary<string, string>? sourceReplacements = null)
    {
        string repository = FindRepositoryRoot();
        string source = Path.Combine(repository, "tests", "scenarios", "relevant");
        string target = Path.Combine(repository, "artifacts", "cli-scenarios", Guid.NewGuid().ToString("N"));
        CopyDirectory(source, target);
        string manifestPath = Path.Combine(target, "scenario.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

        if (sourceReplacements is not null)
        {
            foreach ((string fileName, string contents) in sourceReplacements)
            {
                string path = Path.Combine(target, "inputs", fileName);
                File.WriteAllText(path, contents, new UTF8Encoding(false));
                string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
                if (fileName == "Input.A.cs")
                {
                    document["baseline"]!["sources"]![0]!["sha256"] = hash;
                }
                else if (fileName == "Input.B.cs")
                {
                    document["mutation"]!["replacementSha256"] = hash;
                }
            }
        }

        mutateManifest(document);
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

    private static class NativeMethods
    {
        internal const uint CreateNewProcessGroup = 0x00000200;
        internal const uint CtrlBreakEvent = 1;
        internal const uint WaitObject0 = 0;
        internal const uint WaitTimeout = 258;
        private static readonly nint Kernel32 = NativeLibrary.Load("kernel32.dll");
        private static readonly CreateProcessDelegate CreateProcessFunction = Marshal.GetDelegateForFunctionPointer<CreateProcessDelegate>(
            NativeLibrary.GetExport(Kernel32, "CreateProcessW"));
        private static readonly GenerateConsoleCtrlEventDelegate GenerateConsoleCtrlEventFunction = Marshal.GetDelegateForFunctionPointer<GenerateConsoleCtrlEventDelegate>(
            NativeLibrary.GetExport(Kernel32, "GenerateConsoleCtrlEvent"));
        private static readonly WaitForSingleObjectDelegate WaitForSingleObjectFunction = Marshal.GetDelegateForFunctionPointer<WaitForSingleObjectDelegate>(
            NativeLibrary.GetExport(Kernel32, "WaitForSingleObject"));
        private static readonly GetExitCodeProcessDelegate GetExitCodeProcessFunction = Marshal.GetDelegateForFunctionPointer<GetExitCodeProcessDelegate>(
            NativeLibrary.GetExport(Kernel32, "GetExitCodeProcess"));
        private static readonly CloseHandleDelegate CloseHandleFunction = Marshal.GetDelegateForFunctionPointer<CloseHandleDelegate>(
            NativeLibrary.GetExport(Kernel32, "CloseHandle"));

        internal static bool CreateProcess(
            string? applicationName,
            StringBuilder commandLine,
            nint processAttributes,
            nint threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            nint environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation)
            => CreateProcessFunction(
                applicationName,
                commandLine,
                processAttributes,
                threadAttributes,
                inheritHandles,
                creationFlags,
                environment,
                currentDirectory,
                ref startupInfo,
                out processInformation);

        internal static bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId)
            => GenerateConsoleCtrlEventFunction(controlEvent, processGroupId);

        internal static uint WaitForSingleObject(nint handle, uint milliseconds)
            => WaitForSingleObjectFunction(handle, milliseconds);

        internal static bool GetExitCodeProcess(nint processHandle, out uint exitCode)
            => GetExitCodeProcessFunction(processHandle, out exitCode);

        internal static bool CloseHandle(nint handle)
            => CloseHandleFunction(handle);

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            internal uint Size;
            private nint Reserved;
            private nint Desktop;
            private nint Title;
            private uint X;
            private uint Y;
            private uint XSize;
            private uint YSize;
            private uint XCountChars;
            private uint YCountChars;
            private uint FillAttribute;
            private uint Flags;
            private ushort ShowWindow;
            private ushort ReservedSize;
            private nint ReservedBytes;
            private nint StandardInput;
            private nint StandardOutput;
            private nint StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct ProcessInformation
        {
            internal readonly nint ProcessHandle;
            internal readonly nint ThreadHandle;
            internal readonly uint ProcessId;
            private readonly uint ThreadId;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool CreateProcessDelegate(
            string? applicationName,
            StringBuilder commandLine,
            nint processAttributes,
            nint threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            nint environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool GenerateConsoleCtrlEventDelegate(uint controlEvent, uint processGroupId);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint WaitForSingleObjectDelegate(nint handle, uint milliseconds);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool GetExitCodeProcessDelegate(nint processHandle, out uint exitCode);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool CloseHandleDelegate(nint handle);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
