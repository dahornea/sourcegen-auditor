using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SourceGenAuditor.Core.Scenario;
using Xunit;

namespace SourceGenAuditor.Tests.Scenario;

public sealed class ScenarioValidationTests
{
    [Fact]
    public void ApprovedScenarioLoadsWithContainedHashedInputs()
    {
        string manifest = Path.Combine(FindRepositoryRoot(), "tests", "scenarios", "relevant", "scenario.json");
        Core.Model.ScenarioDefinition scenario = ScenarioLoader.Load(manifest);

        Assert.Single(scenario.Baseline.Sources);
        Assert.Single(scenario.Baseline.References);
        Assert.Equal("Input.cs", scenario.Mutation.TargetLogicalPath);
    }

    [Fact]
    public void UnknownAndDuplicatePropertiesFailClosed()
    {
        string unknown = CopyScenario();
        JsonObject document = Read(unknown);
        document["unknown"] = true;
        Write(unknown, document);
        Assert.Throws<ScenarioValidationException>(() => ScenarioLoader.Load(unknown));

        string duplicate = CopyScenario();
        string text = File.ReadAllText(duplicate);
        text = text.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,",
            StringComparison.Ordinal);
        File.WriteAllText(duplicate, text, new UTF8Encoding(false));
        Assert.Throws<ScenarioValidationException>(() => ScenarioLoader.Load(duplicate));
    }

    [Fact]
    public void DriftEscapesAndUnsupportedEnumsFailBeforeExecution()
    {
        string drift = CopyScenario();
        JsonObject driftDocument = Read(drift);
        driftDocument["baseline"]!["sources"]![0]!["sha256"] = new string('0', 64);
        Write(drift, driftDocument);
        Assert.Throws<ScenarioValidationException>(() => ScenarioLoader.Load(drift));

        string escape = CopyScenario();
        string outside = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(escape))!, "outside.cs");
        File.WriteAllText(outside, "class Outside;", new UTF8Encoding(false));
        JsonObject escapeDocument = Read(escape);
        escapeDocument["baseline"]!["sources"]![0]!["path"] = "../outside.cs";
        Write(escape, escapeDocument);
        Assert.Throws<ScenarioValidationException>(() => ScenarioLoader.Load(escape));

        string invalidEnum = CopyScenario();
        JsonObject enumDocument = Read(invalidEnum);
        enumDocument["mutation"]!["relevance"] = "maybe";
        Write(invalidEnum, enumDocument);
        Assert.Throws<ScenarioValidationException>(() => ScenarioLoader.Load(invalidEnum));
    }

    [Fact]
    public void DuplicateLogicalPathsAndInvalidUtf8FailClosed()
    {
        string duplicate = CopyScenario();
        JsonObject document = Read(duplicate);
        JsonArray sources = document["baseline"]!["sources"]!.AsArray();
        sources.Add(sources[0]!.DeepClone());
        Write(duplicate, document);
        Assert.Throws<ScenarioValidationException>(() => ScenarioLoader.Load(duplicate));

        string invalidUtf8 = CopyScenario();
        File.WriteAllBytes(invalidUtf8, [0xff, 0xfe]);
        Assert.Throws<ScenarioValidationException>(() => ScenarioLoader.Load(invalidUtf8));
    }

    [Fact]
    public void MissingBooleanAndNullCollectionEntriesFailAsScenarioErrors()
    {
        string missingBoolean = CopyScenario();
        JsonObject missingDocument = Read(missingBoolean);
        missingDocument["baseline"]!["compilationOptions"]!.AsObject().Remove("allowUnsafe");
        Write(missingBoolean, missingDocument);
        Assert.Throws<ScenarioValidationException>(() => ScenarioLoader.Load(missingBoolean));

        string nullSource = CopyScenario();
        JsonObject sourceDocument = Read(nullSource);
        sourceDocument["baseline"]!["sources"]!.AsArray()[0] = null;
        Write(nullSource, sourceDocument);
        Assert.Throws<ScenarioValidationException>(() => ScenarioLoader.Load(nullSource));

        string nullReference = CopyScenario();
        JsonObject referenceDocument = Read(nullReference);
        referenceDocument["baseline"]!["references"]!.AsArray()[0] = null;
        Write(nullReference, referenceDocument);
        Assert.Throws<ScenarioValidationException>(() => ScenarioLoader.Load(nullReference));
    }

    private static string CopyScenario()
    {
        string repository = FindRepositoryRoot();
        string source = Path.Combine(repository, "tests", "scenarios", "relevant");
        string target = Path.Combine(repository, "artifacts", "scenario-validation", Guid.NewGuid().ToString("N"));
        CopyDirectory(source, target);
        return Path.Combine(target, "scenario.json");
    }

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static void Write(string path, JsonObject document)
        => File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destinationPath = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SourceGenAuditor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
