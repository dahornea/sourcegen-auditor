# ADR 0002: Worker-process execution

- Status: Accepted
- Date: 2026-09-02

## Context

Generator cancellation is cooperative. A selected generator can hang, crash, terminate the process, or write to console streams. An in-process audit cannot enforce a hard timeout or reliably retain partial evidence after those failures.

## Decision

The CLI supervises sequential worker processes. One fresh worker records cold A. A second worker retains every returned immutable `GeneratorDriver` across A, B, restored A, and stable A.

Each worker has two dedicated anonymous pipes: evidence from worker to parent and control from parent to worker. Every evidence frame is a four-byte unsigned big-endian payload length followed by one strict UTF-8 JSON object without BOM. Its closed envelope is `{ protocolVersion, type, sequence, payload }`; `protocolVersion` is the JSON integer `1`, sequences are JSON integers starting at zero and contiguous, and every envelope and payload object rejects unknown or duplicate properties.

Evidence payloads are closed by message type: `hello` is `{ workerKind, expectedCheckpointIds }`; mandatory pre-checkpoint `admission` is `{ roslynReferences, aggregateAdmissionDecision, fixtureCoverage }`; `checkpoint` is `{ checkpointId, completion, evidence }`; terminal `completed` is `{ completedCheckpointIds }`; and terminal `failure` is `{ failureKind, message, activeCheckpointId }`. Worker kind is `cold` or `transition`; completion is `Complete` or `Partial`; checkpoint identifiers and order must exactly match the parent request; and a failure's active checkpoint is a string or null. Worker-emitted failures are exactly generator, load, compatibility, cancellation, evidence-limit, canonicalization, or internal failures; timeout, crash, protocol, and report-write failures are parent-synthesized. Admission uses the closed `CompatibilityEvidenceV1`; `checkpoint.evidence` uses the closed `CheckpointEvidenceV1`, including four separate diagnostic categories. Their full field types, nullability, ordering, tokenization, Base64/hash validation, numeric bounds, and state rules are normative in `ARCHITECTURE.md`; no other nested value is permitted. Changing any envelope or payload shape requires a protocol-version change.

Control permits exactly `{ protocolVersion: 1, type: "cancel", sequence: 0, payload: { reason: "UserCancellation" | "Timeout" } }`. A strict JSON frame body is at most 8 MiB, including its envelope, and all evidence frame bodies from one worker total at most 32 MiB. Unknown versions, types, properties, enum values, checkpoint IDs, or counts; duplicate properties; invalid UTF-8 or JSON; non-integer, negative, duplicate, or skipped sequences; oversize lengths; EOF within a prefix or payload; a frame or trailing bytes after the terminal frame; or a missing terminal frame are `WorkerProtocolFailure` and `ERROR`. `hello` must be sequence zero, exactly one terminal frame is required, and EOF is required after it. Previously validated checkpoints survive; a partial frame does not.

The cold worker must emit `coldA`; the transition worker must emit, in order, `transitionA`, `mutatedB`, `restoredA`, and `stableA`. A successful inspection emits one identical admission per worker before any checkpoint; rejection is followed by `CompatibilityFailure`. `completed` is valid only after admission and every expected checkpoint was `Complete`. A `Partial` checkpoint must be followed immediately by `failure` naming that checkpoint and yields `ERROR`; a failure before a checkpoint makes that run `Unavailable` in the parent and also yields `ERROR`. Only a valid-but-unsupported public location kind or missing public evidence can make an internal snapshot unavailable in a complete checkpoint and yield `UNKNOWN`; malformed paths, coordinates, bytes, or hashes are `CanonicalizationFailure` and `ERROR`.

The default timeout is 30 seconds for startup and each expected checkpoint, configurable from 1 through 600 seconds. The deadline resets only after a complete validated checkpoint; partial bytes do not extend it. The absolute worker deadline is `(expected checkpoint count + 1) * timeout`. On timeout or user cancellation, the parent sends `cancel`, waits two seconds, calls `Kill(entireProcessTree: true)` if needed, and waits up to five more seconds for the root. Timeout is exit 3. User cancellation is exit 130 only when root cleanup completes; root termination failure overrides it as `InternalFailure`, aggregate `ERROR`, exit 3. Descendant cleanup remains best effort.

Stdout and stderr are separate redirected byte streams, drained concurrently so generator output cannot block or corrupt the protocol. For each stream the parent retains at most the first 1 MiB, hashes all drained bytes with SHA-256, records total/captured/discarded byte counts and truncation, and represents retained bytes as Base64 in the report. Log truncation is observational and cannot change an assertion.

The public report is strict UTF-8 JSON without BOM and may not exceed 32 MiB. Evidence overflow is `EvidenceLimitExceeded`; report overflow is `ReportWriteFailure`. IPC creates no temporary files. For `--output`, the parent creates one same-directory file named `<target>.sga-tmp-<process-id>-<random>`, flushes it, and atomically replaces or moves it to the target. It deletes only that invocation-owned path on success, error, or cancellation and never wildcard-cleans old files; abrupt parent termination may leave an orphan, which is a documented residual risk.

## Consequences

Completed checkpoints can survive most worker failures, and a non-cooperative root worker can normally be terminated. Protocol, byte-limit, timeout, cancellation, atomic-write, and process tests become required. Terminating descendants and removing files after abrupt parent death are best effort. The worker and `AssemblyLoadContext` are fault/dependency boundaries, not security sandboxes; selected code retains the user's permissions.
