using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using SourceGenAuditor.Core.Canonicalization;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Execution;
using SourceGenAuditor.Core.Protocol;
using SourceGenAuditor.Core.Reporting;
using Xunit;

namespace SourceGenAuditor.Tests.Protocol;

public sealed class WorkerProtocolTests
{
    [Fact]
    public async Task CompleteClosedSessionRoundTrips()
    {
        using MemoryStream stream = new();
        WorkerProtocolEmitter emitter = new(stream);
        CompatibilityEvidence compatibility = CreateCompatibility();
        CheckpointEvidence checkpoint = CreateCheckpoint("coldA", CheckpointCompletion.Complete);
        emitter.WriteHello("cold", ["coldA"]);
        emitter.WriteAdmission(compatibility);
        emitter.WriteCheckpoint(checkpoint);
        emitter.WriteCompleted(["coldA"]);
        stream.Position = 0;

        WorkerRunEvidence result = await WorkerProtocolReader.ReadSessionAsync(
            stream,
            "cold",
            ["coldA"],
            TestContext.Current.CancellationToken);

        Assert.Null(result.FailureKind);
        Assert.Equal(AggregateAdmissionDecision.Admitted, result.Compatibility.AggregateAdmissionDecision);
        Assert.Equal("coldA", Assert.Single(result.Checkpoints).RunId);
    }

    [Fact]
    public async Task PartialCheckpointThenMatchingFailurePreservesPartialEvidence()
    {
        using MemoryStream stream = new();
        WorkerProtocolEmitter emitter = new(stream);
        emitter.WriteHello("transition", ["transitionA", "mutatedB", "restoredA", "stableA"]);
        emitter.WriteAdmission(CreateCompatibility());
        emitter.WriteCheckpoint(CreateCheckpoint("transitionA", CheckpointCompletion.Complete));
        emitter.WriteCheckpoint(CreateCheckpoint("mutatedB", CheckpointCompletion.Partial));
        emitter.WriteFailure("GeneratorException", "boom", "mutatedB");
        stream.Position = 0;

        WorkerRunEvidence result = await WorkerProtocolReader.ReadSessionAsync(
            stream,
            "transition",
            ["transitionA", "mutatedB", "restoredA", "stableA"],
            TestContext.Current.CancellationToken);

        Assert.Equal("GeneratorException", result.FailureKind);
        Assert.Equal(2, result.Checkpoints.Count);
        Assert.Equal(CheckpointCompletion.Partial, result.Checkpoints[1].Completion);
    }

