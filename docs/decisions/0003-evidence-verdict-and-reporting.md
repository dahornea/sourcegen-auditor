# ADR 0003: Evidence, verdict, and reporting

- Status: Accepted
- Date: 2026-09-02
- Amendment F5-01: Approved 2026-09-03
- Amendment F5-02: Approved 2026-09-03

## Context

Roslyn exposes observed outputs and tracked reasons but not semantic intent. Missing or partial evidence must not look successful, and console formatting must not become part of domain behavior.

## Decision

Scenario authors explicitly declare relevance and source/diagnostic effects. Generated-source identity is its exact ordinal, case-sensitive `HintName`; duplicates are `ERROR`. Content equality is ordinal equality over the exact `SourceText` UTF-16 code-unit sequence. Encoding metadata or an encoding preamble/BOM is observational and excluded, while a literal U+FEFF code unit in the text is significant.

Canonical bytes use strict UTF-8 without BOM. `frame(x)` is an unsigned 64-bit big-endian byte length followed by `x`. A source record is `frame(UTF8("sga-source-v1")) + frame(UTF8(hintName)) + frame(UTF8(text))`; a source set is `frame(UTF8("sga-source-set-v1")) + UInt64BE(recordCount) + frame(record)` for each record sorted by ordinal hint name. SHA-256 hashes the complete source-set bytes. An unpaired surrogate that strict UTF-8 cannot encode is `CanonicalizationFailure`. Encoding name, encoding preamble length, and Roslyn checksum metadata are reported observations but do not enter equality or this hash.

Diagnostics are a canonical multiset. `u64(n)` is an unsigned 64-bit big-endian integer; `bool(x)` is byte `00` or `01`; `str(s)` is `00` for null or `01 + frame(strict-UTF8(s))`; and `seq(items)` is `u64(count)` followed by each framed item. No Unicode normalization or case folding occurs. A diagnostic record, in order, is framed tag `sga-diagnostic-v1`; `Diagnostic.Id`; `Diagnostic.Severity.ToString()`; warning-as-error and suppression booleans; warning level; `GetMessage(CultureInfo.InvariantCulture)`; `Diagnostic.Descriptor.Category`; `Diagnostic.Descriptor.DefaultSeverity.ToString()`; non-null `Diagnostic.Descriptor.HelpLinkUri`; ordinal-sorted custom-tag sequence; framed primary location; additional locations sorted by unsigned canonical bytes; and properties sorted by ordinal key then null-first ordinal value. Strings use `str`, numbers use `u64`, and sequence items are framed. Equal record bytes collapse to one counted entry; duplicates are never discarded. The set is framed tag `sga-diagnostic-set-v1`, unique-record count, then each unsigned-byte-sorted framed record and its occurrence count. SHA-256 covers the complete bytes and is lowercase hexadecimal.

Amendment F5-01 defines V1 over values observable through the public Roslyn 5.9.0 API after Roslyn's normalization, never over lost constructor arguments. Roslyn exposes null `DiagnosticDescriptor.HelpLinkUri` and null `Description` constructor arguments as empty values. `HelpLinkUri` is in the canonical record, so its observable empty value uses the existing non-null `str` encoding and this field may never use the null marker. `Description` was audited but remains excluded from V1 identity. The descriptor string overload also maps null and empty title or message-format strings to the same non-null empty `LocalizableString`; title and raw message format remain excluded, while the resulting invariant `GetMessage` value is included as an observed non-null string, including an empty result. The `LocalizableString` overload instead rejects a null title/message object. Descriptor category rejects null; ID rejects null, empty, or whitespace and does not trim accepted text. Null custom-tags, additional-locations, and properties containers become observable empty collections; a null primary location becomes observable `Location.None`. Supplied nullable custom-tag elements and diagnostic property values retain null-versus-empty distinction. C# parsing normalizes a null syntax-tree path to empty, while public file-span/external-location constructors reject null and preserve empty paths. F5-02 preserves public mapped state, path identity, and all three line-visibility values with explicit tags as specified below. These are the only documented collapses. V1 does not globally equate null and empty or report any original value that Roslyn no longer exposes.

