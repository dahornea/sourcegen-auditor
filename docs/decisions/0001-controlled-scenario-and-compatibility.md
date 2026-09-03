# ADR 0001: Controlled scenario and compatibility

- Status: Accepted
- Date: 2026-09-02

## Context

Project loading adds MSBuild, SDK, implicit-reference, analyzer, generated-file, and environment state that conflicts with the first slice's controlled-input claim. Loading a generator DLL across compiler versions also creates type-identity and binary-compatibility risk.

## Decision

Phase 1 loads one manifest-hashed compiled DLL in a worker-local `AssemblyLoadContext`. After pre-load compiler-reference validation, it resolves and instantiates the manifest's exact named C# `IIncrementalGenerator`, then converts that selected instance with Roslyn's public `AsSourceGenerator` adapter. A closed V1 manifest supplies explicit sources, metadata references, parse/compilation options, one source replacement, declared relevance, and expected source/diagnostic effects. It does not load a project. Before either worker starts, the public CLI acquires sharing-denying read handles for the manifest and all declared or generator-directory input files, reloads the scenario from that leased byte set, and retains the handles through both workers. This is a Phase 1 Windows consistency measure, not a security sandbox or defense against already-held write handles.

Build and host with .NET SDK 10.0.400 selected with `rollForward: disable` and Microsoft.CodeAnalysis.CSharp 5.9.0. The only host-unified Roslyn assemblies are `Microsoft.CodeAnalysis` and `Microsoft.CodeAnalysis.CSharp`, both assembly version 5.9.0.0. Before code loads, metadata inspection recursively resolves the target's complete file-backed private dependency closure from the generator directory and examines every `Microsoft.CodeAnalysis*` reference occurrence. Each supported simple name is compared with the matching host component; a higher request is rejected and an equal or lower request is admitted because .NET assembly resolution can bind it to an already loaded equal-or-higher shared assembly. Any other Roslyn component is unsupported and rejected. Strict equality is not technically required.

Admission is not a compatibility promise. The report records each closure occurrence's referencing-assembly hash, Roslyn simple name, requested and matching host versions, and admission decision. Phase 1 contains exactly one generator fixture whose closure references Common and CSharp 5.9.0.0, so only that complete equal-version closure is fixture-covered. Any lower component is labeled `NotFixtureCovered`; a successful audit is evidence only for that artifact and scenario, while failure is a typed compatibility/load `ERROR`. A broader supported range requires executable fixtures and a new Architecture Gate.

## Consequences

The first slice is reproducible and testable but deliberately inconvenient for arbitrary projects. Additional inputs and broader advertised Roslyn support require new schema versions, executable compatibility evidence, and approval. Future versioned workers can widen support without loading conflicting Roslyn assemblies together.
