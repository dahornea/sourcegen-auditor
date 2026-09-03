using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SourceGenAuditor.Core.Model;

namespace SourceGenAuditor.Core.Scenario;

public static class ScenarioLoader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static ScenarioDefinition Load(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ScenarioValidationException("A scenario manifest path is required.");
        }

        string fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
        {
            throw new ScenarioValidationException("The scenario manifest does not exist.");
        }

        byte[] manifestBytes = File.ReadAllBytes(fullManifestPath);
        ValidateStrictJson(manifestBytes);
        ScenarioDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ScenarioDocument>(manifestBytes, JsonOptions)
                ?? throw new ScenarioValidationException("The scenario manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new ScenarioValidationException("The scenario manifest does not match schema V1.", exception);
        }

        ValidateRequired(document);
        string scenarioDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new ScenarioValidationException("The scenario directory cannot be resolved.");
        scenarioDirectory = Path.GetFullPath(scenarioDirectory);

        string generatorPath = ResolveContainedFile(scenarioDirectory, document.Generator!.AssemblyPath!, "generator assembly");
        ImmutableArray<byte> generatorBytes = ReadAndVerify(generatorPath, document.Generator.Sha256!, "generator assembly");
        string generatorHash = HashHex(generatorBytes.AsSpan());

        List<SourceInput> sources = [];
        HashSet<string> logicalPaths = new(StringComparer.Ordinal);
        foreach (SourceDocument? source in document.Baseline!.Sources!)
        {
            ValidateSource(source);
            SourceDocument sourceValue = source!;
            string normalizedLogicalPath = NormalizeLogicalPath(sourceValue.LogicalPath!);
            if (!logicalPaths.Add(normalizedLogicalPath))
            {
                throw new ScenarioValidationException($"Duplicate logical source path '{normalizedLogicalPath}'.");
            }

            string physicalPath = ResolveContainedFile(scenarioDirectory, sourceValue.Path!, "source input");
            ImmutableArray<byte> sourceBytes = ReadAndVerify(physicalPath, sourceValue.Sha256!, "source input");
            string hash = HashHex(sourceBytes.AsSpan());
            sources.Add(new SourceInput(normalizedLogicalPath, physicalPath, hash, DecodeStrictUtf8(sourceBytes.AsSpan(), "source input")));
        }

        List<ReferenceInput> references = [];
        HashSet<string> referenceIdentities = new(StringComparer.Ordinal);
        foreach (ReferenceDocument? reference in document.Baseline.References!)
        {
            ValidateReference(reference);
            ReferenceDocument referenceValue = reference!;
            string physicalPath = ResolveContainedFile(scenarioDirectory, referenceValue.Path!, "metadata reference");
            ImmutableArray<byte> referenceBytes = ReadAndVerify(physicalPath, referenceValue.Sha256!, "metadata reference");
            string hash = HashHex(referenceBytes.AsSpan());
            string identity;
            try
            {
                identity = ReadAssemblyIdentity(referenceBytes.AsSpan());
            }
            catch (Exception exception) when (exception is BadImageFormatException or InvalidOperationException)
            {
                throw new ScenarioValidationException("A metadata reference is not a valid managed assembly.", exception);
            }

            if (!referenceIdentities.Add(identity))
            {
                throw new ScenarioValidationException($"Duplicate metadata reference identity '{identity}'.");
            }

            references.Add(new ReferenceInput(physicalPath, hash, identity, referenceBytes));
        }

        ValidateParseOptions(document.Baseline.ParseOptions!);
        ValidateCompilationOptions(document.Baseline.CompilationOptions!);
        ValidateMutation(document.Mutation!, logicalPaths);
        string replacementPath = ResolveContainedFile(
            scenarioDirectory,
            document.Mutation!.ReplacementPath!,
            "replacement source");
        ImmutableArray<byte> replacementBytes = ReadAndVerify(
            replacementPath,
            document.Mutation.ReplacementSha256!,
            "replacement source");
        string replacementHash = HashHex(replacementBytes.AsSpan());

        return new ScenarioDefinition(
            fullManifestPath,
            scenarioDirectory,
            HashHex(manifestBytes),
            document.Id!,
            new GeneratorTarget(generatorPath, generatorHash, document.Generator.TypeName!, generatorBytes),
            new ControlledInputSet(
                document.Baseline.AssemblyName!,
                sources.OrderBy(source => source.LogicalPath, StringComparer.Ordinal).ToArray(),
                references.OrderBy(reference => reference.AssemblyIdentity, StringComparer.Ordinal).ToArray(),
                new ParseOptionsDefinition(
                    document.Baseline.ParseOptions!.LanguageVersion!,
                    document.Baseline.ParseOptions.DocumentationMode!,
                    document.Baseline.ParseOptions.PreprocessorSymbols!.OrderBy(value => value, StringComparer.Ordinal).ToArray()),
                new CompilationOptionsDefinition(
                    document.Baseline.CompilationOptions!.OutputKind!,
                    document.Baseline.CompilationOptions.NullableContext!,
                    document.Baseline.CompilationOptions.AllowUnsafe!.Value)),
            new MutationDefinition(
                document.Mutation.Id!,
                NormalizeLogicalPath(document.Mutation.TargetLogicalPath!),
                replacementPath,
                replacementHash,
                DecodeStrictUtf8(replacementBytes.AsSpan(), "replacement source"),
                ParseRelevance(document.Mutation.Relevance!),
                new MutationExpectations(
                    ParseEffect(document.Mutation.Expectations!.GeneratedSources!),
                    ParseEffect(document.Mutation.Expectations.GeneratorDiagnostics!))));
    }

    public static ScenarioLease AcquireLease(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ScenarioValidationException("A scenario manifest path is required.");
        }

        string fullManifestPath;
        try
        {
            fullManifestPath = Path.GetFullPath(manifestPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ScenarioValidationException("The scenario manifest path is invalid.", exception);
        }

        if (!File.Exists(fullManifestPath))
        {
            throw new ScenarioValidationException("The scenario manifest does not exist.");
        }

        List<FileStream> streams = [];
        try
        {
            streams.Add(OpenLease(fullManifestPath));
            ScenarioDefinition discovered = Load(fullManifestPath);
            IEnumerable<string> declaredPaths = discovered.Baseline.Sources.Select(source => source.PhysicalPath)
                .Concat(discovered.Baseline.References.Select(reference => reference.PhysicalPath))
                .Append(discovered.Mutation.ReplacementPath)
                .Append(discovered.Generator.AssemblyPath)
                .Concat(Directory.EnumerateFiles(
                    Path.GetDirectoryName(discovered.Generator.AssemblyPath)!,
                    "*.dll",
                    SearchOption.TopDirectoryOnly));
            foreach (string path in declaredPaths
                .Select(Path.GetFullPath)
                .Where(path => !StringComparer.OrdinalIgnoreCase.Equals(path, fullManifestPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                streams.Add(OpenLease(path));
            }

            ScenarioDefinition locked = Load(fullManifestPath);
            return new ScenarioLease(locked, streams);
        }
        catch (Exception exception)
        {
            foreach (FileStream stream in streams)
            {
                stream.Dispose();
            }

            if (exception is IOException or UnauthorizedAccessException)
            {
                throw new ScenarioValidationException("The declared scenario files could not be leased for immutable execution.", exception);
            }

            throw;
        }
    }

    private static FileStream OpenLease(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);

    private static void ValidateStrictJson(byte[] bytes)
    {
        try
        {
            _ = StrictUtf8.GetString(bytes);
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            ValidateNoDuplicateProperties(document.RootElement);
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException)
        {
            throw new ScenarioValidationException("The scenario manifest is not strict UTF-8 JSON.", exception);
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
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

                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item);
            }
        }
    }

    private static void ValidateRequired(ScenarioDocument document)
    {
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.Id) || document.Generator is null ||
            document.Baseline is null || document.Mutation is null)
        {
            throw new ScenarioValidationException("The scenario requires schemaVersion 1, id, generator, baseline, and mutation.");
        }

        if (string.IsNullOrWhiteSpace(document.Generator.AssemblyPath) || !IsHash(document.Generator.Sha256) ||
            string.IsNullOrWhiteSpace(document.Generator.TypeName))
        {
            throw new ScenarioValidationException("The generator target is incomplete or malformed.");
        }

        if (string.IsNullOrWhiteSpace(document.Baseline.AssemblyName) || document.Baseline.Sources is null or { Count: 0 } ||
            document.Baseline.References is null or { Count: 0 } || document.Baseline.ParseOptions is null ||
            document.Baseline.CompilationOptions is null)
        {
            throw new ScenarioValidationException("The baseline requires an assembly name, source, reference, parse options, and compilation options.");
        }
    }

    private static void ValidateSource(SourceDocument? source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.LogicalPath) || string.IsNullOrWhiteSpace(source.Path) || !IsHash(source.Sha256))
        {
            throw new ScenarioValidationException("A source input is incomplete or malformed.");
        }
    }

    private static void ValidateReference(ReferenceDocument? reference)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Path) || !IsHash(reference.Sha256))
        {
            throw new ScenarioValidationException("A metadata reference is incomplete or malformed.");
        }
    }

    private static void ValidateParseOptions(ParseOptionsDocument value)
    {
        if (value.LanguageVersion != "14.0" || value.DocumentationMode != "parse" || value.PreprocessorSymbols is null ||
            value.PreprocessorSymbols.Any(string.IsNullOrWhiteSpace) || value.PreprocessorSymbols.Distinct(StringComparer.Ordinal).Count() != value.PreprocessorSymbols.Count)
        {
            throw new ScenarioValidationException("Phase 1 requires languageVersion 14.0, documentationMode parse, and unique nonempty preprocessor symbols.");
        }
    }

    private static void ValidateCompilationOptions(CompilationOptionsDocument value)
    {
        if (value.OutputKind != "dynamicallyLinkedLibrary" || value.NullableContext != "enable" || value.AllowUnsafe is null)
        {
            throw new ScenarioValidationException("Phase 1 requires dynamicallyLinkedLibrary output and nullable enable.");
        }
    }

    private static void ValidateMutation(MutationDocument value, HashSet<string> logicalPaths)
    {
        if (string.IsNullOrWhiteSpace(value.Id) || value.Kind != "replaceSourceText" ||
            string.IsNullOrWhiteSpace(value.TargetLogicalPath) || string.IsNullOrWhiteSpace(value.ReplacementPath) ||
            !IsHash(value.ReplacementSha256) || value.Expectations is null ||
            value.Relevance is not ("relevant" or "irrelevant") ||
            value.Expectations.GeneratedSources is not ("changed" or "unchanged") ||
            value.Expectations.GeneratorDiagnostics is not ("changed" or "unchanged"))
        {
            throw new ScenarioValidationException("The source-replacement mutation is incomplete or malformed.");
        }

        if (!logicalPaths.Contains(NormalizeLogicalPath(value.TargetLogicalPath)))
        {
            throw new ScenarioValidationException("The mutation target is not a baseline logical source path.");
        }
    }

    private static string ResolveContainedFile(string root, string relativePath, string label)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ScenarioValidationException($"The {label} path must be relative.");
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            throw new ScenarioValidationException($"The {label} path escapes the scenario directory or does not exist.");
        }

        string relative = Path.GetRelativePath(root, fullPath);
        string current = root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = File.Exists(current) ? new FileInfo(current) : new DirectoryInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !Path.GetFullPath(target.FullName).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ScenarioValidationException($"The {label} resolves through a reparse point outside the scenario directory.");
                }
            }
        }

        return fullPath;
    }

    private static ImmutableArray<byte> ReadAndVerify(string path, string expected, string label)
    {
        ImmutableArray<byte> bytes = [.. File.ReadAllBytes(path)];
        string actual = HashHex(bytes.AsSpan());
        if (!StringComparer.Ordinal.Equals(actual, expected))
        {
            throw new ScenarioValidationException($"The {label} SHA-256 does not match the manifest.");
        }

        return bytes;
    }

    private static string NormalizeLogicalPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ScenarioValidationException("A logical path is empty, rooted, or contains an escaping segment.");
        }

        return normalized;
    }

    private static string DecodeStrictUtf8(ReadOnlySpan<byte> bytes, string label)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ScenarioValidationException($"The {label} is not strict UTF-8.", exception);
        }
    }

    private static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string HashHex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string ReadAssemblyIdentity(ReadOnlySpan<byte> bytes)
    {
        using MemoryStream stream = new(bytes.ToArray(), writable: false);
        using PEReader pe = new(stream);
        if (!pe.HasMetadata)
        {
            throw new BadImageFormatException("The reference has no managed metadata.");
        }

        MetadataReader metadata = pe.GetMetadataReader();
        AssemblyDefinition definition = metadata.GetAssemblyDefinition();
        AssemblyName name = new(metadata.GetString(definition.Name))
        {
            Version = definition.Version,
        };
        if (!definition.Culture.IsNil)
        {
            name.CultureName = metadata.GetString(definition.Culture);
        }

        if (!definition.PublicKey.IsNil)
        {
            name.SetPublicKey(metadata.GetBlobBytes(definition.PublicKey));
        }

        return name.FullName ?? throw new BadImageFormatException("The assembly identity is missing.");
    }

    private static MutationRelevance ParseRelevance(string value)
        => value == "relevant" ? MutationRelevance.Relevant : MutationRelevance.Irrelevant;

    private static ExpectedEffect ParseEffect(string value)
        => value == "changed" ? ExpectedEffect.Changed : ExpectedEffect.Unchanged;

    private sealed class ScenarioDocument
    {
        public int SchemaVersion { get; init; }
        public string? Id { get; init; }
        public GeneratorDocument? Generator { get; init; }
        public BaselineDocument? Baseline { get; init; }
        public MutationDocument? Mutation { get; init; }
    }

    private sealed class GeneratorDocument
    {
        public string? AssemblyPath { get; init; }
        public string? Sha256 { get; init; }
        public string? TypeName { get; init; }
    }

    private sealed class BaselineDocument
    {
        public string? AssemblyName { get; init; }
        public List<SourceDocument?>? Sources { get; init; }
        public List<ReferenceDocument?>? References { get; init; }
        public ParseOptionsDocument? ParseOptions { get; init; }
        public CompilationOptionsDocument? CompilationOptions { get; init; }
    }

    private sealed class SourceDocument
    {
        public string? LogicalPath { get; init; }
        public string? Path { get; init; }
        public string? Sha256 { get; init; }
    }

    private sealed class ReferenceDocument
    {
        public string? Path { get; init; }
        public string? Sha256 { get; init; }
    }

    private sealed class ParseOptionsDocument
    {
        public string? LanguageVersion { get; init; }
        public string? DocumentationMode { get; init; }
        public List<string>? PreprocessorSymbols { get; init; }
    }

    private sealed class CompilationOptionsDocument
    {
        public string? OutputKind { get; init; }
        public string? NullableContext { get; init; }
        public bool? AllowUnsafe { get; init; }
    }

    private sealed class MutationDocument
    {
        public string? Id { get; init; }
        public string? Kind { get; init; }
        public string? TargetLogicalPath { get; init; }
        public string? ReplacementPath { get; init; }
        public string? ReplacementSha256 { get; init; }
        public string? Relevance { get; init; }
        public ExpectationsDocument? Expectations { get; init; }
    }

    private sealed class ExpectationsDocument
    {
        public string? GeneratedSources { get; init; }
        public string? GeneratorDiagnostics { get; init; }
    }
}

public sealed class ScenarioLease : IDisposable
{
    private readonly IReadOnlyList<FileStream> streams;

    internal ScenarioLease(ScenarioDefinition scenario, IReadOnlyList<FileStream> streams)
    {
        Scenario = scenario;
        this.streams = streams;
    }

    public ScenarioDefinition Scenario { get; }

    public void Dispose()
    {
        foreach (FileStream stream in streams)
        {
            stream.Dispose();
        }
    }
}