Only `LocationKind.None` and `LocationKind.SourceFile` are canonicalized in Phase 1. An unmapped path is `str(token) + byte(kind)`, with kind `00` controlled, `01` generated, or `02` external. A mapped payload uses the same framing but permits only `02` external or `03` mapped-empty. Controlled tokens are nonempty `/`-normalized manifest logical paths; generated tokens are `generated:<observableHintName>`; external tokens are `external:<lowercase SHA-256 of strict-UTF-8 Path.GetFullPath result>` without case folding or normalization; and mapped-empty is exactly the empty string. Separate domain types prevent mapped-empty from becoming an unmapped path and prevent controlled/generated identities from becoming mapped payloads. The discriminator prevents controlled/generated/external token collisions. Mapped state is byte `00` for unmapped with no payload, or byte `01` followed by the mapped payload. An explicitly mapped empty path is therefore `01 + str("") + 03`, not the unmapped state. A nonempty explicit mapped path is tokenized from its own public path and redacted as external. Line visibility is byte `00` `Visible`, `01` `Hidden`, or `02` `BeforeFirstLineDirective`.

A source location is framed `source`, unmapped path value, unsigned UTF-16 span start/length, mapped-state value, unsigned zero-based mapped start/end line/column, and line-visibility byte. JSON uses `UnmappedPathValueV1` with `Controlled`, `Generated`, or `External`; and `MappedPathV1` as `{ hasMappedPath: false }` with no `value` or `{ hasMappedPath: true, value: MappedPathPayloadV1 }`, where the payload is exactly `Empty` with `token: ""` or `External` with a nonempty redacted token. It never uses a sentinel or exposes a raw path. `None` is exactly framed `none`. `MetadataFile`, `XmlFile`, `ExternalFile`, and unknown future kinds are valid but unsupported, making the snapshot unavailable with `UnsupportedLocationKind` and producing `UNKNOWN`; available non-comparison details may still be reported. Empty required unmapped paths, null additional-location elements, invalid line spans, negative/out-of-range coordinates, failed path resolution, invalid Unicode, or inconsistent canonical bytes are `CanonicalizationFailure` and aggregate `ERROR`, never complete-checkpoint unavailability.

The remaining-field collision audit found no other loss inside the declared projections. Custom-tag null elements, empty elements, duplicates, and order-independent multiplicity remain distinct; property entries preserve ordinal key text and null/empty values while intentionally excluding arbitrary dictionary comparer objects; additional-location duplicates remain; and null elements fail canonicalization. Only the invariant formatted message is projected, so raw format strings and arguments that Roslyn renders identically intentionally compare equal without an original-input claim.

Tracked output reasons establish invalidation; `Unchanged` is recomputation, not cache reuse. Arbitrary tracked values and full generated text are omitted from the public report.

Assertions are `PASS`, `FAIL`, `UNKNOWN`, or `ERROR`, aggregating as `ERROR > FAIL > UNKNOWN > PASS`. Observed facts, domain evaluation, report DTOs, JSON/console rendering, worker protocol, and exit mapping are separate contracts.

## Normative V1 vectors

All hex below is lowercase and contiguous; line wrapping in a renderer is not data. These values are part of the contract and must be reproduced byte-for-byte in two fresh processes before F5 passes.