    [Fact]
    public async Task MalformedTruncatedOversizeOutOfOrderAndLateDataFailClosed()
    {
        await AssertProtocolFailure(Frame(Encoding.UTF8.GetBytes("{")), "cold", ["coldA"]);

        byte[] truncatedPrefix = [0, 0];
        await AssertProtocolFailure(truncatedPrefix, "cold", ["coldA"]);

        byte[] truncatedBody = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(truncatedBody, 10);
        await AssertProtocolFailure(truncatedBody, "cold", ["coldA"]);

        byte[] oversize = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(oversize, WorkerProtocolEmitter.MaximumFrameBytes + 1u);
        await AssertProtocolFailure(oversize, "cold", ["coldA"]);

        byte[] outOfOrder = Frame(JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocolVersion = 1,
            type = "hello",
            sequence = 1,
            payload = new { workerKind = "cold", expectedCheckpointIds = new[] { "coldA" } },
        }));
        await AssertProtocolFailure(outOfOrder, "cold", ["coldA"]);

        byte[] duplicate = Frame(Encoding.UTF8.GetBytes(
            "{\"protocolVersion\":1,\"protocolVersion\":1,\"type\":\"hello\",\"sequence\":0,\"payload\":{\"workerKind\":\"cold\",\"expectedCheckpointIds\":[\"coldA\"]}}"));
        await AssertProtocolFailure(duplicate, "cold", ["coldA"]);

        using MemoryStream valid = CreateCompleteSession();
        byte[] late = valid.ToArray().Concat(new byte[] { 1 }).ToArray();
        await AssertProtocolFailure(late, "cold", ["coldA"]);
    }

    [Fact]
    public async Task MissingDuplicateAndRejectedAdmissionStatesFailClosed()
    {
        using MemoryStream missingAdmission = new();
        WriteJsonFrame(missingAdmission, 0, "hello", new { workerKind = "cold", expectedCheckpointIds = new[] { "coldA" } });
        WriteJsonFrame(missingAdmission, 1, "completed", new { completedCheckpointIds = new[] { "coldA" } });
        await AssertProtocolFailure(missingAdmission.ToArray(), "cold", ["coldA"]);

        using MemoryStream duplicateAdmission = new();
        WorkerProtocolEmitter duplicateEmitter = new(duplicateAdmission);
        duplicateEmitter.WriteHello("cold", ["coldA"]);
        duplicateEmitter.WriteAdmission(CreateCompatibility());
        duplicateEmitter.WriteAdmission(CreateCompatibility());
        await AssertProtocolFailure(duplicateAdmission.ToArray(), "cold", ["coldA"]);

        CompatibilityEvidence rejected = CreateCompatibility() with
        {
            AggregateAdmissionDecision = AggregateAdmissionDecision.Rejected,
        };
        using MemoryStream rejectedWithoutFailure = new();
        WorkerProtocolEmitter rejectedEmitter = new(rejectedWithoutFailure);
        rejectedEmitter.WriteHello("cold", ["coldA"]);
        rejectedEmitter.WriteAdmission(rejected);
        await AssertProtocolFailure(rejectedWithoutFailure.ToArray(), "cold", ["coldA"]);

        using MemoryStream partialCompleted = new();
        WorkerProtocolEmitter partialEmitter = new(partialCompleted);
        partialEmitter.WriteHello("cold", ["coldA"]);
        partialEmitter.WriteAdmission(CreateCompatibility());
        partialEmitter.WriteCheckpoint(CreateCheckpoint("coldA", CheckpointCompletion.Partial));
        partialEmitter.WriteCompleted(["coldA"]);
        await AssertProtocolFailure(partialCompleted.ToArray(), "cold", ["coldA"]);
    }

    [Fact]
    public async Task PartialStateRequiresAvailableEvidenceAndImmediateCorrelatedFailure()
    {
        CheckpointEvidence partial = CreateCheckpoint("transitionA", CheckpointCompletion.Partial);

        using MemoryStream followedByCheckpoint = new();
        WorkerProtocolEmitter followedEmitter = new(followedByCheckpoint);
        followedEmitter.WriteHello("transition", ["transitionA", "mutatedB", "restoredA", "stableA"]);
        followedEmitter.WriteAdmission(CreateCompatibility());
        followedEmitter.WriteCheckpoint(partial);
        followedEmitter.WriteCheckpoint(CreateCheckpoint("mutatedB", CheckpointCompletion.Complete));
        await AssertProtocolFailure(followedByCheckpoint.ToArray(), "transition", ["transitionA", "mutatedB", "restoredA", "stableA"]);

        using MemoryStream unavailable = new();
        WorkerProtocolEmitter unavailableEmitter = new(unavailable);
        unavailableEmitter.WriteHello("transition", ["transitionA", "mutatedB", "restoredA", "stableA"]);
        unavailableEmitter.WriteAdmission(CreateCompatibility());
        unavailableEmitter.WriteCheckpoint(MakeAllSnapshotsUnavailable(partial));
        unavailableEmitter.WriteFailure("GeneratorException", "boom", "transitionA");
        await AssertProtocolFailure(unavailable.ToArray(), "transition", ["transitionA", "mutatedB", "restoredA", "stableA"]);

        using MemoryStream mismatched = new();
        WorkerProtocolEmitter mismatchEmitter = new(mismatched);
        mismatchEmitter.WriteHello("transition", ["transitionA", "mutatedB", "restoredA", "stableA"]);
        mismatchEmitter.WriteAdmission(CreateCompatibility());
        mismatchEmitter.WriteCheckpoint(partial);
        mismatchEmitter.WriteFailure("InternalFailure", "boom", "transitionA");
        await AssertProtocolFailure(mismatched.ToArray(), "transition", ["transitionA", "mutatedB", "restoredA", "stableA"]);
    }

    [Fact]
    public async Task NestedCorruptionOrderingBoundsAndRequiredNullabilityFailClosed()
    {
        byte[] sourceHash = MutateCompleteSession(checkpoint =>
            checkpoint["payload"]!["evidence"]!["sources"]!["setSha256"] = new string('0', 64));
        await AssertProtocolFailure(sourceHash, "cold", ["coldA"]);

        byte[] hostOrder = MutateCompleteSession(checkpoint =>
        {
            JsonArray host = checkpoint["payload"]!["evidence"]!["environment"]!["roslynHost"]!.AsArray();
            JsonNode original = host[0]!.DeepClone();
            host[0]!["simpleName"] = "Z";
            original["simpleName"] = "A";
            host.Add(original);
        });
        await AssertProtocolFailure(hostOrder, "cold", ["coldA"]);

        byte[] trackedIndex = MutateCompleteSession(checkpoint =>
            checkpoint["payload"]!["evidence"]!["trackedSteps"]!["steps"]![0]!["outputs"]![0]!["index"] = 1);
        await AssertProtocolFailure(trackedIndex, "cold", ["coldA"]);

        byte[] danglingInput = MutateCompleteSession(checkpoint =>
            checkpoint["payload"]!["evidence"]!["trackedSteps"]!["steps"]![0]!["inputs"]!.AsArray().Add(new JsonObject
            {
                ["sourceStepName"] = "Missing",
                ["sourceOccurrence"] = 0,
                ["outputIndex"] = 0,
            }));
        await AssertProtocolFailure(danglingInput, "cold", ["coldA"]);

        byte[] missingRequired = MutateCompleteSession(checkpoint =>
            checkpoint["payload"]!["evidence"]!["environment"]!.AsObject().Remove("runtimeVersion"));
        await AssertProtocolFailure(missingRequired, "cold", ["coldA"]);

        byte[] sourceRecord = MutateCompleteSession(
            checkpoint => checkpoint["payload"]!["evidence"]!["sources"]!["records"]![0]!["contentSha256"] = new string('0', 64),
            CreatePopulatedCheckpoint());
        await AssertProtocolFailure(sourceRecord, "cold", ["coldA"]);

        byte[] diagnosticRecord = MutateCompleteSession(
            checkpoint => checkpoint["payload"]!["evidence"]!["generatorDiagnostics"]!["records"]![0]!["invariantMessage"] = "altered",
            CreatePopulatedCheckpoint());
        await AssertProtocolFailure(diagnosticRecord, "cold", ["coldA"]);

        using MemoryStream nullMessage = new();
        WorkerProtocolEmitter emitter = new(nullMessage);
        emitter.WriteHello("cold", ["coldA"]);
        emitter.WriteAdmission(CreateCompatibility());
        WriteJsonFrame(nullMessage, 2, "failure", new { failureKind = "InternalFailure", message = (string?)null, activeCheckpointId = "coldA" });
        await AssertProtocolFailure(nullMessage.ToArray(), "cold", ["coldA"]);

        using MemoryStream lateLoadFailure = new();
        WorkerProtocolEmitter lateLoadEmitter = new(lateLoadFailure);
        lateLoadEmitter.WriteHello("cold", ["coldA"]);
        lateLoadEmitter.WriteAdmission(CreateCompatibility());
        lateLoadEmitter.WriteFailure("LoadFailure", "late", "coldA");
        await AssertProtocolFailure(lateLoadFailure.ToArray(), "cold", ["coldA"]);
    }

    [Fact]
    public async Task ReaderRejectsAggregateBodiesAboveThirtyTwoMiB()
    {
        using MemoryStream stream = new();
        WorkerProtocolEmitter prelude = new(stream);
        prelude.WriteHello("transition", ["transitionA", "mutatedB", "restoredA", "stableA"]);
        prelude.WriteAdmission(CreateCompatibility());
        string[] ids = ["transitionA", "mutatedB", "restoredA", "stableA"];
        for (int index = 0; index < ids.Length; index++)
        {
            CheckpointEvidence checkpoint = CreateCheckpoint(ids[index], CheckpointCompletion.Complete) with
            {
                Environment = CreateCheckpoint(ids[index], CheckpointCompletion.Complete).Environment with
                {
                    OsDescription = new string('x', 8_386_900),
                },
            };
            using MemoryStream single = new();
            new WorkerProtocolEmitter(single).WriteCheckpoint(checkpoint);
            JsonObject envelope = ParseFrames(single.ToArray()).Single();
            envelope["sequence"] = index + 2;
            stream.Write(Frame(JsonSerializer.SerializeToUtf8Bytes(envelope)));
        }

        long bodyBytes = stream.Length - (6 * 4);
        Assert.True(bodyBytes > WorkerProtocolEmitter.MaximumWorkerBytes, $"Aggregate body bytes were {bodyBytes}.");
        stream.Position = 0;
        WorkerProtocolException exception = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            WorkerProtocolReader.ReadSessionAsync(
                stream,
                "transition",
                ids,
                TestContext.Current.CancellationToken));
        Assert.Equal("The worker evidence stream exceeds 32 MiB.", exception.Message);
    }

    [Fact]
    public void FrameAndWorkerByteLimitsAreEnforced()
    {
        byte[] tooLarge = new byte[(6 * 1024 * 1024) + 1];
        string base64 = Convert.ToBase64String(tooLarge);
        CheckpointEvidence checkpoint = CreateCheckpoint("coldA", CheckpointCompletion.Complete) with
        {
            Sources = new SourceSnapshot(
                SnapshotAvailability.Available,
                null,
                [new GeneratedSourceObservation("A.g.cs", string.Empty, base64, 0, null, 0, "Sha1", new string('0', 40), new string('0', 64))],
                new string('0', 64)),
        };
        using MemoryStream frameStream = new();
        WorkerProtocolEmitter frameEmitter = new(frameStream);
        Assert.Throws<EvidenceLimitException>(() => frameEmitter.WriteCheckpoint(checkpoint));

        byte[] workerChunk = new byte[5 * 1024 * 1024];
        string workerBase64 = Convert.ToBase64String(workerChunk);
        CheckpointEvidence workerCheckpoint = checkpoint with
        {
            Sources = checkpoint.Sources with
            {
                Records = [checkpoint.Sources.Records[0] with { TextUtf8Base64 = workerBase64 }],
            },
        };
        using MemoryStream workerStream = new();
        WorkerProtocolEmitter workerEmitter = new(workerStream);
        workerEmitter.WriteHello("cold", ["coldA"]);
        workerEmitter.WriteAdmission(CreateCompatibility());
        for (int index = 0; index < 4; index++)
        {
            workerEmitter.WriteCheckpoint(workerCheckpoint);
        }

        Assert.Throws<EvidenceLimitException>(() => workerEmitter.WriteCheckpoint(workerCheckpoint));
    }

    private static MemoryStream CreateCompleteSession(CheckpointEvidence? checkpoint = null)
    {
        MemoryStream stream = new();
        WorkerProtocolEmitter emitter = new(stream);
        emitter.WriteHello("cold", ["coldA"]);
        emitter.WriteAdmission(CreateCompatibility());
        emitter.WriteCheckpoint(checkpoint ?? CreateCheckpoint("coldA", CheckpointCompletion.Complete));
        emitter.WriteCompleted(["coldA"]);
        stream.Position = 0;
        return stream;
    }

    private static CompatibilityEvidence CreateCompatibility() => new(
        [
            new RoslynReferenceDecision(new string('0', 64), "Microsoft.CodeAnalysis", "5.9.0.0", "5.9.0.0", RoslynAdmissionDecision.EqualHost),
            new RoslynReferenceDecision(new string('1', 64), "Microsoft.CodeAnalysis.CSharp", "5.9.0.0", "5.9.0.0", RoslynAdmissionDecision.EqualHost),
        ],
        AggregateAdmissionDecision.Admitted,
        FixtureCoverage.Covered,
        []);

    private static CheckpointEvidence CreateCheckpoint(string runId, CheckpointCompletion completion)
    {
        GeneratorExceptionObservation? exception = completion == CheckpointCompletion.Partial
            ? new GeneratorExceptionObservation("System.InvalidOperationException", "boom", null)
            : null;
        DiagnosticSnapshot diagnostics = new(
            SnapshotAvailability.Available,
            null,
            [],
            "319425da831882c5bbfdcabedb1b577646d92833db95987f57c1c852b574b048");
        return new CheckpointEvidence(
            runId,
            completion,
            new EnvironmentEvidence(
                "10.0.11",
                "Windows",
                "X64",
                string.Empty,
                string.Empty,
                "UTC",
                [new RoslynHostEvidence("Microsoft.CodeAnalysis", "5.9.0.0", Guid.Empty.ToString("D"))],
                []),
            new SourceSnapshot(
                SnapshotAvailability.Available,
                null,
                [],
                "b28b860f8b7a846ce6115b4913d6c6fdb3370869f2664152d9917f0362ad1586"),
            diagnostics,
            diagnostics,
            diagnostics,
            diagnostics,
            new TrackedStepsSnapshot(
                SnapshotAvailability.Available,
                null,
                [new TrackedStepObservation("SourceOutput", 0, [], [new TrackedOutputObservation(0, "New")])]),
            exception);
    }

    private static CheckpointEvidence MakeAllSnapshotsUnavailable(CheckpointEvidence checkpoint)
    {
        SourceSnapshot sources = new(SnapshotAvailability.Unavailable, "MissingPublicEvidence", [], null);
        DiagnosticSnapshot diagnostics = new(SnapshotAvailability.Unavailable, "MissingPublicEvidence", [], null);
        return checkpoint with
        {
            Sources = sources,
            GeneratorDiagnostics = diagnostics,
            RoslynFailureDiagnostics = diagnostics,
            InputCompilationDiagnostics = diagnostics,
            PostGenerationCompilationDiagnostics = diagnostics,
            TrackedSteps = new TrackedStepsSnapshot(SnapshotAvailability.Unavailable, "MissingPublicEvidence", []),
        };
    }

    private static CheckpointEvidence CreatePopulatedCheckpoint()
    {
        CheckpointEvidence checkpoint = CreateCheckpoint("coldA", CheckpointCompletion.Complete);
        const string text = "x";
        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        CanonicalSourceSet sourceSet = GeneratedSourceCanonicalizer.Canonicalize([new GeneratedSourceValue("A.g.cs", text)]);
        SourceSnapshot sources = new(
            SnapshotAvailability.Available,
            null,
            [new GeneratedSourceObservation(
                "A.g.cs",
                text,
                Convert.ToBase64String(textBytes),
                1,
                "utf-8",
                0,
                "Sha1",
                new string('0', 40),
                Convert.ToHexStringLower(SHA256.HashData(textBytes)))],
            sourceSet.Sha256);

        DiagnosticDescriptor descriptor = new(
            "SGAPROTO001",
            "title",
            "message",
            "Protocol",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);
        Diagnostic diagnostic = Diagnostic.Create(
            descriptor,
            Location.None,
            additionalLocations: null,
            properties: ImmutableDictionary<string, string?>.Empty);
        byte[] recordBytes = DiagnosticCanonicalizer.CanonicalizeDiagnosticRecord(diagnostic);
        CanonicalDiagnosticSet diagnosticSet = DiagnosticCanonicalizer.Canonicalize([diagnostic]);
        DiagnosticSnapshot diagnostics = new(
            SnapshotAvailability.Available,
            null,
            [new DiagnosticRecordObservation(
                diagnostic.Id,
                diagnostic.Severity.ToString(),
                diagnostic.IsWarningAsError,
                diagnostic.IsSuppressed,
                checked((ulong)diagnostic.WarningLevel),
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                diagnostic.Descriptor.Category,
                diagnostic.Descriptor.DefaultSeverity.ToString(),
                diagnostic.Descriptor.HelpLinkUri,
                diagnostic.Descriptor.CustomTags.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                LocationV1.None,
                [],
                [],
                1,
                Convert.ToBase64String(recordBytes))],
            diagnosticSet.Sha256);
        return checkpoint with { Sources = sources, GeneratorDiagnostics = diagnostics };
    }

    private static byte[] MutateCompleteSession(Action<JsonObject> mutation, CheckpointEvidence? checkpoint = null)
    {
        JsonObject[] frames = ParseFrames(CreateCompleteSession(checkpoint).ToArray());
        mutation(frames[2]);
        using MemoryStream result = new();
        foreach (JsonObject frame in frames)
        {
            result.Write(Frame(JsonSerializer.SerializeToUtf8Bytes(frame)));
        }

        return result.ToArray();
    }

    private static JsonObject[] ParseFrames(byte[] bytes)
    {
        List<JsonObject> frames = [];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            offset += 4;
            frames.Add(JsonNode.Parse(bytes.AsSpan(offset, length))!.AsObject());
            offset += length;
        }

        return frames.ToArray();
    }

    private static async Task AssertProtocolFailure(byte[] bytes, string workerKind, IReadOnlyList<string> checkpointIds)
    {
        using MemoryStream stream = new(bytes, writable: false);
        await Assert.ThrowsAsync<WorkerProtocolException>(() => WorkerProtocolReader.ReadSessionAsync(
            stream,
            workerKind,
            checkpointIds,
            TestContext.Current.CancellationToken));
    }

    private static byte[] Frame(byte[] body)
    {
        byte[] result = new byte[body.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(result, checked((uint)body.Length));
        body.CopyTo(result, 4);
        return result;
    }

    private static void WriteJsonFrame(Stream stream, ulong sequence, string type, object payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, type, sequence, payload });
        stream.Write(Frame(body));
    }
}
