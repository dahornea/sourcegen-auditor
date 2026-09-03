using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SourceGenAuditor.Core.Canonicalization;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Execution;
using SourceGenAuditor.Core.Reporting;

namespace SourceGenAuditor.Core.Protocol;

public sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException(string message)
        : base(message)
    {
    }

    public WorkerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class EvidenceLimitException : Exception
{
    public EvidenceLimitException(string message)
        : base(message)
    {
    }
}

public sealed class WorkerProtocolProgress
{
    public CompatibilityEvidence Compatibility { get; internal set; } = new(
        [],
        AggregateAdmissionDecision.Unavailable,
        FixtureCoverage.NotFixtureCovered,
        []);

    public List<CheckpointEvidence> Checkpoints { get; } = [];
}

public sealed class WorkerProtocolEmitter
{
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
    public const int MaximumWorkerBytes = 32 * 1024 * 1024;
    public const int ReservedTerminalBytes = 64 * 1024;
    private readonly Stream stream;
    private ulong sequence;
    private int totalBytes;

    public WorkerProtocolEmitter(Stream stream)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public void WriteHello(string workerKind, IReadOnlyList<string> expectedCheckpointIds)
        => Write("hello", new HelloPayload(workerKind, expectedCheckpointIds));

    public void WriteAdmission(CompatibilityEvidence evidence)
        => Write("admission", new AdmissionPayload(
            evidence.RoslynReferences.Select(reference => new RoslynReferenceDto(
                reference.ReferencingAssemblySha256,
                reference.SimpleName,
                reference.RequestedVersion,
                reference.HostVersion,
                reference.AdmissionDecision.ToString())).ToArray(),
            evidence.AggregateAdmissionDecision.ToString(),
            evidence.FixtureCoverage.ToString()));

    public void WriteCheckpoint(CheckpointEvidence checkpoint)
        => Write("checkpoint", new CheckpointPayload(
            checkpoint.RunId,
            checkpoint.Completion.ToString(),
            ProtocolCheckpointEvidence.From(checkpoint)));

    public void WriteCompleted(IReadOnlyList<string> completedCheckpointIds)
        => Write("completed", new CompletedPayload(completedCheckpointIds), terminal: true);

    public void WriteFailure(string failureKind, string message, string? activeCheckpointId)
        => Write("failure", new FailurePayload(failureKind, message, activeCheckpointId), terminal: true);

    internal static int MeasureCheckpointBodyBytes(CheckpointEvidence checkpoint, ulong sequence)
        => JsonSerializer.SerializeToUtf8Bytes(
            new Envelope<CheckpointPayload>(
                1,
                "checkpoint",
                sequence,
                new CheckpointPayload(checkpoint.RunId, checkpoint.Completion.ToString(), ProtocolCheckpointEvidence.From(checkpoint))),
            ProtocolJson.Options).Length;

    private void Write<T>(string type, T payload, bool terminal = false)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new Envelope<T>(1, type, sequence, payload),
            ProtocolJson.Options);
        if (body.Length > MaximumFrameBytes)
        {
            throw new EvidenceLimitException("A worker evidence frame exceeds 8 MiB.");
        }

        int proposedTotal = checked(totalBytes + body.Length);
        int allowedTotal = terminal ? MaximumWorkerBytes : MaximumWorkerBytes - ReservedTerminalBytes;
        if (proposedTotal > allowedTotal)
        {
            throw new EvidenceLimitException("Worker evidence exceeds 32 MiB.");
        }

        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)body.Length));
        stream.Write(prefix);
        stream.Write(body);
        stream.Flush();
        totalBytes = proposedTotal;
        sequence++;
    }
}

public static class WorkerProtocolReader
{
    public static async Task<WorkerRunEvidence> ReadSessionAsync(
        Stream stream,
        string expectedWorkerKind,
        IReadOnlyList<string> expectedCheckpointIds,
        CancellationToken cancellationToken,
        WorkerProtocolProgress? progress = null,
        Action? validatedProgress = null)
    {
        try
        {
            return await ReadSessionCoreAsync(
                stream,
                expectedWorkerKind,
                expectedCheckpointIds,
                cancellationToken,
                progress,
                validatedProgress).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not WorkerProtocolException and not OperationCanceledException &&
            exception is (JsonException or ArgumentException or InvalidOperationException or FormatException or
                OverflowException or DecoderFallbackException or CanonicalizationException or NullReferenceException))
        {
            throw new WorkerProtocolException("Worker evidence violates protocol V1.", exception);
        }
    }

