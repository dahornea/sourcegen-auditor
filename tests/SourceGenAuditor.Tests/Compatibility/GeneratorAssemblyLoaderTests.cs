using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using SourceGenAuditor.Core.Compatibility;
using SourceGenAuditor.Core.Model;
using SourceGenAuditor.Core.Scenario;
using SourceGenAuditor.Fixtures;
using Xunit;

namespace SourceGenAuditor.Tests.Compatibility;

public sealed class GeneratorAssemblyLoaderTests
{
    [Fact]
    public void ApprovedFixtureHasExactCoveredAdmissionAndNamedType()
    {
        ScenarioDefinition scenario = ScenarioLoader.Load(ScenarioPath("relevant"));

        LoadedGenerator loaded = GeneratorAssemblyLoader.Load(scenario.Generator);

        Assert.Equal(typeof(ConfigurableIncrementalGenerator).FullName, loaded.Generator.GetType().FullName);
        Assert.IsAssignableFrom<IIncrementalGenerator>(loaded.Generator);
        Assert.Equal(AggregateAdmissionDecision.Admitted, loaded.Compatibility.AggregateAdmissionDecision);
        Assert.Equal(FixtureCoverage.Covered, loaded.Compatibility.FixtureCoverage);
        Assert.Contains(loaded.Compatibility.RoslynReferences, item =>
            item.SimpleName == "Microsoft.CodeAnalysis" && item.AdmissionDecision == RoslynAdmissionDecision.EqualHost);
        Assert.Contains(loaded.Compatibility.RoslynReferences, item =>
            item.SimpleName == "Microsoft.CodeAnalysis.CSharp" && item.AdmissionDecision == RoslynAdmissionDecision.EqualHost);
    }

    [Theory]
    [InlineData("5.8.0.0", RoslynAdmissionDecision.LowerThanHost, AggregateAdmissionDecision.Admitted)]
    [InlineData("5.9.0.0", RoslynAdmissionDecision.EqualHost, AggregateAdmissionDecision.Admitted)]
    [InlineData("6.0.0.0", RoslynAdmissionDecision.RejectedNewer, AggregateAdmissionDecision.Rejected)]
    public void SupportedRoslynReferenceUsesLowerEqualHigherPolicy(
        string requestedVersion,
        RoslynAdmissionDecision expectedDecision,
        AggregateAdmissionDecision expectedAggregate)
    {
        using ProbeDirectory probe = new();
        string common = probe.EmitAssembly(
            "Microsoft.CodeAnalysis",
            requestedVersion,
            "namespace Microsoft.CodeAnalysis { public sealed class ProbeType { } }");
        string target = probe.EmitAssembly(
            "RoslynVersionProbe",
            "1.0.0.0",
            "public sealed class Target { public Microsoft.CodeAnalysis.ProbeType? Value { get; } }",
            common);

        CompatibilityEvidence evidence = GeneratorAssemblyLoader.Inspect(Target(target));

        RoslynReferenceDecision decision = Assert.Single(evidence.RoslynReferences);
        Assert.Equal("Microsoft.CodeAnalysis", decision.SimpleName);
        Assert.Equal(requestedVersion, decision.RequestedVersion);
        Assert.Equal(expectedDecision, decision.AdmissionDecision);
        Assert.Equal(expectedAggregate, evidence.AggregateAdmissionDecision);
        Assert.Equal(FixtureCoverage.NotFixtureCovered, evidence.FixtureCoverage);
    }

