using System.IO.Pipes;
using System.Runtime.InteropServices;
using SourceGenAuditor.Core.Canonicalization;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Execution;
using SourceGenAuditor.Core.Protocol;
using SourceGenAuditor.Core.Scenario;

namespace SourceGenAuditor.Cli;

internal static class WorkerHost
{
    private const uint HandleFlagInherit = 0x00000001;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;

    public static int Run(string[] args)
    {
        Dictionary<string, string> options = ParseOptions(args);
        string workerKind = Require(options, "kind");
        string scenarioPath = Require(options, "scenario");
        string evidenceHandle = Require(options, "evidence-handle");
        string controlHandle = Require(options, "control-handle");
        string[] expectedIds = workerKind switch
        {
            "cold" => ["coldA"],
            "transition" => ["transitionA", "mutatedB", "restoredA", "stableA"],
            _ => throw new ArgumentException("Worker kind is invalid."),
        };

        using AnonymousPipeClientStream evidence = new(PipeDirection.Out, evidenceHandle);
        using AnonymousPipeClientStream control = new(PipeDirection.In, controlHandle);
        WorkerProtocolEmitter emitter = new(evidence);
        try
        {
            DisablePipeInheritance(evidence, control);
        }
        catch (InvalidOperationException exception)
        {
            emitter.WriteHello(workerKind, expectedIds);
            TryWriteFailure(emitter, "InternalFailure", exception.Message, null);
            return 3;
        }

        using CancellationTokenSource cancellation = new();
        using CancellationTokenSource monitorShutdown = new();
        emitter.WriteHello(workerKind, expectedIds);
        string? activeCheckpoint = null;
        Task<string?> controlTask = WorkerControlProtocol.ReadSingleAsync(control, monitorShutdown.Token);
        _ = controlTask.ContinueWith(
            static (task, state) =>
            {
                if (task.IsFaulted || task.Status == TaskStatus.RanToCompletion && task.Result is not null)
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
            },
            cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            Core.Model.ScenarioDefinition scenario = ScenarioLoader.Load(scenarioPath);
            LoadedGenerator loaded;
            try
            {
                loaded = GeneratorAssemblyLoader.Load(scenario.Generator);
            }
            catch (GeneratorCompatibilityException exception)
            {
                ObserveControl(controlTask, monitorShutdown);
                emitter.WriteAdmission(exception.Evidence);
                emitter.WriteFailure("CompatibilityFailure", exception.Message, null);
                return 3;
            }
            catch (GeneratorLoadException exception)
            {
                ObserveControl(controlTask, monitorShutdown);
                emitter.WriteFailure("LoadFailure", exception.Message, null);
                return 3;
            }

            emitter.WriteAdmission(loaded.Compatibility);
            RoslynAuditRunner runner = new(scenario, loaded.Generator, loaded.Compatibility);
            WorkerRunEvidence result = workerKind == "cold"
                ? runner.RunCold(cancellation.Token, WriteCheckpoint)
                : runner.RunTransition(cancellation.Token, WriteCheckpoint);
            if (result.FailureKind is not null)
            {
                ObserveControl(controlTask, monitorShutdown);
                emitter.WriteFailure(
                    result.FailureKind,
                    result.FailureMessage ?? string.Empty,
                    result.ActiveCheckpointId);
                return 3;
            }

            ObserveControl(controlTask, monitorShutdown);
            emitter.WriteCompleted(result.Checkpoints.Select(checkpoint => checkpoint.RunId).ToArray());
            return 0;
        }
        catch (OperationCanceledException)
        {
            if (TryObserveControlFailure(controlTask, monitorShutdown, out string? controlFailure))
            {
                TryWriteFailure(emitter, "InternalFailure", controlFailure!, null);
                return 3;
            }

            TryWriteFailure(emitter, "Canceled", "Worker cancellation was requested.", null);
            return 130;
        }
        catch (EvidenceLimitException exception)
        {
            TryWriteFailure(emitter, "EvidenceLimitExceeded", exception.Message, activeCheckpoint);
            return 3;
        }
        catch (CanonicalizationException exception)
        {
            TryWriteFailure(emitter, "CanonicalizationFailure", exception.Message, null);
            return 3;
        }
        catch (ScenarioValidationException exception)
        {
            TryWriteFailure(emitter, "LoadFailure", exception.Message, null);
            return 3;
        }
        catch (Exception exception)
        {
            string message = TryObserveControlFailure(controlTask, monitorShutdown, out string? controlFailure)
                ? controlFailure!
                : exception.Message;
            TryWriteFailure(emitter, "InternalFailure", message, null);
            return 3;
        }
        finally
        {
            cancellation.Cancel();
            monitorShutdown.Cancel();
        }

        void WriteCheckpoint(CheckpointEvidence checkpoint)
        {
            activeCheckpoint = checkpoint.RunId;
            emitter.WriteCheckpoint(checkpoint);
            activeCheckpoint = null;
        }
    }