    private static async Task<WorkerRunEvidence> ReadSessionCoreAsync(
        Stream stream,
        string expectedWorkerKind,
        IReadOnlyList<string> expectedCheckpointIds,
        CancellationToken cancellationToken,
        WorkerProtocolProgress? progress,
        Action? validatedProgress)
    {
        progress ??= new WorkerProtocolProgress();
        ulong expectedSequence = 0;
        int totalBytes = 0;
        bool helloSeen = false;
        bool admissionSeen = false;
        bool terminalSeen = false;
        CompatibilityEvidence compatibility = new([], AggregateAdmissionDecision.Unavailable, FixtureCoverage.NotFixtureCovered, []);
        List<CheckpointEvidence> checkpoints = [];
        string? failureKind = null;
        string? failureMessage = null;
        string? activeCheckpointId = null;

        while (!terminalSeen)
        {
            byte[] body = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false)
                ?? throw new WorkerProtocolException("The worker evidence stream ended before a terminal frame.");
            totalBytes = checked(totalBytes + body.Length);
            if (totalBytes > WorkerProtocolEmitter.MaximumWorkerBytes)
            {
                throw new WorkerProtocolException("The worker evidence stream exceeds 32 MiB.");
            }

            using JsonDocument document = ProtocolJson.ParseStrict(body);
            JsonElement root = document.RootElement;
            ProtocolJson.RequireProperties(root, "protocolVersion", "type", "sequence", "payload");
            if (!root.GetProperty("protocolVersion").TryGetInt32(out int version) || version != 1)
            {
                throw new WorkerProtocolException("The protocol version is not 1.");
            }

            if (!root.GetProperty("sequence").TryGetUInt64(out ulong actualSequence) ||
                actualSequence > ProtocolJson.MaximumJsonInteger || actualSequence != expectedSequence)
            {
                throw new WorkerProtocolException("The protocol sequence is invalid or noncontiguous.");
            }

            expectedSequence++;
            if (root.GetProperty("type").ValueKind != JsonValueKind.String || root.GetProperty("payload").ValueKind != JsonValueKind.Object)
            {
                throw new WorkerProtocolException("The protocol type or payload is invalid.");
            }

            string type = root.GetProperty("type").GetString()!;
            JsonElement payload = root.GetProperty("payload");
            if (!helloSeen)
            {
                if (type != "hello" || actualSequence != 0)
                {
                    throw new WorkerProtocolException("The first worker message must be hello sequence zero.");
                }

                HelloPayload hello = ProtocolJson.Deserialize<HelloPayload>(payload);
                if (hello.WorkerKind != expectedWorkerKind || !hello.ExpectedCheckpointIds.SequenceEqual(expectedCheckpointIds, StringComparer.Ordinal))
                {
                    throw new WorkerProtocolException("The worker hello does not match the parent request.");
                }

                helloSeen = true;
                validatedProgress?.Invoke();
                continue;
            }

            if (checkpoints.LastOrDefault()?.Completion == CheckpointCompletion.Partial && type != "failure")
            {
                throw new WorkerProtocolException("A partial checkpoint must be followed immediately by terminal failure.");
            }

            if (type == "admission")
            {
                if (admissionSeen || checkpoints.Count != 0)
                {
                    throw new WorkerProtocolException("Admission is duplicate or late.");
                }

                compatibility = MapAdmission(ProtocolJson.Deserialize<AdmissionPayload>(payload));
                progress.Compatibility = compatibility;
                admissionSeen = true;
                continue;
            }

            if (type == "checkpoint")
            {
                if (!admissionSeen || compatibility.AggregateAdmissionDecision != AggregateAdmissionDecision.Admitted ||
                    checkpoints.Count >= expectedCheckpointIds.Count)
                {
                    throw new WorkerProtocolException("A checkpoint arrived in an invalid protocol state.");
                }

                CheckpointPayload checkpointPayload = ProtocolJson.Deserialize<CheckpointPayload>(payload);
                string expectedId = expectedCheckpointIds[checkpoints.Count];
                if (checkpointPayload.CheckpointId != expectedId || checkpointPayload.Evidence.RunId != expectedId)
                {
                    throw new WorkerProtocolException("A checkpoint identifier is missing, duplicate, or out of order.");
                }

                CheckpointEvidence checkpoint = checkpointPayload.Evidence.ToDomain(checkpointPayload.Completion);
                ProtocolJson.ValidateCheckpoint(checkpoint);
                checkpoints.Add(checkpoint);
                progress.Checkpoints.Add(checkpoint);
                if (checkpoint.Completion == CheckpointCompletion.Complete)
                {
                    validatedProgress?.Invoke();
                }
                continue;
            }

            if (type == "completed")
            {
                if (!admissionSeen || compatibility.AggregateAdmissionDecision != AggregateAdmissionDecision.Admitted ||
                    checkpoints.Count != expectedCheckpointIds.Count || checkpoints.Any(checkpoint => checkpoint.Completion != CheckpointCompletion.Complete))
                {
                    throw new WorkerProtocolException("The completed terminal message has incomplete state.");
                }

                CompletedPayload completed = ProtocolJson.Deserialize<CompletedPayload>(payload);
                if (!completed.CompletedCheckpointIds.SequenceEqual(expectedCheckpointIds, StringComparer.Ordinal))
                {
                    throw new WorkerProtocolException("The completed checkpoint list is invalid.");
                }

                terminalSeen = true;
                continue;
            }

            if (type == "failure")
            {
                FailurePayload failure = ProtocolJson.Deserialize<FailurePayload>(payload);
                ProtocolJson.ValidateFailureKind(failure.FailureKind);
                if (failure.Message is null)
                {
                    throw new WorkerProtocolException("A worker failure message must be a string.");
                }
                bool rejectedAdmission = admissionSeen && compatibility.AggregateAdmissionDecision == AggregateAdmissionDecision.Rejected;
                bool partial = checkpoints.LastOrDefault()?.Completion == CheckpointCompletion.Partial;
                if (rejectedAdmission && failure.FailureKind != "CompatibilityFailure")
                {
                    throw new WorkerProtocolException("Rejected admission must terminate with CompatibilityFailure.");
                }

                if (partial && failure.ActiveCheckpointId != checkpoints[^1].RunId)
                {
                    throw new WorkerProtocolException("A partial checkpoint failure must name that active checkpoint.");
                }


                if (partial && ((failure.FailureKind == "GeneratorException") != (checkpoints[^1].GeneratorException is not null)))
                {
                    throw new WorkerProtocolException("GeneratorException evidence and the terminal failure kind disagree.");
                }

                if (!partial && failure.FailureKind == "GeneratorException")
                {
                    throw new WorkerProtocolException("GeneratorException requires a preceding partial checkpoint.");
                }

                if (!partial && failure.ActiveCheckpointId is not null)
                {
                    string? nextExpected = checkpoints.Count < expectedCheckpointIds.Count
                        ? expectedCheckpointIds[checkpoints.Count]
                        : null;
                    if (failure.ActiveCheckpointId != nextExpected)
                    {
                        throw new WorkerProtocolException("A failure names an invalid active checkpoint.");
                    }
                }

                if (!admissionSeen && failure.FailureKind != "LoadFailure")
                {
                    throw new WorkerProtocolException("Only LoadFailure may terminate before admission.");
                }

                if (admissionSeen && failure.FailureKind == "LoadFailure")
                {
                    throw new WorkerProtocolException("LoadFailure may terminate only before admission.");
                }

                if (admissionSeen && !rejectedAdmission && failure.FailureKind == "CompatibilityFailure")
                {
                    throw new WorkerProtocolException("CompatibilityFailure requires rejected admission evidence.");
                }

                failureKind = failure.FailureKind;
                failureMessage = failure.Message;
                activeCheckpointId = failure.ActiveCheckpointId;
                terminalSeen = true;
                continue;
            }

            throw new WorkerProtocolException("The worker message type is unknown.");
        }

