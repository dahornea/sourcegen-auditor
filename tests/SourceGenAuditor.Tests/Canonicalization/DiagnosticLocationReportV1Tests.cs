using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceGenAuditor.Core.Canonicalization;
using SourceGenAuditor.Core.Reporting;
using Xunit;

namespace SourceGenAuditor.Tests.Canonicalization;

public sealed class DiagnosticLocationReportV1Tests
{
    [Fact]
    public void NoneLocationJsonIsTheExactClosedVariant()
    {
        Assert.Equal(
            "{\"kind\":\"None\"}",
            Encoding.UTF8.GetString(ReportV1Json.SerializeLocation(LocationV1.None)));
    }

    [Fact]
    public void MappedPathJsonStatesArePairwiseDistinctAndSchemaExact()
    {
        byte[] unmapped = Serialize(new MappedPathValue.Unmapped());
        byte[] mappedEmpty = Serialize(new MappedPathValue.Mapped(MappedPathPayload.Empty));
        byte[] mappedNonEmpty = Serialize(
            new MappedPathValue.Mapped(MappedPathPayload.External(new string('0', 64))));

        Assert.Equal("{\"hasMappedPath\":false}", GetMappedPathJson(unmapped));
        Assert.Equal(
            "{\"hasMappedPath\":true,\"value\":{\"kind\":\"Empty\",\"token\":\"\"}}",
            GetMappedPathJson(mappedEmpty));
        Assert.Equal(
            $"{{\"hasMappedPath\":true,\"value\":{{\"kind\":\"External\",\"token\":\"external:{new string('0', 64)}\"}}}}",
            GetMappedPathJson(mappedNonEmpty));

        Assert.NotEqual(unmapped, mappedEmpty);
        Assert.NotEqual(unmapped, mappedNonEmpty);
        Assert.NotEqual(mappedEmpty, mappedNonEmpty);
    }

    [Fact]
    public void ExternalMappedPathIsRedactedBeforeReportSerialization()
    {
        string rawPath = Path.GetFullPath("mapped-target.cs");
        CanonicalPathContext context = new();
        MappedPathPayload payload = context.ResolveMapped(rawPath);
        byte[] json = Serialize(new MappedPathValue.Mapped(payload));
        string text = Encoding.UTF8.GetString(json);
        string expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawPath)));

        Assert.DoesNotContain(rawPath, text, StringComparison.Ordinal);
        Assert.Contains($"external:{expectedHash}", text, StringComparison.Ordinal);
        Assert.Equal("External", payload.Kind.ToString());
    }

    [Fact]
    public void ReportLocationCarriesTheClosedMappedUnion()
    {
        CanonicalSourceLocation location = CreateLocation(
            new MappedPathValue.Mapped(MappedPathPayload.Empty));

        byte[] json = ReportV1Json.SerializeLocation(LocationV1.FromCanonical(location));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal("SourceFile", root.GetProperty("kind").GetString());
        Assert.Equal("Controlled", root.GetProperty("unmappedPath").GetProperty("kind").GetString());
        Assert.True(root.GetProperty("mappedPath").GetProperty("hasMappedPath").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("mappedPath").GetProperty("value").GetProperty("token").GetString());
        Assert.Equal("Visible", root.GetProperty("lineVisibility").GetString());
        Assert.Equal(10, root.EnumerateObject().Count());
    }

    private static byte[] Serialize(MappedPathValue mappedPath)
        => ReportV1Json.SerializeLocation(LocationV1.FromCanonical(CreateLocation(mappedPath)));

    private static CanonicalSourceLocation CreateLocation(MappedPathValue mappedPath) => new(
        UnmappedPathValue.Controlled("Input.cs"),
        0,
        1,
        mappedPath,
        0,
        0,
        0,
        1,
        CanonicalLineVisibility.Visible);

    private static string GetMappedPathJson(byte[] locationJson)
    {
        using JsonDocument document = JsonDocument.Parse(locationJson);
        return document.RootElement.GetProperty("mappedPath").GetRawText();
    }
}
