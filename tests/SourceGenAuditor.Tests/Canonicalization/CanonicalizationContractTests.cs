using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SourceGenAuditor.Core.Canonicalization;
using Xunit;

namespace SourceGenAuditor.Tests.Canonicalization;

public sealed class CanonicalizationContractTests
{
    public static TheoryData<string?, string, string> SourceVectors => new()
    {
        {
            null,
            "00000000000000117367612d736f757263652d7365742d76310000000000000000",
            "b28b860f8b7a846ce6115b4913d6c6fdb3370869f2664152d9917f0362ad1586"
        },
        {
            "x",
            "00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002c000000000000000d7367612d736f757263652d76310000000000000006412e672e6373000000000000000178",
            "5fd0ef7f721b43a4fb3a89f2519cd908afd58796b8354e8e28ba366ab20ff744"
        },
        {
            "\u00e9",
            "00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002d000000000000000d7367612d736f757263652d76310000000000000006412e672e63730000000000000002c3a9",
            "34350d1fddc2c27512c246e8bf8bec5ec25afd6db7bc081116bcfe24306b3773"
        },
        {
            "e\u0301",
            "00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002e000000000000000d7367612d736f757263652d76310000000000000006412e672e6373000000000000000365cc81",
            "06a411fa1b78d672510fccdad4932912cfac5005351a59d9ad7db01c66bf8c40"
        },
        {
            "\ufeff",
            "00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002e000000000000000d7367612d736f757263652d76310000000000000006412e672e63730000000000000003efbbbf",
            "331c7aa9b0219298cf01c15b05e5b398fca88fac1d0e42725889610588587006"
        }
    };

    [Theory]
    [MemberData(nameof(SourceVectors))]
    public void SourceVectorsMatchExactly(string? text, string expectedHex, string expectedHash)
    {
        CanonicalSourceSet result = GeneratedSourceCanonicalizer.Canonicalize(
            text is null ? [] : [new GeneratedSourceValue("A.g.cs", text)]);

        Assert.Equal(expectedHex, Convert.ToHexStringLower(result.Bytes));
        Assert.Equal(expectedHash, result.Sha256);
    }

    [Fact]
    public void EmptyDiagnosticSetVectorMatchesExactly()
    {
        CanonicalDiagnosticSet result = DiagnosticCanonicalizer.Canonicalize([]);

        Assert.Equal(
            "00000000000000157367612d646961676e6f737469632d7365742d76310000000000000000",
            Convert.ToHexStringLower(result.Bytes));
        Assert.Equal("319425da831882c5bbfdcabedb1b577646d92833db95987f57c1c852b574b048", result.Sha256);
    }

    [Fact]
    public void DuplicateDiagnosticVectorMatchesExactly()
    {
        DiagnosticDescriptor descriptor = new(
            "SGA001",
            "title",
            "caf\u00e9",
            "Test",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            customTags: ["tag"]);
        Diagnostic diagnostic = Diagnostic.Create(
            descriptor,
            Location.None,
            additionalLocations: null,
            properties: ImmutableDictionary<string, string?>.Empty.Add("k", null));

        CanonicalDiagnosticSet result = DiagnosticCanonicalizer.Canonicalize([diagnostic, diagnostic]);

        Assert.Equal(
            "00000000000000157367612d646961676e6f737469632d7365742d7631000000000000000100000000000000c900000000000000117367612d646961676e6f737469632d76310100000000000000065347413030310100000000000000075761726e696e6700000000000000000001010000000000000005636166c3a9010000000000000004546573740100000000000000075761726e696e670100000000000000000000000000000001000000000000000c010000000000000003746167000000000000000c00000000000000046e6f6e6500000000000000000000000000000001000000000000000b0100000000000000016b000000000000000002",
            Convert.ToHexStringLower(result.Bytes));
        Assert.Equal("b2bbc1538dc65a17d1d7422f7d43e28bf739ad0afe1978b8db455c95fb1f0bb2", result.Sha256);
        Assert.Equal(2UL, Assert.Single(result.Entries).OccurrenceCount);
    }

