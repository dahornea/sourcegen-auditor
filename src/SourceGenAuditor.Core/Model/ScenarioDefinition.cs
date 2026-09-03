using System.Collections.Immutable;

namespace SourceGenAuditor.Core.Model;

public enum MutationRelevance
{
    Relevant,
    Irrelevant,
}

public enum ExpectedEffect
{
    Changed,
    Unchanged,
}

public sealed record GeneratorTarget(
    string AssemblyPath,
    string Sha256,
    string TypeName,
    ImmutableArray<byte> AssemblyBytes);

public sealed record SourceInput(string LogicalPath, string PhysicalPath, string Sha256, string Text);

public sealed record ReferenceInput(
    string PhysicalPath,
    string Sha256,
    string AssemblyIdentity,
    ImmutableArray<byte> AssemblyBytes);

public sealed record ParseOptionsDefinition(
    string LanguageVersion,
    string DocumentationMode,
    IReadOnlyList<string> PreprocessorSymbols);

public sealed record CompilationOptionsDefinition(
    string OutputKind,
    string NullableContext,
    bool AllowUnsafe);

public sealed record ControlledInputSet(
    string AssemblyName,
    IReadOnlyList<SourceInput> Sources,
    IReadOnlyList<ReferenceInput> References,
    ParseOptionsDefinition ParseOptions,
    CompilationOptionsDefinition CompilationOptions);

public sealed record MutationExpectations(ExpectedEffect GeneratedSources, ExpectedEffect GeneratorDiagnostics);

public sealed record MutationDefinition(
    string Id,
    string TargetLogicalPath,
    string ReplacementPath,
    string ReplacementSha256,
    string ReplacementText,
    MutationRelevance Relevance,
    MutationExpectations Expectations);

public sealed record ScenarioDefinition(
    string ManifestPath,
    string ScenarioDirectory,
    string ManifestSha256,
    string Id,
    GeneratorTarget Generator,
    ControlledInputSet Baseline,
    MutationDefinition Mutation);