| Vector | Canonical bytes (hex) | SHA-256 |
| --- | --- | --- |
| empty source set | `00000000000000117367612d736f757263652d7365742d76310000000000000000` | `b28b860f8b7a846ce6115b4913d6c6fdb3370869f2664152d9917f0362ad1586` |
| one source, hint `A.g.cs`, text `x` | `00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002c000000000000000d7367612d736f757263652d76310000000000000006412e672e6373000000000000000178` | `5fd0ef7f721b43a4fb3a89f2519cd908afd58796b8354e8e28ba366ab20ff744` |
| one source, hint `A.g.cs`, text U+00E9 | `00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002d000000000000000d7367612d736f757263652d76310000000000000006412e672e63730000000000000002c3a9` | `34350d1fddc2c27512c246e8bf8bec5ec25afd6db7bc081116bcfe24306b3773` |
| one source, hint `A.g.cs`, text U+0065 U+0301 | `00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002e000000000000000d7367612d736f757263652d76310000000000000006412e672e6373000000000000000365cc81` | `06a411fa1b78d672510fccdad4932912cfac5005351a59d9ad7db01c66bf8c40` |
| one source, hint `A.g.cs`, literal text U+FEFF | `00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002e000000000000000d7367612d736f757263652d76310000000000000006412e672e63730000000000000003efbbbf` | `331c7aa9b0219298cf01c15b05e5b398fca88fac1d0e42725889610588587006` |
| empty diagnostic set | `00000000000000157367612d646961676e6f737469632d7365742d76310000000000000000` | `319425da831882c5bbfdcabedb1b577646d92833db95987f57c1c852b574b048` |

The diagnostic duplicate vector has two occurrences of one record: ID `SGA001`; severity/default severity `Warning`; both booleans false; warning level 1; invariant message `café` (U+00E9); descriptor category `Test`; publicly observable empty help link; custom tag `tag`; primary location `None`; no additional locations; and property key `k` with null value. The diagnostic record is exactly 201 bytes (`0x00000000000000c9` in its enclosing frame); the complete set is 254 bytes. Its canonical bytes are:

```text
00000000000000157367612d646961676e6f737469632d7365742d7631000000000000000100000000000000c900000000000000117367612d646961676e6f737469632d76310100000000000000065347413030310100000000000000075761726e696e6700000000000000000001010000000000000005636166c3a9010000000000000004546573740100000000000000075761726e696e670100000000000000000000000000000001000000000000000c010000000000000003746167000000000000000c00000000000000046e6f6e6500000000000000000000000000000001000000000000000b0100000000000000016b000000000000000002
```

Its SHA-256 is `b2bbc1538dc65a17d1d7422f7d43e28bf739ad0afe1978b8db455c95fb1f0bb2`. The `None` location primitive alone is `00000000000000046e6f6e65` with SHA-256 `9506f170afb36fca8c02831b18c88d7247935cae33b932f00eed9c9263e3ab6c`.

The F5-02 source-location vectors all use controlled unmapped token `Input.cs`, UTF-16 span 0/1, mapped range (0,0)-(0,1), and `Visible`. The mapped-nonempty vector uses a redacted external token `external:` followed by 64 zeroes; it is canonical evidence, not a raw path.

| Location state | Length | SHA-256 | Canonical bytes (hex) |
| --- | ---: | --- | --- |
| unmapped | 82 | `ad9845e844ed5f8900b1071e5d918ffd66f1391cb7329c9fc6115bde2dbf5870` | `0000000000000006736f75726365010000000000000008496e7075742e6373000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000100` |
| mapped empty | 92 | `b044182f465972d2341d849d9a5162f25993489aabd94d26b034aba50ddbd278` | `0000000000000006736f75726365010000000000000008496e7075742e637300000000000000000000000000000000010101000000000000000003000000000000000000000000000000000000000000000000000000000000000100` |
| mapped nonempty | 165 | `356c04407696fcd6a62d459d14743fbd1e60bd64a0e1dfb7f2b76b31c540bd8a` | `0000000000000006736f75726365010000000000000008496e7075742e637300000000000000000000000000000000010101000000000000004965787465726e616c3a3030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303002000000000000000000000000000000000000000000000000000000000000000100` |

These three states are pairwise unequal. Controlled, generated, and external path kinds and `Visible`, `Hidden`, and `BeforeFirstLineDirective` likewise produce pairwise-distinct bytes.

Two `SourceText` values with the same code units but different encoding name/preamble/checksum observations must reproduce the same source vector. U+00E9, U+0065 U+0301, and literal U+FEFF must reproduce the three different vectors above.

## Consequences

The report can explain every conclusion and preserve partial evidence without overstating it. Hash, path-token, and schema evolution require a new version. A completed run with missing tracking or unresolved required location identity is inconclusive, while load/execution/protocol/canonicalization/report failures are errors.