    [Fact]
    public void DiagnosticMessageCanonicalizationIsInvariantAcrossCurrentCultures()
    {
        DiagnosticDescriptor descriptor = new(
            "SGA001",
            "title",
            "Value {0:N2}",
            "Test",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);
        Diagnostic diagnostic = Diagnostic.Create(descriptor, Location.None, 1234.5m);
        CultureInfo originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            byte[] frenchCurrentCulture = DiagnosticCanonicalizer.CanonicalizeDiagnosticRecord(diagnostic);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            byte[] germanCurrentCulture = DiagnosticCanonicalizer.CanonicalizeDiagnosticRecord(diagnostic);

            Assert.NotEqual(
                diagnostic.GetMessage(CultureInfo.GetCultureInfo("fr-FR")),
                diagnostic.GetMessage(CultureInfo.InvariantCulture));
            Assert.Equal("Value 1,234.50", diagnostic.GetMessage(CultureInfo.InvariantCulture));
            Assert.Equal(frenchCurrentCulture, germanCurrentCulture);
            Assert.True(frenchCurrentCulture.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Value 1,234.50")) >= 0);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void NoneLocationVectorMatchesExactly()
    {
        Assert.Equal("00000000000000046e6f6e65", Convert.ToHexStringLower(DiagnosticCanonicalizer.CanonicalizeLocation(Location.None)));
    }

    [Fact]
    public void SourceLocationVectorMatchesExactly()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "x",
            path: "controlled-input.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        Location location = Location.Create(tree, new TextSpan(0, 1));
        Dictionary<SyntaxTree, string> controlled = new(ReferenceEqualityComparer.Instance)
        {
            [tree] = "Input.cs",
        };

        byte[] result = DiagnosticCanonicalizer.CanonicalizeLocation(location, new CanonicalPathContext(controlled));

        Assert.Equal(
            "0000000000000006736f75726365010000000000000008496e7075742e6373000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000100",
            Convert.ToHexStringLower(result));
        Assert.Equal("ad9845e844ed5f8900b1071e5d918ffd66f1391cb7329c9fc6115bde2dbf5870", Hash(result));
    }

    [Fact]
    public void MappedPathStatesArePairwiseDistinct()
    {
        UnmappedPathValue controlled = UnmappedPathValue.Controlled("Input.cs");
        CanonicalSourceLocation unmappedModel = CreateModel(controlled, new MappedPathValue.Unmapped());
        CanonicalSourceLocation mappedEmptyModel = CreateModel(
            controlled,
            new MappedPathValue.Mapped(MappedPathPayload.Empty));
        CanonicalSourceLocation mappedNonEmptyModel = CreateModel(
            controlled,
            new MappedPathValue.Mapped(MappedPathPayload.External(new string('0', 64))));

        byte[] unmapped = DiagnosticCanonicalizer.CanonicalizeSourceLocation(unmappedModel);
        byte[] mappedEmpty = DiagnosticCanonicalizer.CanonicalizeSourceLocation(mappedEmptyModel);
        byte[] mappedNonEmpty = DiagnosticCanonicalizer.CanonicalizeSourceLocation(mappedNonEmptyModel);

        Assert.NotEqual(unmapped, mappedEmpty);
        Assert.NotEqual(unmapped, mappedNonEmpty);
        Assert.NotEqual(mappedEmpty, mappedNonEmpty);
        Assert.IsType<MappedPathValue.Unmapped>(unmappedModel.MappedPath);
        Assert.Equal(CanonicalPathKind.Empty, Assert.IsType<MappedPathValue.Mapped>(mappedEmptyModel.MappedPath).Path.Kind);
        Assert.Equal(CanonicalPathKind.External, Assert.IsType<MappedPathValue.Mapped>(mappedNonEmptyModel.MappedPath).Path.Kind);

        Assert.Equal("ad9845e844ed5f8900b1071e5d918ffd66f1391cb7329c9fc6115bde2dbf5870", Hash(unmapped));
        Assert.Equal("b044182f465972d2341d849d9a5162f25993489aabd94d26b034aba50ddbd278", Hash(mappedEmpty));
        Assert.Equal("356c04407696fcd6a62d459d14743fbd1e60bd64a0e1dfb7f2b76b31c540bd8a", Hash(mappedNonEmpty));
    }

    [Fact]
    public void RoslynMappedEmptyPathRemainsMapped()
    {
        const string source = "#line 1 \"\"\nclass C { }";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source,
            path: "controlled-input.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        SyntaxToken token = tree.GetRoot(TestContext.Current.CancellationToken)
            .DescendantTokens()
            .First(candidate => candidate.IsKind(SyntaxKind.ClassKeyword));
        Location location = token.GetLocation();

        Assert.True(location.GetMappedLineSpan().HasMappedPath);
        Assert.Equal(string.Empty, location.GetMappedLineSpan().Path);

        Dictionary<SyntaxTree, string> controlled = new(ReferenceEqualityComparer.Instance)
        {
            [tree] = "Input.cs",
        };
        CanonicalSourceLocation model = DiagnosticCanonicalizer.CreateSourceLocation(location, new CanonicalPathContext(controlled));
        Assert.Equal(CanonicalPathKind.Empty, Assert.IsType<MappedPathValue.Mapped>(model.MappedPath).Path.Kind);
    }

    [Fact]
    public void PathIdentityKindsPreventSentinelCollisions()
    {
        SyntaxTree controlledTree = CSharpSyntaxTree.ParseText(
            "x",
            path: "same.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        SyntaxTree generatedTree = CSharpSyntaxTree.ParseText(
            "x",
            path: "same.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        Dictionary<SyntaxTree, string> controlled = new(ReferenceEqualityComparer.Instance)
        {
            [controlledTree] = "generated:A.g.cs",
        };
        Dictionary<SyntaxTree, string> generated = new(ReferenceEqualityComparer.Instance)
        {
            [generatedTree] = "A.g.cs",
        };
        CanonicalPathContext context = new(controlled, generated);

        byte[] controlledBytes = DiagnosticCanonicalizer.CanonicalizeLocation(
            Location.Create(controlledTree, new TextSpan(0, 1)), context);
        byte[] generatedBytes = DiagnosticCanonicalizer.CanonicalizeLocation(
            Location.Create(generatedTree, new TextSpan(0, 1)), context);

        Assert.NotEqual(controlledBytes, generatedBytes);
    }

    [Fact]
    public void PathIdentityDiscriminatorsArePairwiseDistinct()
    {
        byte[] controlled = DiagnosticCanonicalizer.CanonicalizeSourceLocation(
            CreateModel(UnmappedPathValue.Controlled("generated:same"), new MappedPathValue.Unmapped()));
        byte[] generated = DiagnosticCanonicalizer.CanonicalizeSourceLocation(
            CreateModel(UnmappedPathValue.Generated("same"), new MappedPathValue.Unmapped()));
        byte[] external = DiagnosticCanonicalizer.CanonicalizeSourceLocation(
            CreateModel(UnmappedPathValue.External(new string('0', 64)), new MappedPathValue.Unmapped()));

        Assert.NotEqual(controlled, generated);
        Assert.NotEqual(controlled, external);
        Assert.NotEqual(generated, external);
    }

    [Fact]
    public void LineVisibilityStatesArePairwiseDistinct()
    {
        CanonicalSourceLocation visibleModel = CreateModel(
            UnmappedPathValue.Controlled("Input.cs"),
            new MappedPathValue.Unmapped(),
            CanonicalLineVisibility.Visible);
        CanonicalSourceLocation hiddenModel = CreateModel(
            UnmappedPathValue.Controlled("Input.cs"),
            new MappedPathValue.Unmapped(),
            CanonicalLineVisibility.Hidden);
        CanonicalSourceLocation beforeFirstModel = CreateModel(
            UnmappedPathValue.Controlled("Input.cs"),
            new MappedPathValue.Unmapped(),
            CanonicalLineVisibility.BeforeFirstLineDirective);

        byte[] visible = DiagnosticCanonicalizer.CanonicalizeSourceLocation(visibleModel);
        byte[] hidden = DiagnosticCanonicalizer.CanonicalizeSourceLocation(hiddenModel);
        byte[] beforeFirst = DiagnosticCanonicalizer.CanonicalizeSourceLocation(beforeFirstModel);

        Assert.NotEqual(visible, hidden);
        Assert.NotEqual(visible, beforeFirst);
        Assert.NotEqual(hidden, beforeFirst);
    }

    [Fact]
    public void NullableTagAndPropertyValuesRemainDistinctFromEmpty()
    {
        DiagnosticDescriptor nullTagDescriptor = new(
            "SGA001", "title", "message", "Test", DiagnosticSeverity.Warning, true, customTags: [null!]);
        DiagnosticDescriptor emptyTagDescriptor = new(
            "SGA001", "title", "message", "Test", DiagnosticSeverity.Warning, true, customTags: [string.Empty]);
        Assert.NotEqual(
            DiagnosticCanonicalizer.Canonicalize([Diagnostic.Create(nullTagDescriptor, Location.None)]).Bytes,
            DiagnosticCanonicalizer.Canonicalize([Diagnostic.Create(emptyTagDescriptor, Location.None)]).Bytes);

        ImmutableDictionary<string, string?> nullProperty = ImmutableDictionary<string, string?>.Empty.Add("k", null);
        ImmutableDictionary<string, string?> emptyProperty = ImmutableDictionary<string, string?>.Empty.Add("k", string.Empty);
        DiagnosticDescriptor descriptor = new("SGA002", "title", "message", "Test", DiagnosticSeverity.Warning, true);
        Assert.NotEqual(
            DiagnosticCanonicalizer.Canonicalize([Diagnostic.Create(descriptor, Location.None, properties: nullProperty)]).Bytes,
            DiagnosticCanonicalizer.Canonicalize([Diagnostic.Create(descriptor, Location.None, properties: emptyProperty)]).Bytes);
    }

    [Fact]
    public void TagOrderIsIgnoredButDuplicatesArePreserved()
    {
        DiagnosticDescriptor first = new(
            "SGA001", "title", "message", "Test", DiagnosticSeverity.Warning, true, customTags: ["b", null!, "a", "a"]);
        DiagnosticDescriptor reversed = new(
            "SGA001", "title", "message", "Test", DiagnosticSeverity.Warning, true, customTags: ["a", "a", null!, "b"]);
        DiagnosticDescriptor deduplicated = new(
            "SGA001", "title", "message", "Test", DiagnosticSeverity.Warning, true, customTags: ["a", null!, "b"]);

        byte[] firstBytes = DiagnosticCanonicalizer.Canonicalize([Diagnostic.Create(first, Location.None)]).Bytes;
        byte[] reversedBytes = DiagnosticCanonicalizer.Canonicalize([Diagnostic.Create(reversed, Location.None)]).Bytes;
        byte[] deduplicatedBytes = DiagnosticCanonicalizer.Canonicalize([Diagnostic.Create(deduplicated, Location.None)]).Bytes;

        Assert.Equal(firstBytes, reversedBytes);
        Assert.NotEqual(firstBytes, deduplicatedBytes);
    }

    [Fact]
    public void MalformedLocationsBecomeCanonicalizationFailures()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "x",
            path: "Input.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        Location outOfTree = Location.Create(tree, new TextSpan(0, 99));
        Assert.Throws<CanonicalizationException>(() => DiagnosticCanonicalizer.CanonicalizeLocation(outOfTree));

        DiagnosticDescriptor descriptor = new("SGA001", "title", "message", "Test", DiagnosticSeverity.Warning, true);
        Diagnostic nullAdditional = Diagnostic.Create(
            descriptor,
            Location.None,
            additionalLocations: new Location[] { null! });
        Assert.Throws<CanonicalizationException>(() => DiagnosticCanonicalizer.Canonicalize([nullAdditional]));
    }

    [Fact]
    public void ATreeCannotHaveTwoPathIdentityKinds()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "x",
            path: "Input.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        Dictionary<SyntaxTree, string> controlled = new(ReferenceEqualityComparer.Instance) { [tree] = "Input.cs" };
        Dictionary<SyntaxTree, string> generated = new(ReferenceEqualityComparer.Instance) { [tree] = "A.g.cs" };

        Assert.Throws<CanonicalizationException>(() => new CanonicalPathContext(controlled, generated));
    }

