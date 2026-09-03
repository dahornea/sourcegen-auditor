using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace SourceGenAuditor.Tests.Canonicalization;

public sealed class RoslynObservableNormalizationTests
{
    [Fact]
    public void DescriptorNormalizesOnlyDocumentedNullableConstructorValues()
    {
        DiagnosticDescriptor descriptor = new(
            "SGA001",
            "title",
            "message",
            "Category",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: null,
            helpLinkUri: null,
            customTags: null!);

        Assert.Equal(string.Empty, descriptor.Description.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, descriptor.HelpLinkUri);
        Assert.Empty(descriptor.CustomTags);
        Assert.Equal("Category", descriptor.Category);
        Assert.Equal("SGA001", descriptor.Id);
    }

    [Fact]
    public void DescriptorStringOverloadNormalizesNullTitleAndMessageFormat()
    {
        DiagnosticDescriptor nullTitle = CreateDescriptor(
            id: "SGA001", title: null!, message: "message", category: "Category");
        DiagnosticDescriptor nullMessage = CreateDescriptor(
            id: "SGA001", title: "title", message: null!, category: "Category");

        Assert.Equal(string.Empty, nullTitle.Title.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, nullMessage.MessageFormat.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(
            string.Empty,
            Diagnostic.Create(nullMessage, Location.None).GetMessage(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DescriptorRejectsInvalidIdAndNullCategory()
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateDescriptor(id: null!, title: "title", message: "message", category: "Category"));
        Assert.ThrowsAny<ArgumentException>(() => CreateDescriptor(id: string.Empty, title: "title", message: "message", category: "Category"));
        Assert.ThrowsAny<ArgumentException>(() => CreateDescriptor(id: " ", title: "title", message: "message", category: "Category"));
        Assert.ThrowsAny<ArgumentException>(() => CreateDescriptor(id: "SGA001", title: "title", message: "message", category: null!));

        Assert.Equal(string.Empty, CreateDescriptor("SGA001", "title", "message", string.Empty).Category);
        Assert.Equal(" SGA001 ", CreateDescriptor(" SGA001 ", "title", "message", "Category").Id);
    }

    [Fact]
    public void DescriptorLocalizableStringOverloadRejectsNullTitleAndMessageFormat()
    {
        DiagnosticDescriptor valid = CreateDescriptor("SGA001", "title", "message", "Category");

        Assert.Throws<ArgumentNullException>(() => new DiagnosticDescriptor(
            "SGA001",
            (LocalizableString)null!,
            valid.MessageFormat,
            "Category",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true));
        Assert.Throws<ArgumentNullException>(() => new DiagnosticDescriptor(
            "SGA001",
            valid.Title,
            (LocalizableString)null!,
            "Category",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true));
    }

    [Fact]
    public void DiagnosticNormalizesNullContainersButPreservesPropertyNullAndEmptyValues()
    {
        DiagnosticDescriptor descriptor = CreateDescriptor("SGA001", "title", "message", "Category");
        Diagnostic normalized = Diagnostic.Create(descriptor, location: null, additionalLocations: null, properties: null);

        Assert.Same(Location.None, normalized.Location);
        Assert.Empty(normalized.AdditionalLocations);
        Assert.Empty(normalized.Properties);

        ImmutableDictionary<string, string?> properties = ImmutableDictionary<string, string?>.Empty
            .Add("empty", string.Empty)
            .Add("null", null);
        Diagnostic preserved = Diagnostic.Create(descriptor, Location.None, additionalLocations: null, properties: properties);
        Assert.Equal(string.Empty, preserved.Properties["empty"]);
        Assert.Null(preserved.Properties["null"]);
    }

    [Fact]
    public void SyntaxTreeNormalizesNullPathToObservableEmptyPath()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "class C { }",
            path: null!,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, tree.FilePath);
        Assert.Equal(string.Empty, tree.GetLineSpan(default, TestContext.Current.CancellationToken).Path);
    }

    [Fact]
    public void ExternalLocationRejectsNullPathAndPreservesEmptyPath()
    {
        TextSpan textSpan = new(0, 1);
        LinePositionSpan lineSpan = new(new LinePosition(0, 0), new LinePosition(0, 1));

        Assert.Throws<ArgumentNullException>(() => Location.Create(null!, textSpan, lineSpan));
        Location location = Location.Create(string.Empty, textSpan, lineSpan);
        Assert.Equal(string.Empty, location.GetLineSpan().Path);
    }

    [Fact]
    public void RoslynExposesAllThreeLineVisibilityStates()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SyntaxTree visibleTree = CSharpSyntaxTree.ParseText("class Visible { }", cancellationToken: cancellationToken);
        SyntaxTree hiddenTree = CSharpSyntaxTree.ParseText("#line hidden\nclass Hidden { }", cancellationToken: cancellationToken);
        SyntaxTree beforeFirstTree = CSharpSyntaxTree.ParseText(
            "class Before { }\n#line 1 \"mapped.cs\"\nclass After { }",
            cancellationToken: cancellationToken);

        int visiblePosition = FindClassKeyword(visibleTree, cancellationToken).SpanStart;
        int hiddenPosition = FindClassKeyword(hiddenTree, cancellationToken).SpanStart;
        int beforeFirstPosition = FindClassKeyword(beforeFirstTree, cancellationToken).SpanStart;

        Assert.Equal(LineVisibility.Visible, visibleTree.GetLineVisibility(visiblePosition, cancellationToken));
        Assert.Equal(LineVisibility.Hidden, hiddenTree.GetLineVisibility(hiddenPosition, cancellationToken));
        Assert.Equal(LineVisibility.BeforeFirstLineDirective, beforeFirstTree.GetLineVisibility(beforeFirstPosition, cancellationToken));
    }

    private static DiagnosticDescriptor CreateDescriptor(string id, string title, string message, string category) => new(
        id,
        title,
        message,
        category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static SyntaxToken FindClassKeyword(SyntaxTree tree, CancellationToken cancellationToken) => tree
        .GetRoot(cancellationToken)
        .DescendantTokens()
        .First(token => token.IsKind(SyntaxKind.ClassKeyword));
}