    private static void ObserveControl(Task<string?> task, CancellationTokenSource shutdown)
    {
        if (!task.IsCompleted)
        {
            shutdown.Cancel();
        }

        try
        {
            task.WaitAsync(TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
    }

    private static bool TryObserveControlFailure(
        Task<string?> task,
        CancellationTokenSource shutdown,
        out string? message)
    {
        try
        {
            ObserveControl(task, shutdown);
            message = null;
            return false;
        }
        catch (InvalidDataException exception)
        {
            message = $"ControlProtocolFailure: {exception.Message}";
            return true;
        }
    }

    private static void TryWriteFailure(WorkerProtocolEmitter emitter, string kind, string message, string? active)
    {
        try
        {
            emitter.WriteFailure(kind, message, active);
        }
        catch (Exception exception) when (exception is IOException or EvidenceLimitException)
        {
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) ||
                !result.TryAdd(args[index][2..], args[index + 1]))
            {
                throw new ArgumentException("Worker options are malformed.");
            }
        }

        return result;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out string? value) ? value : throw new ArgumentException($"Worker option --{key} is required.");

    private static void DisablePipeInheritance(AnonymousPipeClientStream evidence, AnonymousPipeClientStream control)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint[] handles =
        [
            evidence.SafePipeHandle.DangerousGetHandle(),
            control.SafePipeHandle.DangerousGetHandle(),
            WindowsNative.GetStdHandle(StandardOutputHandle),
            WindowsNative.GetStdHandle(StandardErrorHandle),
        ];
        RequireInheritanceCleared(handles, handle => WindowsNative.SetHandleInformation(handle, HandleFlagInherit, 0));
    }

    internal static void RequireInheritanceCleared(IReadOnlyList<nint> handles, Func<nint, bool> clearInheritance)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(clearInheritance);
        if (handles.Count != 4)
        {
            throw new InvalidOperationException("Exactly four worker handles must have inheritance cleared.");
        }

        foreach (nint handle in handles)
        {
            if (handle == nint.Zero || handle == new nint(-1))
            {
                throw new InvalidOperationException("A required worker pipe or standard-stream handle is invalid.");
            }

            if (!clearInheritance(handle))
            {
                throw new InvalidOperationException("A required worker handle could not have inheritance cleared.");
            }
        }
    }

    private static class WindowsNative
    {
        private static readonly nint Kernel32 = NativeLibrary.Load("kernel32.dll");
        private static readonly GetStdHandleDelegate GetStdHandleMethod = Marshal.GetDelegateForFunctionPointer<GetStdHandleDelegate>(
            NativeLibrary.GetExport(Kernel32, "GetStdHandle"));
        private static readonly SetHandleInformationDelegate SetHandleInformationMethod = Marshal.GetDelegateForFunctionPointer<SetHandleInformationDelegate>(
            NativeLibrary.GetExport(Kernel32, "SetHandleInformation"));

        public static nint GetStdHandle(int standardHandle) => GetStdHandleMethod(standardHandle);

        public static bool SetHandleInformation(nint handle, uint mask, uint flags)
            => SetHandleInformationMethod(handle, mask, flags);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint GetStdHandleDelegate(int standardHandle);

        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool SetHandleInformationDelegate(nint handle, uint mask, uint flags);
    }
}