    [Fact]
    public void UnsupportedPublicLocationKindDoesNotCanonicalize()
    {
        Location external = Location.Create(
            "external.cs",
            new TextSpan(0, 1),
            new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 1)));

        Assert.Equal(LocationKind.ExternalFile, external.Kind);
        Assert.Throws<UnsupportedLocationKindException>(() => DiagnosticCanonicalizer.CanonicalizeLocation(external));
    }

    [Fact]
    public void UnicodeAndLiteralBomRemainDistinct()
    {
        string composed = GeneratedSourceCanonicalizer.Canonicalize([new("A.g.cs", "\u00e9")]).Sha256;
        string decomposed = GeneratedSourceCanonicalizer.Canonicalize([new("A.g.cs", "e\u0301")]).Sha256;
        string literalBom = GeneratedSourceCanonicalizer.Canonicalize([new("A.g.cs", "\ufeff")]).Sha256;

        Assert.NotEqual(composed, decomposed);
        Assert.NotEqual(composed, literalBom);
        Assert.NotEqual(decomposed, literalBom);
    }

    [Fact]
    public void EncodingAndChecksumObservationsDoNotAffectEquality()
    {
        CanonicalSourceSet utf8 = GeneratedSourceCanonicalizer.Canonicalize(
            [new("A.g.cs", "same", Encoding.UTF8.WebName, 3, "aaaa")]);
        CanonicalSourceSet utf16 = GeneratedSourceCanonicalizer.Canonicalize(
            [new("A.g.cs", "same", Encoding.Unicode.WebName, 2, "bbbb")]);

        Assert.Equal(utf8.Bytes, utf16.Bytes);
        Assert.Equal(utf8.Sha256, utf16.Sha256);
    }

    [Fact]
    public void SourceEmissionOrderIsIgnoredAndDuplicateHintsFail()
    {
        GeneratedSourceValue first = new("A.g.cs", "a");
        GeneratedSourceValue second = new("B.g.cs", "b");

        Assert.Equal(
            GeneratedSourceCanonicalizer.Canonicalize([first, second]).Bytes,
            GeneratedSourceCanonicalizer.Canonicalize([second, first]).Bytes);
        Assert.Throws<CanonicalizationException>(() => GeneratedSourceCanonicalizer.Canonicalize([first, first]));
    }

    [Fact]
    public void SourceDiffUsesHintIdentityAndExactContent()
    {
        CanonicalSourceSet before = GeneratedSourceCanonicalizer.Canonicalize(
            [new("A.g.cs", "old"), new("Removed.g.cs", "same")]);
        CanonicalSourceSet after = GeneratedSourceCanonicalizer.Canonicalize(
            [new("A.g.cs", "new"), new("Added.g.cs", "same")]);

        Assert.Equal(
            [
                new GeneratedSourceChange("A.g.cs", GeneratedSourceChangeKind.Modified),
                new GeneratedSourceChange("Added.g.cs", GeneratedSourceChangeKind.Added),
                new GeneratedSourceChange("Removed.g.cs", GeneratedSourceChangeKind.Removed),
            ],
            GeneratedSourceDiff.Compare(before, after));
    }

    [Fact]
    public void DiagnosticOrderIsIgnoredAndAbsoluteExternalPathIsRedacted()
    {
        DiagnosticDescriptor firstDescriptor = new("SGA001", "a", "a", "Test", DiagnosticSeverity.Info, true);
        DiagnosticDescriptor secondDescriptor = new("SGA002", "b", "b", "Test", DiagnosticSeverity.Warning, true);
        Diagnostic first = Diagnostic.Create(firstDescriptor, Location.None);
        Diagnostic second = Diagnostic.Create(secondDescriptor, Location.None);

        Assert.Equal(
            DiagnosticCanonicalizer.Canonicalize([first, second]).Bytes,
            DiagnosticCanonicalizer.Canonicalize([second, first]).Bytes);

        string absolute = Path.GetFullPath("external.cs");
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "x",
            path: absolute,
            cancellationToken: TestContext.Current.CancellationToken);
        byte[] location = DiagnosticCanonicalizer.CanonicalizeLocation(Location.Create(tree, new TextSpan(0, 1)));
        string hex = Convert.ToHexStringLower(location);
        Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(absolute)), hex, StringComparison.Ordinal);
    }

    [Fact]
    public void PathFactoriesRejectMalformedTokensAndNormalizeControlledSeparators()
    {
        Assert.Equal("folder/Input.cs", UnmappedPathValue.Controlled("folder\\Input.cs").Token);
        Assert.Throws<ArgumentException>(() => UnmappedPathValue.Controlled(string.Empty));
        Assert.Throws<ArgumentException>(() => UnmappedPathValue.Controlled("C:/secret.cs"));
        Assert.Throws<ArgumentException>(() => UnmappedPathValue.Controlled("/secret.cs"));
        Assert.Throws<ArgumentException>(() => UnmappedPathValue.Controlled("folder/../secret.cs"));
        Assert.Throws<ArgumentException>(() => UnmappedPathValue.Controlled("folder//secret.cs"));
        Assert.Throws<ArgumentException>(() => UnmappedPathValue.Generated(string.Empty));
        Assert.Throws<ArgumentException>(() => UnmappedPathValue.External("same"));
        Assert.Throws<ArgumentException>(() => UnmappedPathValue.External(new string('A', 64)));
        Assert.Throws<ArgumentException>(() => MappedPathPayload.External("same"));
    }

    private static CanonicalSourceLocation CreateModel(
        UnmappedPathValue unmappedPath,
        MappedPathValue mappedPath,
        CanonicalLineVisibility lineVisibility = CanonicalLineVisibility.Visible) => new(
        unmappedPath,
        0,
        1,
        mappedPath,
        0,
        0,
        0,
        1,
        lineVisibility);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}