        byte[] trailing = new byte[1];
        int trailingCount = await stream.ReadAsync(trailing, cancellationToken).ConfigureAwait(false);
        if (trailingCount != 0)
        {
            throw new WorkerProtocolException("Data followed the terminal worker frame.");
        }

        return new WorkerRunEvidence(compatibility, checkpoints, failureKind, failureMessage, activeCheckpointId);
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[4];
        int prefixRead = await ReadAtMostAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (prefixRead == 0)
        {
            return null;
        }

        if (prefixRead != 4)
        {
            throw new WorkerProtocolException("The worker evidence stream ended inside a frame prefix.");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (length > WorkerProtocolEmitter.MaximumFrameBytes)
        {
            throw new WorkerProtocolException("A worker evidence frame exceeds 8 MiB.");
        }

        byte[] body = new byte[length];
        int bodyRead = await ReadAtMostAsync(stream, body, cancellationToken).ConfigureAwait(false);
        if (bodyRead != body.Length)
        {
            throw new WorkerProtocolException("The worker evidence stream ended inside a frame payload.");
        }

        return body;
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset;
    }

    private static CompatibilityEvidence MapAdmission(AdmissionPayload payload)
    {
        RoslynReferenceDecision[] references = payload.RoslynReferences.Select(reference => new RoslynReferenceDecision(
            reference.ReferencingAssemblySha256,
            reference.SimpleName,
            reference.RequestedVersion,
            reference.HostVersion,
            Enum.Parse<RoslynAdmissionDecision>(reference.AdmissionDecision, ignoreCase: false))).ToArray();
        foreach (RoslynReferenceDecision reference in references)
        {
            ProtocolJson.RequireHash(reference.ReferencingAssemblySha256, "referencing assembly");
            ProtocolJson.RequireVersion(reference.RequestedVersion);
            if (string.IsNullOrEmpty(reference.SimpleName) ||
                !reference.SimpleName.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
            {
                throw new WorkerProtocolException("A Roslyn admission simple name is invalid.");
            }

            if (reference.HostVersion is not null)
            {
                ProtocolJson.RequireVersion(reference.HostVersion);
            }

            bool supported = reference.SimpleName is "Microsoft.CodeAnalysis" or "Microsoft.CodeAnalysis.CSharp";
            if (!supported)
            {
                if (reference.HostVersion is not null || reference.AdmissionDecision != RoslynAdmissionDecision.RejectedUnsupportedComponent)
                {
                    throw new WorkerProtocolException("Unsupported Roslyn admission evidence is inconsistent.");
                }

                continue;
            }

            if (reference.HostVersion is null)
            {
                throw new WorkerProtocolException("Supported Roslyn admission evidence lacks a host version.");
            }

            int comparison = Version.Parse(reference.RequestedVersion).CompareTo(Version.Parse(reference.HostVersion));
            RoslynAdmissionDecision expected = comparison > 0
                ? RoslynAdmissionDecision.RejectedNewer
                : comparison == 0 ? RoslynAdmissionDecision.EqualHost : RoslynAdmissionDecision.LowerThanHost;
            if (reference.AdmissionDecision != expected)
            {
                throw new WorkerProtocolException("A supported Roslyn admission decision is inconsistent with its versions.");
            }
        }

        if (!references.SequenceEqual(references
            .OrderBy(reference => reference.SimpleName, StringComparer.Ordinal)
            .ThenBy(reference => Version.Parse(reference.RequestedVersion))
            .ThenBy(reference => reference.ReferencingAssemblySha256, StringComparer.Ordinal)))
        {
            throw new WorkerProtocolException("Roslyn admission records are not in canonical order.");
        }

        if (references.Distinct().Count() != references.Length ||
            !references.Any(reference => reference.SimpleName == "Microsoft.CodeAnalysis"))
        {
            throw new WorkerProtocolException("Roslyn admission records are duplicate or omit Microsoft.CodeAnalysis.");
        }

        AggregateAdmissionDecision aggregate = Enum.Parse<AggregateAdmissionDecision>(payload.AggregateAdmissionDecision, ignoreCase: false);
        FixtureCoverage fixtureCoverage = Enum.Parse<FixtureCoverage>(payload.FixtureCoverage, ignoreCase: false);
        AggregateAdmissionDecision expectedAggregate = references.Any(reference =>
            reference.AdmissionDecision is RoslynAdmissionDecision.RejectedNewer or RoslynAdmissionDecision.RejectedUnsupportedComponent)
            ? AggregateAdmissionDecision.Rejected
            : AggregateAdmissionDecision.Admitted;
        if (aggregate != expectedAggregate)
        {
            throw new WorkerProtocolException("Aggregate Roslyn admission disagrees with its records.");
        }

        if (fixtureCoverage == FixtureCoverage.Covered &&
            (aggregate != AggregateAdmissionDecision.Admitted ||
             !references.Any(reference => reference.SimpleName == "Microsoft.CodeAnalysis.CSharp") ||
             references.Any(reference => reference.AdmissionDecision != RoslynAdmissionDecision.EqualHost)))
        {
            throw new WorkerProtocolException("Fixture coverage disagrees with Roslyn admission evidence.");
        }

        return new CompatibilityEvidence(
            references,
            aggregate,
            fixtureCoverage,
            []);
    }
}

internal static class ProtocolJson
{
    public const ulong MaximumJsonInteger = 9_007_199_254_740_991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        RespectRequiredConstructorParameters = true,
    };

