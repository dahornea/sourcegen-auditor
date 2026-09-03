using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SourceGenAuditor.Core.Model;

namespace SourceGenAuditor.Core.Compatibility;

public sealed record LoadedGenerator(
    IIncrementalGenerator Generator,
    CompatibilityEvidence Compatibility,
    AssemblyLoadContext LoadContext);

public static class GeneratorAssemblyLoader
{
    private const string CommonName = "Microsoft.CodeAnalysis";
    private const string CSharpName = "Microsoft.CodeAnalysis.CSharp";
    private const string ApprovedFixtureAssemblyName = "SourceGenAuditor.Fixtures";
    private const string ApprovedFixtureType = "SourceGenAuditor.Fixtures.ConfigurableIncrementalGenerator";
    private const string ApprovedFixtureSha256 = "0f22ceda1bb8d75701a962c325b68f9dc0fd202018bea4e0f170a48b88da3fa1";
    private static readonly HashSet<string> TrustedPlatformAssemblyNames = LoadTrustedPlatformAssemblyNames();

    public static LoadedGenerator Load(GeneratorTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        InspectionResult inspection = InspectSnapshot(target);
        CompatibilityEvidence compatibility = inspection.Evidence;
        if (compatibility.AggregateAdmissionDecision == AggregateAdmissionDecision.Rejected)
        {
            throw new GeneratorCompatibilityException("The generator's Roslyn reference closure was rejected.", compatibility);
        }

        Dictionary<string, ImmutableArray<byte>> privateImages = inspection.PrivateDependencies
            .ToDictionary(dependency => dependency.SimpleName, dependency => dependency.Bytes, StringComparer.Ordinal);
        GeneratorLoadContext context = new(privateImages);
        try
        {
            using MemoryStream targetStream = new(target.AssemblyBytes.AsSpan().ToArray(), writable: false);
            Assembly assembly = context.LoadFromStream(targetStream);
            Type type = assembly.GetType(target.TypeName, throwOnError: false, ignoreCase: false)
                ?? throw new GeneratorLoadException("The exact configured generator type was not found.");
            if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters || !typeof(IIncrementalGenerator).IsAssignableFrom(type))
            {
                throw new GeneratorLoadException("The configured type is not a concrete IIncrementalGenerator.");
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new GeneratorLoadException("The configured generator type has no public parameterless constructor.");
            }

            object instance = Activator.CreateInstance(type)
                ?? throw new GeneratorLoadException("The configured generator type could not be instantiated.");
            return new LoadedGenerator((IIncrementalGenerator)instance, compatibility, context);
        }
        catch (GeneratorLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or FileNotFoundException or TypeLoadException or MissingMethodException or TargetInvocationException)
        {
            throw new GeneratorLoadException("The configured generator could not be loaded.", exception);
        }
    }

    public static CompatibilityEvidence Inspect(GeneratorTarget target)
        => InspectSnapshot(target).Evidence;

    private static InspectionResult InspectSnapshot(GeneratorTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string generatorDirectory = Path.GetDirectoryName(target.AssemblyPath)
            ?? throw new GeneratorLoadException("The generator directory could not be resolved.");
        generatorDirectory = Path.GetFullPath(generatorDirectory);

        InspectedAssembly targetAssembly = InspectAssembly(target.AssemblyPath, target.AssemblyBytes, isTarget: true);
        if (!StringComparer.Ordinal.Equals(targetAssembly.Sha256, target.Sha256))
        {
            throw new GeneratorLoadException("The immutable generator image does not match its declared SHA-256.");
        }

        Dictionary<string, InspectedAssembly> candidates = EnumerateManagedCandidates(generatorDirectory, target.AssemblyPath);
        if (candidates.ContainsKey(targetAssembly.SimpleName))
        {
            throw new GeneratorLoadException($"Multiple private dependency files have the simple name '{targetAssembly.SimpleName}'.");
        }

        Dictionary<string, InspectedAssembly> closure = new(StringComparer.Ordinal);
        Queue<InspectedAssembly> pending = new();
        pending.Enqueue(targetAssembly);
        List<RoslynReferenceDecision> roslynReferences = [];
        bool hasDirectCommon = false;

        while (pending.Count > 0)
        {
            InspectedAssembly inspected = pending.Dequeue();
            if (closure.TryGetValue(inspected.SimpleName, out InspectedAssembly? existing))
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(existing.Path, inspected.Path))
                {
                    throw new GeneratorLoadException($"Duplicate private assembly identity '{inspected.SimpleName}'.");
                }

                continue;
            }

            closure.Add(inspected.SimpleName, inspected);
            foreach (AssemblyReferenceInfo reference in inspected.References)
            {
                if (reference.SimpleName.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
                {
                    if (inspected.IsTarget && reference.SimpleName == CommonName)
                    {
                        hasDirectCommon = true;
                    }

                    roslynReferences.Add(CreateDecision(inspected.Sha256, reference));
                    continue;
                }

                if (candidates.TryGetValue(reference.SimpleName, out InspectedAssembly? dependency) && !dependency.IsTarget)
                {
                    pending.Enqueue(dependency);
                    continue;
                }

                if (!TrustedPlatformAssemblyNames.Contains(reference.SimpleName))
                {
                    throw new GeneratorLoadException($"Private dependency '{reference.SimpleName}' could not be resolved from the generator directory.");
                }
            }
        }

        if (!hasDirectCommon)
        {
            throw new GeneratorLoadException("The generator target has no direct Microsoft.CodeAnalysis reference.");
        }

        RoslynReferenceDecision[] orderedReferences = roslynReferences
            .OrderBy(reference => reference.SimpleName, StringComparer.Ordinal)
            .ThenBy(reference => Version.Parse(reference.RequestedVersion))
            .ThenBy(reference => reference.ReferencingAssemblySha256, StringComparer.Ordinal)
            .ToArray();
        AggregateAdmissionDecision aggregate = orderedReferences.Any(reference => reference.AdmissionDecision is RoslynAdmissionDecision.RejectedNewer or RoslynAdmissionDecision.RejectedUnsupportedComponent)
            ? AggregateAdmissionDecision.Rejected
            : AggregateAdmissionDecision.Admitted;

        PrivateDependencyEvidence[] privateDependencies = closure.Values
            .Where(assembly => !assembly.IsTarget)
            .Select(assembly => new PrivateDependencyEvidence(
                assembly.SimpleName,
                CreatePrivateToken(generatorDirectory, assembly.Path),
                assembly.Sha256,
                assembly.Path))
            .OrderBy(dependency => dependency.PathToken, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.SimpleName, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Sha256, StringComparer.Ordinal)
            .ToArray();

        bool covered = targetAssembly.SimpleName == ApprovedFixtureAssemblyName &&
            target.TypeName == ApprovedFixtureType && targetAssembly.Sha256 == ApprovedFixtureSha256 &&
            aggregate == AggregateAdmissionDecision.Admitted &&
            orderedReferences.Any(reference => reference.SimpleName == CommonName) &&
            orderedReferences.Any(reference => reference.SimpleName == CSharpName) &&
            orderedReferences.All(reference => reference.AdmissionDecision == RoslynAdmissionDecision.EqualHost);

        CompatibilityEvidence evidence = new(
            orderedReferences,
            aggregate,
            covered ? FixtureCoverage.Covered : FixtureCoverage.NotFixtureCovered,
            privateDependencies);
        return new InspectionResult(
            evidence,
            closure.Values.Where(assembly => !assembly.IsTarget).OrderBy(assembly => assembly.SimpleName, StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<string, InspectedAssembly> EnumerateManagedCandidates(string directory, string targetPath)
    {
        Dictionary<string, InspectedAssembly> candidates = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), Path.GetFullPath(targetPath)))
            {
                continue;
            }

            try
            {
                ImmutableArray<byte> bytes = [.. File.ReadAllBytes(path)];
                InspectedAssembly inspected = InspectAssembly(path, bytes, isTarget: false);

                if (!candidates.TryAdd(inspected.SimpleName, inspected))
                {
                    throw new GeneratorLoadException($"Multiple private dependency files have the simple name '{inspected.SimpleName}'.");
                }
            }
            catch (GeneratorLoadException exception) when (exception.InnerException is BadImageFormatException)
            {
                // Native DLLs are outside the managed private closure.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new GeneratorLoadException($"Private dependency inspection failed for '{Path.GetFileName(path)}'.", exception);
            }
        }

        return candidates;
    }

    private static HashSet<string> LoadTrustedPlatformAssemblyNames()
    {
        string? trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            throw new GeneratorLoadException("The trusted platform assembly inventory is unavailable.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string path in trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static InspectedAssembly InspectAssembly(string path, ImmutableArray<byte> bytes, bool isTarget)
    {
        try
        {
            using MemoryStream stream = new(bytes.AsSpan().ToArray(), writable: false);
            using PEReader reader = new(stream, PEStreamOptions.LeaveOpen);
            if (!reader.HasMetadata)
            {
                throw new BadImageFormatException("The file has no managed metadata.");
            }

            MetadataReader metadata = reader.GetMetadataReader();
            AssemblyDefinition definition = metadata.GetAssemblyDefinition();
            string simpleName = metadata.GetString(definition.Name);
            List<AssemblyReferenceInfo> references = [];
            foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
            {
                AssemblyReference reference = metadata.GetAssemblyReference(handle);
                references.Add(new AssemblyReferenceInfo(metadata.GetString(reference.Name), NormalizeVersion(reference.Version)));
            }

            return new InspectedAssembly(
                Path.GetFullPath(path),
                simpleName,
                Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan())),
                references,
                bytes,
                isTarget);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            throw new GeneratorLoadException($"Managed metadata inspection failed for '{Path.GetFileName(path)}'.", exception);
        }
    }

    private static RoslynReferenceDecision CreateDecision(string referencingHash, AssemblyReferenceInfo reference)
    {
        Assembly? host = reference.SimpleName switch
        {
            CommonName => typeof(ISourceGenerator).Assembly,
            CSharpName => typeof(CSharpCompilation).Assembly,
            _ => null,
        };
        if (host is null)
        {
            return new RoslynReferenceDecision(
                referencingHash,
                reference.SimpleName,
                reference.Version,
                null,
                RoslynAdmissionDecision.RejectedUnsupportedComponent);
        }

        Version requested = Version.Parse(reference.Version);
        Version hostVersion = host.GetName().Version ?? throw new GeneratorLoadException("A host Roslyn assembly version is missing.");
        RoslynAdmissionDecision decision = requested > hostVersion
            ? RoslynAdmissionDecision.RejectedNewer
            : requested == hostVersion
                ? RoslynAdmissionDecision.EqualHost
                : RoslynAdmissionDecision.LowerThanHost;
        return new RoslynReferenceDecision(
            referencingHash,
            reference.SimpleName,
            NormalizeVersion(requested),
            NormalizeVersion(hostVersion),
            decision);
    }

    private static string CreatePrivateToken(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative.Length == 0 || relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new GeneratorLoadException("A private dependency path escapes the generator directory.");
        }

        return $"private:{relative}";
    }

    private static string NormalizeVersion(Version version)
        => $"{Math.Max(0, version.Major)}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}.{Math.Max(0, version.Revision)}";

    private sealed record AssemblyReferenceInfo(string SimpleName, string Version);

    private sealed record InspectedAssembly(
        string Path,
        string SimpleName,
        string Sha256,
        IReadOnlyList<AssemblyReferenceInfo> References,
        ImmutableArray<byte> Bytes,
        bool IsTarget);

    private sealed record InspectionResult(
        CompatibilityEvidence Evidence,
        IReadOnlyList<InspectedAssembly> PrivateDependencies);

    private sealed class GeneratorLoadContext(IReadOnlyDictionary<string, ImmutableArray<byte>> privateImages) : AssemblyLoadContext(isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == CommonName)
            {
                return typeof(ISourceGenerator).Assembly;
            }

            if (assemblyName.Name == CSharpName)
            {
                return typeof(CSharpCompilation).Assembly;
            }

            if (assemblyName.Name is not null && privateImages.TryGetValue(assemblyName.Name, out ImmutableArray<byte> image))
            {
                using MemoryStream stream = new(image.AsSpan().ToArray(), writable: false);
                return LoadFromStream(stream);
            }

            return null;
        }
    }
}
