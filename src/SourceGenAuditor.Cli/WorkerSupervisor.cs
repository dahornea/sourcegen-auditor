using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Execution;
using SourceGenAuditor.Core.Protocol;
using SourceGenAuditor.Core.Reporting;

namespace SourceGenAuditor.Cli;

public sealed record SupervisedWorkerResult(
    WorkerRunEvidence Evidence,
    WorkerLogReportV1 Stdout,
    WorkerLogReportV1 Stderr,
    int ExitCode,
    bool RootCleanupCompleted,
    int ProcessId);

public sealed class WorkerSupervisor
{
    public async Task<SupervisedWorkerResult> RunAsync(
        string scenarioPath,
        string workerKind,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        string[] expectedIds = workerKind switch
        {
            "cold" => ["coldA"],
            "transition" => ["transitionA", "mutatedB", "restoredA", "stableA"],
            _ => throw new ArgumentOutOfRangeException(nameof(workerKind)),
        };
        AnonymousPipeServerStream evidencePipe = new(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        using AnonymousPipeServerStream controlPipe = new(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        startInfo.ArgumentList.Add("__worker");
        startInfo.ArgumentList.Add("--kind");
        startInfo.ArgumentList.Add(workerKind);
        startInfo.ArgumentList.Add("--scenario");
        startInfo.ArgumentList.Add(Path.GetFullPath(scenarioPath));
        startInfo.ArgumentList.Add("--evidence-handle");
        startInfo.ArgumentList.Add(evidencePipe.GetClientHandleAsString());
        startInfo.ArgumentList.Add("--control-handle");
        startInfo.ArgumentList.Add(controlPipe.GetClientHandleAsString());

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The worker process could not be started.");
        int processId = process.Id;
        evidencePipe.DisposeLocalCopyOfClientHandle();
        controlPipe.DisposeLocalCopyOfClientHandle();
        Stream stdoutStream = process.StandardOutput.BaseStream;
        Stream stderrStream = process.StandardError.BaseStream;
        Task<WorkerLogReportV1> stdoutTask = StartLongRunningCapture(stdoutStream);
        Task<WorkerLogReportV1> stderrTask = StartLongRunningCapture(stderrStream);
        WorkerProtocolProgress progress = new();
        using CancellationTokenSource checkpointTimeout = new(TimeSpan.FromSeconds(timeoutSeconds));
        using CancellationTokenSource absoluteTimeout = new(TimeSpan.FromSeconds((expectedIds.Length + 1) * timeoutSeconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            checkpointTimeout.Token,
            absoluteTimeout.Token);
        void ResetDeadline() => checkpointTimeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        WorkerRunEvidence? evidence = null;
        Exception? readFailure = null;
        bool timedOut = false;
        bool canceled = false;
        bool cleanupCompleted = true;
        bool workerExitedBeforeCleanup = false;
        bool evidencePipeDetached = false;
        Task<WorkerRunEvidence>? protocolTask = null;
        try
        {
            protocolTask = Task.Factory.StartNew(
                () => WorkerProtocolReader.ReadSessionAsync(
                    evidencePipe,
                    workerKind,
                    expectedIds,
                    linked.Token,
                    progress,
                    ResetDeadline),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            evidence = await protocolTask.WaitAsync(linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            readFailure = exception;
            canceled = cancellationToken.IsCancellationRequested;
            timedOut = !canceled;
            await SendCancelAsync(controlPipe, canceled ? "UserCancellation" : "Timeout").ConfigureAwait(false);
            DisposeInBackground(evidencePipe);
            evidencePipeDetached = true;
            ObserveBackgroundFailure(protocolTask);
            cleanupCompleted = await StopProcessAsync(process).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WorkerProtocolException or IOException)
        {
            readFailure = exception;
            workerExitedBeforeCleanup = process.HasExited;
            await SendCancelAsync(controlPipe, "Timeout").ConfigureAwait(false);
            DisposeInBackground(evidencePipe);
            evidencePipeDetached = true;
            ObserveBackgroundFailure(protocolTask);
            cleanupCompleted = await StopProcessAsync(process).ConfigureAwait(false);
        }

        if (!cleanupCompleted)
        {
            stdoutStream.Dispose();
            stderrStream.Dispose();
        }

        if (!stdoutTask.IsCompleted || !stderrTask.IsCompleted)
        {
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                stdoutStream.Dispose();
                stderrStream.Dispose();
            }
        }

        WorkerLogReportV1 stdout = await stdoutTask.ConfigureAwait(false);
        WorkerLogReportV1 stderr = await stderrTask.ConfigureAwait(false);
        int exitCode = process.HasExited ? process.ExitCode : 3;
        if (!cleanupCompleted)
        {
            WorkerRunEvidence availableEvidence = evidence ?? new WorkerRunEvidence(
                progress.Compatibility,
                progress.Checkpoints.ToArray(),
                null,
                null,
                null);
            (evidence, exitCode) = OverrideForCleanupFailure(availableEvidence);
        }
        else if (evidence is not null && readFailure is OperationCanceledException)
        {
            string kind;
            string message;
            if (canceled)
            {
                kind = "Canceled";
                message = "The audit was canceled by the user.";
                exitCode = 130;
            }
            else
            {
                kind = "Timeout";
                message = "The worker absolute lifetime or checkpoint deadline expired.";
                exitCode = 3;
            }

            evidence = evidence with
            {
                FailureKind = kind,
                FailureMessage = message,
                ActiveCheckpointId = null,
            };
        }
        else if (evidence is null)
        {
            string kind;
            string message;
            if (canceled)
            {
                kind = cleanupCompleted ? "Canceled" : "InternalFailure";
                message = cleanupCompleted ? "The audit was canceled by the user." : "The worker root did not terminate after cancellation.";
                exitCode = cleanupCompleted ? 130 : 3;
            }
            else if (timedOut)
            {
                kind = cleanupCompleted ? "Timeout" : "InternalFailure";
                message = cleanupCompleted ? "The worker checkpoint deadline expired." : "The worker root did not terminate after timeout.";
                exitCode = 3;
            }
            else if (workerExitedBeforeCleanup && exitCode != 0 &&
                readFailure is WorkerProtocolException { Message: "The worker evidence stream ended before a terminal frame." })
            {
                kind = "WorkerCrash";
                message = "The worker exited without a valid terminal message.";
                exitCode = 3;
            }
            else if (readFailure is WorkerProtocolException or IOException)
            {
                kind = "WorkerProtocolFailure";
                message = readFailure.Message;
                exitCode = 3;
            }
            else if (exitCode != 0)
            {
                kind = "WorkerCrash";
                message = "The worker exited without a valid terminal message.";
                exitCode = 3;
            }
            else
            {
                kind = "WorkerProtocolFailure";
                message = readFailure?.Message ?? "The worker protocol failed.";
                exitCode = 3;
            }

            evidence = new WorkerRunEvidence(
                progress.Compatibility,
                progress.Checkpoints.ToArray(),
                kind,
                message,
                null);
        }
        else if (evidence.FailureKind is null && exitCode != 0)
        {
            evidence = evidence with
            {
                FailureKind = "WorkerCrash",
                FailureMessage = "The worker exited nonzero after sending a completed terminal message.",
                ActiveCheckpointId = null,
            };
            exitCode = 3;
        }

        evidence = SynthesizeUnavailableActiveCheckpoint(evidence, expectedIds);
        if (!evidencePipeDetached)
        {
            evidencePipe.Dispose();
        }

        return new SupervisedWorkerResult(evidence, stdout, stderr, exitCode, cleanupCompleted, processId);
    }

    internal static (WorkerRunEvidence Evidence, int ExitCode) OverrideForCleanupFailure(WorkerRunEvidence evidence)
        => (evidence with
        {
            FailureKind = "InternalFailure",
            FailureMessage = "The worker root did not terminate during cleanup.",
            ActiveCheckpointId = null,
        }, 3);

    private static void ObserveBackgroundFailure(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Task<WorkerLogReportV1> StartLongRunningCapture(Stream stream)
        => Task.Factory.StartNew(
            () => CaptureAsync(stream),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    private static void DisposeInBackground(IDisposable disposable)
        => _ = Task.Factory.StartNew(
            disposable.Dispose,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static async Task SendCancelAsync(Stream stream, string reason)
    {
        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocolVersion = 1,
                type = "cancel",
                sequence = 0,
                payload = new { reason },
            });
            byte[] prefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)body.Length));
            await stream.WriteAsync(prefix).ConfigureAwait(false);
            await stream.WriteAsync(body).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static async Task<bool> StopProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return true;
        }

        using CancellationTokenSource cooperative = new(TimeSpan.FromSeconds(2));
        try
        {
            await process.WaitForExitAsync(cooperative.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }

        using CancellationTokenSource forced = new(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(forced.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<WorkerLogReportV1> CaptureAsync(Stream stream)
    {
        const int maximumCaptured = 1024 * 1024;
        using MemoryStream captured = new();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[16 * 1024];
        ulong total = 0;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                total = checked(total + (ulong)read);
                int remaining = maximumCaptured - checked((int)captured.Length);
                if (remaining > 0)
                {
                    captured.Write(buffer, 0, Math.Min(remaining, read));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }

        byte[] bytes = captured.ToArray();
        ulong capturedCount = checked((ulong)bytes.Length);
        return new WorkerLogReportV1(
            total,
            capturedCount,
            total - capturedCount,
            total > capturedCount,
            Convert.ToBase64String(bytes),
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    internal static WorkerRunEvidence SynthesizeUnavailableActiveCheckpoint(
        WorkerRunEvidence evidence,
        IReadOnlyList<string> expectedIds)
    {
        if (evidence.FailureKind is null || evidence.Checkpoints.LastOrDefault()?.Completion == CheckpointCompletion.Partial ||
            evidence.Checkpoints.Count >= expectedIds.Count)
        {
            return evidence;
        }

        string activeId = expectedIds[evidence.Checkpoints.Count];
        EnvironmentEvidence environment = evidence.Checkpoints.LastOrDefault()?.Environment ?? new EnvironmentEvidence(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            []);
        SourceSnapshot unavailableSources = new(SnapshotAvailability.Unavailable, "MissingPublicEvidence", [], null);
        DiagnosticSnapshot unavailableDiagnostics = new(SnapshotAvailability.Unavailable, "MissingPublicEvidence", [], null);
        CheckpointEvidence unavailable = new(
            activeId,
            CheckpointCompletion.Unavailable,
            environment,
            unavailableSources,
            unavailableDiagnostics,
            unavailableDiagnostics,
            unavailableDiagnostics,
            unavailableDiagnostics,
            new TrackedStepsSnapshot(SnapshotAvailability.Unavailable, "MissingPublicEvidence", []),
            null);
        return evidence with
        {
            Checkpoints = evidence.Checkpoints.Append(unavailable).ToArray(),
            ActiveCheckpointId = activeId,
        };
    }
}