    [Theory]
    [InlineData("Microsoft.CodeAnalysis.CSharp", "5.8.0.0", RoslynAdmissionDecision.LowerThanHost, AggregateAdmissionDecision.Admitted)]
    [InlineData("Microsoft.CodeAnalysis.CSharp", "5.9.0.0", RoslynAdmissionDecision.EqualHost, AggregateAdmissionDecision.Admitted)]
    [InlineData("Microsoft.CodeAnalysis.CSharp", "6.0.0.0", RoslynAdmissionDecision.RejectedNewer, AggregateAdmissionDecision.Rejected)]
    [InlineData("Microsoft.CodeAnalysis.Workspaces", "5.9.0.0", RoslynAdmissionDecision.RejectedUnsupportedComponent, AggregateAdmissionDecision.Rejected)]
    public void PrivateDependencyRoslynReferenceIsAdmittedAcrossTheCompleteClosure(
        string roslynSimpleName,
        string requestedVersion,
        RoslynAdmissionDecision expectedDecision,
        AggregateAdmissionDecision expectedAggregate)
    {
        using ProbeDirectory probe = new();
        string common = probe.EmitAssembly(
            "Microsoft.CodeAnalysis",
            "5.9.0.0",
            "namespace Microsoft.CodeAnalysis { public sealed class CommonProbe { } }");
        string component = probe.EmitAssembly(
            roslynSimpleName,
            requestedVersion,
            $"namespace {roslynSimpleName} {{ public sealed class ComponentProbe {{ }} }}");
        string dependency = probe.EmitAssembly(
            "Fixture.Private.Dependency",
            "1.0.0.0",
            $"namespace Fixture.Private {{ public sealed class DependencyType {{ public {roslynSimpleName}.ComponentProbe? Value {{ get; }} }} }}",
            component);
        string target = probe.EmitAssembly(
            "TransitiveRoslynProbe",
            "1.0.0.0",
            "public sealed class Target { public Microsoft.CodeAnalysis.CommonProbe? Common { get; } public Fixture.Private.DependencyType? Dependency { get; } }",
            common,
            dependency);

        CompatibilityEvidence evidence = GeneratorAssemblyLoader.Inspect(Target(target));

        RoslynReferenceDecision decision = Assert.Single(
            evidence.RoslynReferences,
            item => item.SimpleName == roslynSimpleName);
        Assert.Equal(requestedVersion, decision.RequestedVersion);
        Assert.Equal(expectedDecision, decision.AdmissionDecision);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dependency))), decision.ReferencingAssemblySha256);
        Assert.Equal(expectedAggregate, evidence.AggregateAdmissionDecision);
        Assert.Contains(evidence.PrivateDependencies, item =>
            item.SimpleName == "Fixture.Private.Dependency" &&
            item.PathToken == "private:Fixture.Private.Dependency.dll");
        Assert.Equal(FixtureCoverage.NotFixtureCovered, evidence.FixtureCoverage);
    }

    [Fact]
    public void UnsupportedRoslynComponentRejectsBeforeCodeLoads()
    {
        using ProbeDirectory probe = new();
        string common = probe.EmitAssembly(
            "Microsoft.CodeAnalysis",
            "5.9.0.0",
            "namespace Microsoft.CodeAnalysis { public sealed class CommonProbe { } }");
        string workspaces = probe.EmitAssembly(
            "Microsoft.CodeAnalysis.Workspaces",
            "5.9.0.0",
            "namespace Microsoft.CodeAnalysis.Workspaces { public sealed class WorkspaceProbe { } }");
        string target = probe.EmitAssembly(
            "UnsupportedRoslynProbe",
            "1.0.0.0",
            "public sealed class Target { public Microsoft.CodeAnalysis.CommonProbe? Common { get; } public Microsoft.CodeAnalysis.Workspaces.WorkspaceProbe? Workspace { get; } }",
            common,
            workspaces);

        CompatibilityEvidence evidence = GeneratorAssemblyLoader.Inspect(Target(target));

        Assert.Equal(AggregateAdmissionDecision.Rejected, evidence.AggregateAdmissionDecision);
        Assert.Contains(evidence.RoslynReferences, item =>
            item.SimpleName == "Microsoft.CodeAnalysis.Workspaces" &&
            item.AdmissionDecision == RoslynAdmissionDecision.RejectedUnsupportedComponent &&
            item.HostVersion is null);
    }

    [Fact]
    public void MissingPrivateDependencyIsTypedLoadFailure()
    {
        using ProbeDirectory probe = new();
        string dependency = probe.EmitAssembly(
            "Fixture.Private.Dependency",
            "1.0.0.0",
            "namespace Fixture.Private { public sealed class DependencyType { } }");
        string target = probe.EmitAssembly(
            "MissingDependencyProbe",
            "1.0.0.0",
            "public sealed class Target { public Fixture.Private.DependencyType? Value { get; } }",
            dependency);
        File.Delete(dependency);

        GeneratorLoadException exception = Assert.Throws<GeneratorLoadException>(() => GeneratorAssemblyLoader.Inspect(Target(target)));

        Assert.Contains("Fixture.Private.Dependency", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LookalikeWithApprovedAssemblyAndTypeNamesIsNotFixtureCovered()
    {
        using ProbeDirectory probe = new();
        string common = probe.EmitAssembly(
            "Microsoft.CodeAnalysis",
            "5.9.0.0",
            "namespace Microsoft.CodeAnalysis { public sealed class CommonProbe { } }");
        string csharp = probe.EmitAssembly(
            "Microsoft.CodeAnalysis.CSharp",
            "5.9.0.0",
            "namespace Microsoft.CodeAnalysis.CSharp { public sealed class CSharpProbe { } }");
        string target = probe.EmitAssembly(
            "SourceGenAuditor.Fixtures",
            "1.0.0.0",
            "namespace SourceGenAuditor.Fixtures { public sealed class ConfigurableIncrementalGenerator { public Microsoft.CodeAnalysis.CommonProbe? Common { get; } public Microsoft.CodeAnalysis.CSharp.CSharpProbe? CSharp { get; } } }",
            common,
            csharp);

        GeneratorTarget generator = Target(target) with
        {
            TypeName = "SourceGenAuditor.Fixtures.ConfigurableIncrementalGenerator",
        };
        CompatibilityEvidence evidence = GeneratorAssemblyLoader.Inspect(generator);

        Assert.Equal(AggregateAdmissionDecision.Admitted, evidence.AggregateAdmissionDecision);
        Assert.Equal(FixtureCoverage.NotFixtureCovered, evidence.FixtureCoverage);
    }

    private static GeneratorTarget Target(string path)
    {
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        return new GeneratorTarget(path, sha256, "Probe.Target", [.. File.ReadAllBytes(path)]);
    }

    private static string ScenarioPath(string name)
        => Path.Combine(FindRepositoryRoot(), "tests", "scenarios", name, "scenario.json");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SourceGenAuditor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class ProbeDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"sga-loader-{Guid.NewGuid():N}");

        public ProbeDirectory()
        {
            Directory.CreateDirectory(root);
        }

        public string EmitAssembly(string assemblyName, string version, string source, params string[] references)
        {
            string assemblyPath = Path.Combine(root, $"{assemblyName}.dll");
            string versionSource = $"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]\n{source}";
            List<MetadataReference> metadataReferences =
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            ];
            metadataReferences.AddRange(references.Select(path => MetadataReference.CreateFromFile(path)));

            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName,
                [CSharpSyntaxTree.ParseText(versionSource)],
                metadataReferences,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            EmitResult result = compilation.Emit(assemblyPath);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return assemblyPath;
        }

        public void Dispose()
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
