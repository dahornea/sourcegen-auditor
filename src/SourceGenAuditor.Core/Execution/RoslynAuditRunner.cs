using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SourceGenAuditor.Core.Canonicalization;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Model;
using SourceGenAuditor.Core.Reporting;

namespace SourceGenAuditor.Core.Execution;

public sealed class RoslynAuditRunner
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ScenarioDefinition scenario;
    private readonly IIncrementalGenerator generator;
    private readonly CompatibilityEvidence compatibility;
    private readonly CSharpParseOptions parseOptions;
    private readonly CSharpCompilation baselineCompilation;
    private readonly SyntaxTree baselineMutationTree;

    public RoslynAuditRunner(
        ScenarioDefinition scenario,
        IIncrementalGenerator generator,
        CompatibilityEvidence compatibility)
    {
        this.scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));

        parseOptions = new CSharpParseOptions(
            LanguageVersion.CSharp14,
            DocumentationMode.Parse,
            preprocessorSymbols: scenario.Baseline.ParseOptions.PreprocessorSymbols);
        SyntaxTree[] trees = scenario.Baseline.Sources
            .Select(source => CSharpSyntaxTree.ParseText(
                SourceText.From(source.Text, StrictUtf8),
                parseOptions,
                source.LogicalPath))
            .ToArray();
        baselineMutationTree = trees.Single(tree =>
            StringComparer.Ordinal.Equals(tree.FilePath, scenario.Mutation.TargetLogicalPath));
        MetadataReference[] references = scenario.Baseline.References
            .Select(reference => MetadataReference.CreateFromImage(reference.AssemblyBytes))
            .ToArray();
        CSharpCompilationOptions compilationOptions = new(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: scenario.Baseline.CompilationOptions.AllowUnsafe,
            nullableContextOptions: NullableContextOptions.Enable,
            deterministic: true);
        baselineCompilation = CSharpCompilation.Create(
            scenario.Baseline.AssemblyName,
            trees,
            references,
            compilationOptions);
    }

    public WorkerRunEvidence RunCold(
        CancellationToken cancellationToken,
        Action<CheckpointEvidence>? checkpointAccepted = null)
    {
        GeneratorDriver driver = CreateDriver();
        (GeneratorDriver _, CheckpointEvidence checkpoint) = RunCheckpoint(
            driver,
            baselineCompilation,
            "coldA",
            cancellationToken);
        checkpointAccepted?.Invoke(checkpoint);
        return CreateWorkerEvidence([checkpoint]);
    }

    public WorkerRunEvidence RunTransition(
        CancellationToken cancellationToken,
        Action<CheckpointEvidence>? checkpointAccepted = null)
    {
        GeneratorDriver driver = CreateDriver();
        List<CheckpointEvidence> checkpoints = [];

        (driver, CheckpointEvidence transitionA) = RunCheckpoint(
            driver,
            baselineCompilation,
            "transitionA",
            cancellationToken);
        checkpoints.Add(transitionA);
        checkpointAccepted?.Invoke(transitionA);
        if (transitionA.Completion != CheckpointCompletion.Complete)
        {
            return CreateWorkerEvidence(checkpoints);
        }

        SyntaxTree mutationTree = CSharpSyntaxTree.ParseText(
            SourceText.From(scenario.Mutation.ReplacementText, StrictUtf8),
            parseOptions,
            scenario.Mutation.TargetLogicalPath);
        CSharpCompilation mutationCompilation = baselineCompilation.ReplaceSyntaxTree(baselineMutationTree, mutationTree);
        (driver, CheckpointEvidence mutatedB) = RunCheckpoint(
            driver,
            mutationCompilation,
            "mutatedB",
            cancellationToken);
        checkpoints.Add(mutatedB);
        checkpointAccepted?.Invoke(mutatedB);
        if (mutatedB.Completion != CheckpointCompletion.Complete)
        {
            return CreateWorkerEvidence(checkpoints);
        }

        (driver, CheckpointEvidence restoredA) = RunCheckpoint(
            driver,
            baselineCompilation,
            "restoredA",
            cancellationToken);
        checkpoints.Add(restoredA);
        checkpointAccepted?.Invoke(restoredA);
        if (restoredA.Completion != CheckpointCompletion.Complete)
        {
            return CreateWorkerEvidence(checkpoints);
        }

        (driver, CheckpointEvidence stableA) = RunCheckpoint(
            driver,
            baselineCompilation,
            "stableA",
            cancellationToken);
        checkpoints.Add(stableA);
        checkpointAccepted?.Invoke(stableA);
        return CreateWorkerEvidence(checkpoints);
    }

    private GeneratorDriver CreateDriver()
    {
        GeneratorDriverOptions options = new(
            IncrementalGeneratorOutputKind.None,
            trackIncrementalGeneratorSteps: true);
        return CSharpGeneratorDriver.Create(
            generators: [generator.AsSourceGenerator()],
            additionalTexts: [],
            parseOptions: parseOptions,
            optionsProvider: null,
            driverOptions: options);
    }

    private (GeneratorDriver Driver, CheckpointEvidence Evidence) RunCheckpoint(
        GeneratorDriver driver,
        CSharpCompilation inputCompilation,
        string runId,
        CancellationToken cancellationToken)
    {
        ImmutableArray<Diagnostic> inputDiagnostics = inputCompilation.GetDiagnostics(cancellationToken);
        GeneratorDriver updatedDriver = driver.RunGeneratorsAndUpdateCompilation(
            inputCompilation,
            out Compilation outputCompilation,
            out ImmutableArray<Diagnostic> driverDiagnostics,
            cancellationToken);
        GeneratorDriverRunResult runResult = updatedDriver.GetRunResult();
        GeneratorRunResult generatorResult = runResult.Results.Single();

        Dictionary<SyntaxTree, string> controlledTrees = new(ReferenceEqualityComparer.Instance);
        foreach (SyntaxTree tree in inputCompilation.SyntaxTrees)
        {
            controlledTrees.Add(tree, tree.FilePath);
        }

        Dictionary<SyntaxTree, string> generatedTrees = new(ReferenceEqualityComparer.Instance);
        foreach (GeneratedSourceResult source in generatorResult.GeneratedSources)
        {
            generatedTrees.Add(source.SyntaxTree, source.HintName);
        }

        CanonicalPathContext pathContext = new(controlledTrees, generatedTrees);
        SourceSnapshot sources = CreateSourceSnapshot(generatorResult.GeneratedSources);
        DiagnosticSnapshot generatorDiagnostics = CreateDiagnosticSnapshot(
            generatorResult.Diagnostics.Where(diagnostic => !IsRoslynFailureDiagnostic(diagnostic)),
            pathContext);
        DiagnosticSnapshot roslynFailures = CreateDiagnosticSnapshot(
            driverDiagnostics.Where(IsRoslynFailureDiagnostic),
            pathContext);
        DiagnosticSnapshot inputCompilationDiagnostics = CreateDiagnosticSnapshot(inputDiagnostics, pathContext);
        DiagnosticSnapshot postCompilationDiagnostics = CreateDiagnosticSnapshot(
            outputCompilation.GetDiagnostics(cancellationToken),
            pathContext);
        TrackedStepsSnapshot trackedSteps = CreateTrackedSteps(generatorResult);
        GeneratorExceptionObservation? exception = generatorResult.Exception is null
            ? null
            : new GeneratorExceptionObservation(
                generatorResult.Exception.GetType().FullName ?? generatorResult.Exception.GetType().Name,
                generatorResult.Exception.Message,
                generatorResult.Exception.StackTrace);
        CheckpointCompletion completion = exception is null ? CheckpointCompletion.Complete : CheckpointCompletion.Partial;

        return (updatedDriver, new CheckpointEvidence(
            runId,
            completion,
            CreateEnvironment(),
            sources,
            generatorDiagnostics,
            roslynFailures,
            inputCompilationDiagnostics,
            postCompilationDiagnostics,
            trackedSteps,
            exception));
    }

    private WorkerRunEvidence CreateWorkerEvidence(IReadOnlyList<CheckpointEvidence> checkpoints)
    {
        CheckpointEvidence? failed = checkpoints.LastOrDefault(checkpoint => checkpoint.Completion != CheckpointCompletion.Complete);
        return new WorkerRunEvidence(
            compatibility,
            checkpoints,
            failed is null ? null : "GeneratorException",
            failed?.GeneratorException?.Message,
            failed?.RunId);
    }

    private static SourceSnapshot CreateSourceSnapshot(ImmutableArray<GeneratedSourceResult> sources)
    {
        GeneratedSourceValue[] values = sources.Select(source => new GeneratedSourceValue(
            source.HintName,
            source.SourceText.ToString(),
            source.SourceText.Encoding?.WebName,
            source.SourceText.Encoding?.GetPreamble().Length ?? 0,
            Convert.ToHexStringLower(source.SourceText.GetChecksum().AsSpan()))).ToArray();
        CanonicalSourceSet canonical = GeneratedSourceCanonicalizer.Canonicalize(values);
        Dictionary<string, GeneratedSourceValue> byHint = values.ToDictionary(value => value.HintName, StringComparer.Ordinal);
        Dictionary<string, GeneratedSourceResult> resultByHint = sources.ToDictionary(value => value.HintName, StringComparer.Ordinal);
        List<GeneratedSourceObservation> records = [];
        foreach (CanonicalSourceRecord record in canonical.Records)
        {
            GeneratedSourceValue value = byHint[record.HintName];
            byte[] textBytes = StrictUtf8.GetBytes(value.Text);
            records.Add(new GeneratedSourceObservation(
                value.HintName,
                value.Text,
                Convert.ToBase64String(textBytes),
                checked((ulong)value.Text.Length),
                value.EncodingName,
                checked((ulong)value.EncodingPreambleLength),
                resultByHint[value.HintName].SourceText.ChecksumAlgorithm.ToString(),
                value.RoslynChecksum ?? string.Empty,
                record.TextHash));
        }

        return new SourceSnapshot(SnapshotAvailability.Available, null, records, canonical.Sha256);
    }

    private static DiagnosticSnapshot CreateDiagnosticSnapshot(
        IEnumerable<Diagnostic> diagnostics,
        CanonicalPathContext pathContext)
    {
        Diagnostic[] values = diagnostics.ToArray();
        try
        {
            CanonicalDiagnosticSet canonical = DiagnosticCanonicalizer.Canonicalize(values, pathContext);
            List<(byte[] Bytes, Diagnostic Diagnostic)> records = values.Select(diagnostic =>
                (DiagnosticCanonicalizer.CanonicalizeDiagnosticRecord(diagnostic, pathContext), diagnostic)).ToList();
            List<DiagnosticRecordObservation> observations = [];
            foreach (IGrouping<string, (byte[] Bytes, Diagnostic Diagnostic)> group in records
                .GroupBy(item => Convert.ToHexStringLower(item.Bytes), StringComparer.Ordinal)
                .OrderBy(group => group.First().Bytes, Comparer<byte[]>.Create(CompareBytes)))
            {
                Diagnostic diagnostic = group.First().Diagnostic;
                observations.Add(new DiagnosticRecordObservation(
                    diagnostic.Id,
                    diagnostic.Severity.ToString(),
                    diagnostic.IsWarningAsError,
                    diagnostic.IsSuppressed,
                    checked((ulong)diagnostic.WarningLevel),
                    diagnostic.GetMessage(CultureInfo.InvariantCulture),
                    diagnostic.Descriptor.Category,
                    diagnostic.Descriptor.DefaultSeverity.ToString(),
                    diagnostic.Descriptor.HelpLinkUri,
                    diagnostic.Descriptor.CustomTags.OrderBy(tag => tag, StringComparer.Ordinal).ToArray(),
                    CreateLocation(diagnostic.Location, pathContext),
                    diagnostic.AdditionalLocations
                        .Select(location => (Location: location, Bytes: DiagnosticCanonicalizer.CanonicalizeLocation(location, pathContext)))
                        .OrderBy(item => item.Bytes, Comparer<byte[]>.Create(CompareBytes))
                        .Select(item => CreateLocation(item.Location, pathContext))
                        .ToArray(),
                    diagnostic.Properties
                        .OrderBy(property => property.Key, StringComparer.Ordinal)
                        .ThenBy(property => property.Value, StringComparer.Ordinal)
                        .Select(property => new DiagnosticPropertyObservation(property.Key, property.Value))
                        .ToArray(),
                    checked((ulong)group.Count()),
                    Convert.ToBase64String(group.First().Bytes)));
            }

            return new DiagnosticSnapshot(SnapshotAvailability.Available, null, observations, canonical.Sha256);
        }
        catch (UnsupportedLocationKindException)
        {
            return new DiagnosticSnapshot(SnapshotAvailability.Unavailable, "UnsupportedLocationKind", [], null);
        }
    }

    private static LocationV1 CreateLocation(Location location, CanonicalPathContext pathContext)
        => location.Kind == LocationKind.None
            ? LocationV1.None
            : LocationV1.FromCanonical(DiagnosticCanonicalizer.CreateSourceLocation(location, pathContext));

    private static TrackedStepsSnapshot CreateTrackedSteps(GeneratorRunResult result)
    {
        Dictionary<IncrementalGeneratorRunStep, (string Name, ulong Occurrence)> trackedIdentities =
            CreateOccurrenceMap(result.TrackedSteps);
        Dictionary<IncrementalGeneratorRunStep, (string Name, ulong Occurrence)> outputIdentities =
            CreateOccurrenceMap(result.TrackedOutputSteps);

        List<TrackedStepObservation> steps = [];
        HashSet<IncrementalGeneratorRunStep> observed = new(ReferenceEqualityComparer.Instance);
        HashSet<(string Name, ulong Occurrence)> publicIdentities = [];
        foreach ((string name, ImmutableArray<IncrementalGeneratorRunStep> occurrences) in result.TrackedSteps
            .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            for (int occurrence = 0; occurrence < occurrences.Length; occurrence++)
            {
                IncrementalGeneratorRunStep step = occurrences[occurrence];
                ulong publicOccurrence = checked((ulong)occurrence);
                if (step.Name != name || !observed.Add(step) || !publicIdentities.Add((name, publicOccurrence)) ||
                    !TryCreateTrackedInputs(step, trackedIdentities, outputIdentities, out TrackedInputObservation[] inputs))
                {
                    return new TrackedStepsSnapshot(SnapshotAvailability.Unavailable, "MissingPublicEvidence", []);
                }
                TrackedOutputObservation[] outputs = step.Outputs.Select((output, index) => new TrackedOutputObservation(
                    checked((ulong)index),
                    output.Reason.ToString())).ToArray();
                steps.Add(new TrackedStepObservation(name, checked((ulong)occurrence), inputs, outputs));
            }
        }

        foreach ((string name, ImmutableArray<IncrementalGeneratorRunStep> occurrences) in result.TrackedOutputSteps
            .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            for (int occurrence = 0; occurrence < occurrences.Length; occurrence++)
            {
                IncrementalGeneratorRunStep step = occurrences[occurrence];
                if (observed.Contains(step))
                {
                    continue;
                }

                ulong publicOccurrence = checked((ulong)occurrence);
                if (step.Name != name || !observed.Add(step) || !publicIdentities.Add((name, publicOccurrence)) ||
                    !TryCreateTrackedInputs(step, trackedIdentities, outputIdentities, out TrackedInputObservation[] inputs))
                {
                    return new TrackedStepsSnapshot(SnapshotAvailability.Unavailable, "MissingPublicEvidence", []);
                }

                steps.Add(new TrackedStepObservation(
                    name,
                    checked((ulong)occurrence),
                    inputs,
                    step.Outputs.Select((output, index) => new TrackedOutputObservation(
                        checked((ulong)index),
                        output.Reason.ToString())).ToArray()));
            }
        }

        return new TrackedStepsSnapshot(
            SnapshotAvailability.Available,
            null,
            steps.OrderBy(step => step.Name, StringComparer.Ordinal).ThenBy(step => step.Occurrence).ToArray());
    }

    private static Dictionary<IncrementalGeneratorRunStep, (string Name, ulong Occurrence)> CreateOccurrenceMap(
        ImmutableDictionary<string, ImmutableArray<IncrementalGeneratorRunStep>> source)
    {
        Dictionary<IncrementalGeneratorRunStep, (string Name, ulong Occurrence)> identities =
            new(ReferenceEqualityComparer.Instance);
        foreach ((string name, ImmutableArray<IncrementalGeneratorRunStep> occurrences) in source
            .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            for (int occurrence = 0; occurrence < occurrences.Length; occurrence++)
            {
                IncrementalGeneratorRunStep step = occurrences[occurrence];
                if (step.Name != name || !identities.TryAdd(step, (name, checked((ulong)occurrence))))
                {
                    throw new InvalidOperationException("Roslyn returned inconsistent public tracked-step evidence.");
                }
            }
        }

        return identities;
    }

    private static bool TryCreateTrackedInputs(
        IncrementalGeneratorRunStep step,
        IReadOnlyDictionary<IncrementalGeneratorRunStep, (string Name, ulong Occurrence)> trackedIdentities,
        IReadOnlyDictionary<IncrementalGeneratorRunStep, (string Name, ulong Occurrence)> outputIdentities,
        out TrackedInputObservation[] inputs)
    {
        List<TrackedInputObservation> values = [];
        foreach ((IncrementalGeneratorRunStep source, int outputIndex) in step.Inputs)
        {
            if (source.Name is null)
            {
                continue;
            }

            if ((!trackedIdentities.TryGetValue(source, out (string Name, ulong Occurrence) identity) &&
                    !outputIdentities.TryGetValue(source, out identity)) ||
                outputIndex < 0)
            {
                inputs = [];
                return false;
            }

            values.Add(new TrackedInputObservation(identity.Name, identity.Occurrence, checked((ulong)outputIndex)));
        }

        inputs = values
            .OrderBy(value => value.SourceStepName, StringComparer.Ordinal)
            .ThenBy(value => value.SourceOccurrence)
            .ThenBy(value => value.OutputIndex)
            .ToArray();
        return true;
    }

    private EnvironmentEvidence CreateEnvironment()
    {
        RoslynHostEvidence[] host = new[] { typeof(ISourceGenerator).Assembly, typeof(CSharpCompilation).Assembly }
            .Select(assembly => new RoslynHostEvidence(
                assembly.GetName().Name ?? string.Empty,
                assembly.GetName().Version?.ToString() ?? string.Empty,
                assembly.ManifestModule.ModuleVersionId.ToString("D").ToLowerInvariant()))
            .OrderBy(item => item.SimpleName, StringComparer.Ordinal)
            .ToArray();
        return new EnvironmentEvidence(
            Environment.Version.ToString(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            CultureInfo.CurrentCulture.Name,
            CultureInfo.CurrentUICulture.Name,
            TimeZoneInfo.Local.Id,
            host,
            compatibility.PrivateDependencies);
    }

    private static bool IsRoslynFailureDiagnostic(Diagnostic diagnostic)
        => diagnostic.Id is "CS8784" or "CS8785" or "CS8786";

    private static int CompareBytes(byte[] left, byte[] right)
    {
        int length = Math.Min(left.Length, right.Length);
        for (int index = 0; index < length; index++)
        {
            int comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }
}