    public static JsonDocument ParseStrict(byte[] bytes)
    {
        try
        {
            _ = StrictUtf8.GetString(bytes);
            JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            ValidateDuplicates(document.RootElement);
            return document;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException)
        {
            throw new WorkerProtocolException("A worker frame is not strict UTF-8 JSON.", exception);
        }
    }

    public static T Deserialize<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(Options)
                ?? throw new WorkerProtocolException("A worker payload deserialized to null.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new WorkerProtocolException("A worker payload violates its closed schema.", exception);
        }
    }

    public static void RequireProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new WorkerProtocolException("A protocol value is not an object.");
        }

        string[] actual = element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        string[] wanted = expected.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal))
        {
            throw new WorkerProtocolException("A protocol object has missing or unknown properties.");
        }
    }

    public static void ValidateCheckpoint(CheckpointEvidence checkpoint)
    {
        if (checkpoint.Completion == CheckpointCompletion.Unavailable)
        {
            throw new WorkerProtocolException("Workers may not emit unavailable checkpoint completion.");
        }

        if (checkpoint.Completion == CheckpointCompletion.Complete && checkpoint.GeneratorException is not null)
        {
            throw new WorkerProtocolException("A complete checkpoint cannot contain a generator exception.");
        }

        ValidateSources(checkpoint.Sources);
        ValidateDiagnostics(checkpoint.GeneratorDiagnostics);
        ValidateDiagnostics(checkpoint.RoslynFailureDiagnostics);
        ValidateDiagnostics(checkpoint.InputCompilationDiagnostics);
        ValidateDiagnostics(checkpoint.PostGenerationCompilationDiagnostics);
        ValidateEnvironment(checkpoint.Environment);
        ValidateTrackedSteps(checkpoint.TrackedSteps);
        if (checkpoint.Completion == CheckpointCompletion.Complete &&
            checkpoint.Sources.Availability != SnapshotAvailability.Available)
        {
            throw new WorkerProtocolException("A complete checkpoint requires available generated sources.");
        }

        if (checkpoint.Completion == CheckpointCompletion.Partial &&
            checkpoint.Sources.Availability != SnapshotAvailability.Available &&
            checkpoint.GeneratorDiagnostics.Availability != SnapshotAvailability.Available &&
            checkpoint.RoslynFailureDiagnostics.Availability != SnapshotAvailability.Available &&
            checkpoint.InputCompilationDiagnostics.Availability != SnapshotAvailability.Available &&
            checkpoint.PostGenerationCompilationDiagnostics.Availability != SnapshotAvailability.Available &&
            checkpoint.TrackedSteps.Availability != SnapshotAvailability.Available)
        {
            throw new WorkerProtocolException("A partial checkpoint requires at least one available snapshot.");
        }

        if (checkpoint.GeneratorException is not null &&
            (string.IsNullOrEmpty(checkpoint.GeneratorException.TypeName) || checkpoint.GeneratorException.Message is null))
        {
            throw new WorkerProtocolException("Generator exception evidence is malformed.");
        }
    }

    private static void ValidateEnvironment(EnvironmentEvidence environment)
    {
        if (environment.RuntimeVersion is null || environment.OsDescription is null ||
            environment.ProcessArchitecture is null || environment.Culture is null ||
            environment.UiCulture is null || environment.TimeZoneId is null)
        {
            throw new WorkerProtocolException("Environment strings must be non-null.");
        }

        RoslynHostEvidence[] orderedHost = environment.RoslynHost
            .OrderBy(item => item.SimpleName, StringComparer.Ordinal)
            .ToArray();
        if (!environment.RoslynHost.SequenceEqual(orderedHost) ||
            environment.RoslynHost.Select(item => item.SimpleName).Distinct(StringComparer.Ordinal).Count() != environment.RoslynHost.Count)
        {
            throw new WorkerProtocolException("Roslyn host evidence is not uniquely ordinally ordered.");
        }

        foreach (RoslynHostEvidence host in environment.RoslynHost)
        {
            if (string.IsNullOrEmpty(host.SimpleName))
            {
                throw new WorkerProtocolException("A Roslyn host simple name is empty.");
            }

            RequireVersion(host.AssemblyVersion);
            if (!Guid.TryParseExact(host.ModuleVersionId, "D", out Guid moduleId) ||
                moduleId.ToString("D") != host.ModuleVersionId)
            {
                throw new WorkerProtocolException("A Roslyn module version ID is not a lowercase canonical UUID.");
            }
        }

        PrivateDependencyEvidence[] orderedDependencies = environment.PrivateDependencies
            .OrderBy(item => item.PathToken, StringComparer.Ordinal)
            .ThenBy(item => item.SimpleName, StringComparer.Ordinal)
            .ThenBy(item => item.Sha256, StringComparer.Ordinal)
            .ToArray();
        if (!environment.PrivateDependencies.SequenceEqual(orderedDependencies))
        {
            throw new WorkerProtocolException("Private dependency evidence is not canonically ordered.");
        }

        foreach (PrivateDependencyEvidence dependency in environment.PrivateDependencies)
        {
            if (string.IsNullOrEmpty(dependency.SimpleName) || !IsPrivatePathToken(dependency.PathToken))
            {
                throw new WorkerProtocolException("Private dependency identity or path token is invalid.");
            }

            RequireHash(dependency.Sha256, "private dependency");
        }
    }

    private static void ValidateTrackedSteps(TrackedStepsSnapshot snapshot)
    {
        if (snapshot.Availability == SnapshotAvailability.Unavailable &&
            snapshot.UnavailableReason != "MissingPublicEvidence")
        {
            throw new WorkerProtocolException("Tracked-step unavailability is invalid.");
        }

        if (snapshot.Availability == SnapshotAvailability.Unavailable)
        {
            if (snapshot.Steps.Count != 0)
            {
                throw new WorkerProtocolException("Unavailable tracked-step evidence must have no steps.");
            }

            return;
        }

        if (snapshot.UnavailableReason is not null)
        {
            throw new WorkerProtocolException("Available tracked-step evidence has an unavailable reason.");
        }

        TrackedStepObservation[] orderedSteps = snapshot.Steps
            .OrderBy(step => step.Name, StringComparer.Ordinal)
            .ThenBy(step => step.Occurrence)
            .ToArray();
        if (!snapshot.Steps.SequenceEqual(orderedSteps))
        {
            throw new WorkerProtocolException("Tracked steps are not canonically ordered.");
        }

        foreach (IGrouping<string, TrackedStepObservation> group in snapshot.Steps.GroupBy(step => step.Name, StringComparer.Ordinal))
        {
            ulong expectedOccurrence = 0;
            foreach (TrackedStepObservation step in group)
            {
                if (string.IsNullOrEmpty(step.Name) || step.Occurrence != expectedOccurrence++)
                {
                    throw new WorkerProtocolException("Tracked-step occurrences are invalid or noncontiguous.");
                }
            }
        }

        foreach (TrackedStepObservation step in snapshot.Steps)
        {
            if (step.Occurrence > MaximumJsonInteger || step.Outputs.Any(output =>
                output.Index > MaximumJsonInteger || output.Reason is not ("New" or "Modified" or "Unchanged" or "Cached" or "Removed")))
            {
                throw new WorkerProtocolException("Tracked-step data is outside its closed contract.");
            }

            if (step.Inputs.Any(input => string.IsNullOrEmpty(input.SourceStepName) ||
                    input.SourceOccurrence > MaximumJsonInteger || input.OutputIndex > MaximumJsonInteger) ||
                !IsNondecreasing(step.Inputs, static (left, right) =>
                {
                    int name = StringComparer.Ordinal.Compare(left.SourceStepName, right.SourceStepName);
                    return name != 0 ? name : left.SourceOccurrence != right.SourceOccurrence
                        ? left.SourceOccurrence.CompareTo(right.SourceOccurrence)
                        : left.OutputIndex.CompareTo(right.OutputIndex);
                }) ||
                !step.Outputs.Select(output => output.Index).SequenceEqual(Enumerable.Range(0, step.Outputs.Count).Select(value => (ulong)value)))
            {
                throw new WorkerProtocolException("Tracked relationships or outputs are not canonically ordered.");
            }
        }

        Dictionary<(string Name, ulong Occurrence), TrackedStepObservation> identities = snapshot.Steps
            .ToDictionary(step => (step.Name, step.Occurrence));
        foreach (TrackedInputObservation input in snapshot.Steps.SelectMany(step => step.Inputs))
        {
            if (!identities.TryGetValue((input.SourceStepName, input.SourceOccurrence), out TrackedStepObservation? source) ||
                input.OutputIndex >= checked((ulong)source.Outputs.Count))
            {
                throw new WorkerProtocolException("A tracked relationship refers to a missing step occurrence or output.");
            }
        }
    }

    public static void ValidateFailureKind(string value)
    {
        if (value is not ("GeneratorException" or "LoadFailure" or "CompatibilityFailure" or "Canceled" or "EvidenceLimitExceeded" or "CanonicalizationFailure" or "InternalFailure"))
        {
            throw new WorkerProtocolException("The worker failure kind is unknown.");
        }
    }

    public static void RequireHash(string value, string label)
    {
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new WorkerProtocolException($"The {label} hash is invalid.");
        }
    }

    public static void RequireVersion(string value)
    {
        string[] parts = value.Split('.');
        if (parts.Length != 4 || parts.Any(part => part.Length == 0 || (part.Length > 1 && part[0] == '0') || !int.TryParse(part, out int number) || number < 0))
        {
            throw new WorkerProtocolException("A protocol assembly version is not canonical.");
        }
    }

    private static void ValidateSources(SourceSnapshot snapshot)
    {
        if (snapshot.Availability == SnapshotAvailability.Unavailable)
        {
            if (snapshot.UnavailableReason != "MissingPublicEvidence" || snapshot.Records.Count != 0 || snapshot.SetSha256 is not null)
            {
                throw new WorkerProtocolException("An unavailable source snapshot is inconsistent.");
            }

            return;
        }

        if (snapshot.UnavailableReason is not null || snapshot.SetSha256 is null)
        {
            throw new WorkerProtocolException("An available source snapshot is inconsistent.");
        }

        RequireHash(snapshot.SetSha256, "generated-source set");
        string[] transmittedHintOrder = snapshot.Records.Select(record => record.HintName).ToArray();
        if (transmittedHintOrder.Any(hint => hint is null) ||
            !transmittedHintOrder.SequenceEqual(transmittedHintOrder.OrderBy(hint => hint, StringComparer.Ordinal), StringComparer.Ordinal) ||
            transmittedHintOrder.Distinct(StringComparer.Ordinal).Count() != transmittedHintOrder.Length)
        {
            throw new WorkerProtocolException("Generated-source records are not uniquely ordinally ordered by hint name.");
        }

        GeneratedSourceValue[] values = snapshot.Records.Select(record =>
        {
            byte[] textBytes = DecodeCanonicalBase64(record.TextUtf8Base64);
            string text;
            try
            {
                text = StrictUtf8.GetString(textBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new WorkerProtocolException("A generated source is not strict UTF-8.", exception);
            }

            if (checked((ulong)text.Length) != record.Utf16Length || record.Utf16Length > MaximumJsonInteger ||
                record.PreambleLength > MaximumJsonInteger || record.EncodingName is { } encodingName && encodingName.Length == 0)
            {
                throw new WorkerProtocolException("Generated-source lengths are invalid.");
            }

            string contentHash = Convert.ToHexStringLower(SHA256.HashData(textBytes));
            RequireHash(record.ContentSha256, "generated source content");
            if (contentHash != record.ContentSha256)
            {
                throw new WorkerProtocolException("A generated-source content hash is inconsistent.");
            }

            int expectedChecksumLength = record.ChecksumAlgorithm switch
            {
                "None" => 0,
                "Sha1" => 40,
                "Sha256" => 64,
                _ => throw new WorkerProtocolException("A Roslyn checksum algorithm is outside protocol V1."),
            };
            if (record.RoslynChecksumHex.Length != expectedChecksumLength ||
                record.RoslynChecksumHex.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                throw new WorkerProtocolException("A Roslyn checksum does not match its declared algorithm.");
            }
            return new GeneratedSourceValue(
                record.HintName,
                text,
                record.EncodingName,
                0,
                record.RoslynChecksumHex);
        }).ToArray();
        CanonicalSourceSet canonical = GeneratedSourceCanonicalizer.Canonicalize(values);
        if (canonical.Sha256 != snapshot.SetSha256)
        {
            throw new WorkerProtocolException("The generated-source set hash is inconsistent.");
        }
    }

    private static void ValidateDiagnostics(DiagnosticSnapshot snapshot)
    {
        if (snapshot.Availability == SnapshotAvailability.Unavailable)
        {
            if (snapshot.UnavailableReason is not ("UnsupportedLocationKind" or "MissingPublicEvidence") ||
                snapshot.Records.Count != 0 || snapshot.SetSha256 is not null)
            {
                throw new WorkerProtocolException("An unavailable diagnostic snapshot is inconsistent.");
            }

            return;
        }

        if (snapshot.UnavailableReason is not null || snapshot.SetSha256 is null)
        {
            throw new WorkerProtocolException("An available diagnostic snapshot is inconsistent.");
        }

        RequireHash(snapshot.SetSha256, "diagnostic set");

        List<(byte[] Bytes, ulong Count)> entries = [];
        foreach (DiagnosticRecordObservation record in snapshot.Records)
        {
            byte[] bytes = DecodeCanonicalBase64(record.CanonicalRecordBase64);
            byte[] reconstructed = ReconstructDiagnosticRecord(record);
            if (!bytes.AsSpan().SequenceEqual(reconstructed))
            {
                throw new WorkerProtocolException("A diagnostic canonical record disagrees with its structured evidence.");
            }

            if (record.OccurrenceCount == 0 || record.OccurrenceCount > MaximumJsonInteger)
            {
                throw new WorkerProtocolException("A diagnostic occurrence count is invalid.");
            }

            entries.Add((bytes, record.OccurrenceCount));
        }

        for (int index = 1; index < entries.Count; index++)
        {
            if (CompareBytes(entries[index - 1].Bytes, entries[index].Bytes) >= 0)
            {
                throw new WorkerProtocolException("Diagnostic records are not unique canonical-byte ordered values.");
            }
        }

        using MemoryStream stream = new();
        WriteFrame(stream, StrictUtf8.GetBytes("sga-diagnostic-set-v1"));
        WriteUInt64(stream, checked((ulong)entries.Count));
        foreach ((byte[] bytes, ulong count) in entries)
        {
            WriteFrame(stream, bytes);
            WriteUInt64(stream, count);
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        if (hash != snapshot.SetSha256)
        {
            throw new WorkerProtocolException("The diagnostic-set hash is inconsistent.");
        }
    }

    private static byte[] ReconstructDiagnosticRecord(DiagnosticRecordObservation record)
    {
        if (record is null || record.Id is null || record.Severity is not ("Hidden" or "Info" or "Warning" or "Error") ||
            record.InvariantMessage is null || record.DescriptorCategory is null ||
            record.DescriptorDefaultSeverity is not ("Hidden" or "Info" or "Warning" or "Error") ||
            record.HelpLinkUri is null || record.WarningLevel > MaximumJsonInteger || record.CustomTags is null ||
            record.PrimaryLocation is null || record.AdditionalLocations is null || record.Properties is null)
        {
            throw new WorkerProtocolException("Structured diagnostic evidence contains a missing or invalid scalar.");
        }

        string?[] orderedTags = record.CustomTags.OrderBy(tag => tag, NullFirstOrdinalComparer.Instance).ToArray();
        if (!record.CustomTags.SequenceEqual(orderedTags, StringComparer.Ordinal))
        {
            throw new WorkerProtocolException("Diagnostic custom tags are not canonically ordered.");
        }

        byte[][] additionalLocations = record.AdditionalLocations.Select(CanonicalizeReportLocation).ToArray();
        if (!IsNondecreasing(additionalLocations, CompareBytes))
        {
            throw new WorkerProtocolException("Additional diagnostic locations are not canonical-byte ordered.");
        }

        if (record.Properties.Any(property => property is null || property.Key is null))
        {
            throw new WorkerProtocolException("A diagnostic property entry or key is null.");
        }

        DiagnosticPropertyObservation[] orderedProperties = record.Properties
            .OrderBy(property => property.Key, StringComparer.Ordinal)
            .ThenBy(property => property.Value, NullFirstOrdinalComparer.Instance)
            .ToArray();
        if (!record.Properties.SequenceEqual(orderedProperties) ||
            record.Properties.Select(property => property.Key).Distinct(StringComparer.Ordinal).Count() != record.Properties.Count)
        {
            throw new WorkerProtocolException("Diagnostic properties are not uniquely canonically ordered.");
        }

        CanonicalWriter writer = new();
        writer.WriteFrame("sga-diagnostic-v1");
        writer.WriteNullableString(record.Id);
        writer.WriteNullableString(record.Severity);
        writer.WriteBoolean(record.IsWarningAsError);
        writer.WriteBoolean(record.IsSuppressed);
        writer.WriteUInt64(record.WarningLevel);
        writer.WriteNonNullString(record.InvariantMessage, "diagnostic invariant message");
        writer.WriteNullableString(record.DescriptorCategory);
        writer.WriteNullableString(record.DescriptorDefaultSeverity);
        writer.WriteNonNullString(record.HelpLinkUri, "diagnostic help link");
        writer.WriteSequence(record.CustomTags, EncodeNullableString);
        writer.WriteFrame(CanonicalizeReportLocation(record.PrimaryLocation));
        writer.WriteSequence(additionalLocations, static bytes => bytes);
        writer.WriteSequence(record.Properties, EncodeProperty);
        return writer.ToArray();
    }

    private static byte[] CanonicalizeReportLocation(LocationV1 location)
    {
        if (location.Kind == "None")
        {
            CanonicalWriter none = new();
            none.WriteFrame("none");
            return none.ToArray();
        }

        if (location.Kind != "SourceFile" || location.UnmappedPath is null || location.MappedPath is null ||
            location.Utf16SpanStart is null || location.Utf16SpanLength is null ||
            location.MappedStartLine is null || location.MappedStartColumn is null ||
            location.MappedEndLine is null || location.MappedEndColumn is null || location.LineVisibility is null)
        {
            throw new WorkerProtocolException("A report location is outside the closed SourceFile/None union.");
        }

        UnmappedPathValue unmapped = location.UnmappedPath.Kind switch
        {
            "Controlled" => UnmappedPathValue.Controlled(location.UnmappedPath.Token),
            "Generated" when location.UnmappedPath.Token.StartsWith("generated:", StringComparison.Ordinal) =>
                UnmappedPathValue.Generated(location.UnmappedPath.Token[10..]),
            "External" when location.UnmappedPath.Token.StartsWith("external:", StringComparison.Ordinal) =>
                UnmappedPathValue.External(location.UnmappedPath.Token[9..]),
            _ => throw new WorkerProtocolException("An unmapped report path is invalid."),
        };
        MappedPathValue mapped = !location.MappedPath.HasMappedPath
            ? new MappedPathValue.Unmapped()
            : location.MappedPath.Value?.Kind switch
            {
                "Empty" when location.MappedPath.Value.Token.Length == 0 => new MappedPathValue.Mapped(MappedPathPayload.Empty),
                "External" when location.MappedPath.Value.Token.StartsWith("external:", StringComparison.Ordinal) =>
                    new MappedPathValue.Mapped(MappedPathPayload.External(location.MappedPath.Value.Token[9..])),
                _ => throw new WorkerProtocolException("A mapped report path is invalid."),
            };
        CanonicalLineVisibility visibility = location.LineVisibility switch
        {
            "Visible" => CanonicalLineVisibility.Visible,
            "Hidden" => CanonicalLineVisibility.Hidden,
            "BeforeFirstLineDirective" => CanonicalLineVisibility.BeforeFirstLineDirective,
            _ => throw new WorkerProtocolException("A report line-visibility value is invalid."),
        };
        return DiagnosticCanonicalizer.CanonicalizeSourceLocation(new CanonicalSourceLocation(
            unmapped,
            location.Utf16SpanStart.Value,
            location.Utf16SpanLength.Value,
            mapped,
            location.MappedStartLine.Value,
            location.MappedStartColumn.Value,
            location.MappedEndLine.Value,
            location.MappedEndColumn.Value,
            visibility));
    }

    private static byte[] EncodeNullableString(string? value)
    {
        CanonicalWriter writer = new();
        writer.WriteNullableString(value);
        return writer.ToArray();
    }

    private static byte[] EncodeProperty(DiagnosticPropertyObservation property)
    {
        CanonicalWriter writer = new();
        writer.WriteNullableString(property.Key);
        writer.WriteNullableString(property.Value);
        return writer.ToArray();
    }

    private static byte[] DecodeCanonicalBase64(string value)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            if (Convert.ToBase64String(bytes) != value)
            {
                throw new WorkerProtocolException("A Base64 value is not canonical RFC 4648 with padding.");
            }

            return bytes;
        }
        catch (FormatException exception)
        {
            throw new WorkerProtocolException("A Base64 value is invalid.", exception);
        }
    }

    private static void ValidateDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException($"Duplicate JSON property '{property.Name}'.");
                }

                ValidateDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                ValidateDuplicates(item);
            }
        }
    }

    private static void WriteFrame(Stream stream, byte[] bytes)
    {
        WriteUInt64(stream, checked((ulong)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static int CompareBytes(byte[] left, byte[] right)
    {
        int length = Math.Min(left.Length, right.Length);
        for (int index = 0; index < length; index++)
        {
            int result = left[index].CompareTo(right[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool IsPrivatePathToken(string value)
    {
        const string prefix = "private:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string relative = value[prefix.Length..];
        return relative.Length > 0 && !Path.IsPathRooted(relative) && !relative.Contains('\\') &&
            relative.Split('/').All(segment => segment is not ("" or "." or ".."));
    }

    private static bool IsNondecreasing<T>(IReadOnlyList<T> values, Comparison<T> comparison)
    {
        for (int index = 1; index < values.Count; index++)
        {
            if (comparison(values[index - 1], values[index]) > 0)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class NullFirstOrdinalComparer : IComparer<string?>
    {
        public static NullFirstOrdinalComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
            => left is null ? right is null ? 0 : -1 : right is null ? 1 : StringComparer.Ordinal.Compare(left, right);
    }
}

internal sealed record Envelope<T>(int ProtocolVersion, string Type, ulong Sequence, T Payload);
internal sealed record HelloPayload(string WorkerKind, IReadOnlyList<string> ExpectedCheckpointIds);
internal sealed record AdmissionPayload(
    IReadOnlyList<RoslynReferenceDto> RoslynReferences,
    string AggregateAdmissionDecision,
    string FixtureCoverage);
internal sealed record RoslynReferenceDto(
    string ReferencingAssemblySha256,
    string SimpleName,
    string RequestedVersion,
    string? HostVersion,
    string AdmissionDecision);
internal sealed record CheckpointPayload(string CheckpointId, string Completion, ProtocolCheckpointEvidence Evidence);
internal sealed record CompletedPayload(IReadOnlyList<string> CompletedCheckpointIds);
internal sealed record FailurePayload(string FailureKind, string Message, string? ActiveCheckpointId);
internal sealed record PrivateDependencyDto(string SimpleName, string PathToken, string Sha256);
internal sealed record EnvironmentDto(
    string RuntimeVersion,
    string OsDescription,
    string ProcessArchitecture,
    string Culture,
    string UiCulture,
    string TimeZoneId,
    IReadOnlyList<RoslynHostEvidence> RoslynHost,
    IReadOnlyList<PrivateDependencyDto> PrivateDependencies);
internal sealed record SourceRecordDto(
    string HintName,
    string TextUtf8Base64,
    ulong Utf16Length,
    string? EncodingName,
    ulong PreambleLength,
    string ChecksumAlgorithm,
    string RoslynChecksumHex,
    string ContentSha256);
internal sealed record SourceSnapshotDto(
    string Availability,
    string? UnavailableReason,
    IReadOnlyList<SourceRecordDto> Records,
    string? SetSha256);
internal sealed record DiagnosticSnapshotDto(
    string Availability,
    string? UnavailableReason,
    IReadOnlyList<DiagnosticRecordObservation> Records,
    string? SetSha256);
internal sealed record TrackedStepsDto(
    string Availability,
    string? UnavailableReason,
    IReadOnlyList<TrackedStepObservation> Steps);

internal sealed record ProtocolCheckpointEvidence(
    int EvidenceSchemaVersion,
    string RunId,
    EnvironmentDto Environment,
    SourceSnapshotDto Sources,
    DiagnosticSnapshotDto GeneratorDiagnostics,
    DiagnosticSnapshotDto RoslynFailureDiagnostics,
    DiagnosticSnapshotDto InputCompilationDiagnostics,
    DiagnosticSnapshotDto PostGenerationCompilationDiagnostics,
    TrackedStepsDto TrackedSteps,
    GeneratorExceptionObservation? GeneratorException)
{
    public static ProtocolCheckpointEvidence From(CheckpointEvidence value) => new(
        1,
        value.RunId,
        new EnvironmentDto(
            value.Environment.RuntimeVersion,
            value.Environment.OsDescription,
            value.Environment.ProcessArchitecture,
            value.Environment.Culture,
            value.Environment.UiCulture,
            value.Environment.TimeZoneId,
            value.Environment.RoslynHost,
            value.Environment.PrivateDependencies.Select(dependency => new PrivateDependencyDto(
                dependency.SimpleName,
                dependency.PathToken,
                dependency.Sha256)).ToArray()),
        new SourceSnapshotDto(
            value.Sources.Availability.ToString(),
            value.Sources.UnavailableReason,
            value.Sources.Records.Select(record => new SourceRecordDto(
                record.HintName,
                record.TextUtf8Base64,
                record.Utf16Length,
                record.EncodingName,
                record.PreambleLength,
                record.ChecksumAlgorithm,
                record.RoslynChecksumHex,
                record.ContentSha256)).ToArray(),
            value.Sources.SetSha256),
        Map(value.GeneratorDiagnostics),
        Map(value.RoslynFailureDiagnostics),
        Map(value.InputCompilationDiagnostics),
        Map(value.PostGenerationCompilationDiagnostics),
        new TrackedStepsDto(
            value.TrackedSteps.Availability.ToString(),
            value.TrackedSteps.UnavailableReason,
            value.TrackedSteps.Steps),
        value.GeneratorException);

    public CheckpointEvidence ToDomain(string completion)
    {
        if (EvidenceSchemaVersion != 1)
        {
            throw new WorkerProtocolException("The checkpoint evidence schema version is not 1.");
        }

        SourceSnapshot sources = new(
            Enum.Parse<SnapshotAvailability>(Sources.Availability, ignoreCase: false),
            Sources.UnavailableReason,
            Sources.Records.Select(record =>
            {
                byte[] textBytes = Convert.FromBase64String(record.TextUtf8Base64);
                return new GeneratedSourceObservation(
                    record.HintName,
                    new UTF8Encoding(false, true).GetString(textBytes),
                    record.TextUtf8Base64,
                    record.Utf16Length,
                    record.EncodingName,
                    record.PreambleLength,
                    record.ChecksumAlgorithm,
                    record.RoslynChecksumHex,
                    record.ContentSha256);
            }).ToArray(),
            Sources.SetSha256);
        EnvironmentEvidence environment = new(
            Environment.RuntimeVersion,
            Environment.OsDescription,
            Environment.ProcessArchitecture,
            Environment.Culture,
            Environment.UiCulture,
            Environment.TimeZoneId,
            Environment.RoslynHost,
            Environment.PrivateDependencies.Select(dependency => new PrivateDependencyEvidence(
                dependency.SimpleName,
                dependency.PathToken,
                dependency.Sha256,
                string.Empty)).ToArray());
        return new CheckpointEvidence(
            RunId,
            Enum.Parse<CheckpointCompletion>(completion, ignoreCase: false),
            environment,
            sources,
            Map(GeneratorDiagnostics),
            Map(RoslynFailureDiagnostics),
            Map(InputCompilationDiagnostics),
            Map(PostGenerationCompilationDiagnostics),
            new TrackedStepsSnapshot(
                Enum.Parse<SnapshotAvailability>(TrackedSteps.Availability, ignoreCase: false),
                TrackedSteps.UnavailableReason,
                TrackedSteps.Steps),
            GeneratorException);
    }

    private static DiagnosticSnapshotDto Map(DiagnosticSnapshot value)
        => new(value.Availability.ToString(), value.UnavailableReason, value.Records, value.SetSha256);

    private static DiagnosticSnapshot Map(DiagnosticSnapshotDto value)
        => new(Enum.Parse<SnapshotAvailability>(value.Availability, ignoreCase: false), value.UnavailableReason, value.Records, value.SetSha256);
}
