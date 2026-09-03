using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SourceGenAuditor.Fixtures;

public sealed class ConfigurableIncrementalGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor FixtureDiagnostic = new(
        "SGAFIX001",
        "Fixture diagnostic",
        "Fixture value: {0}",
        "SourceGenAuditor.Fixture",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<FixtureInput> inputs = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, cancellationToken) => CreateInput((ClassDeclarationSyntax)syntaxContext.Node, cancellationToken))
            .WithTrackingName("FixtureClass");

        IncrementalValueProvider<ImmutableArray<FixtureInput>> collected = inputs
            .Collect()
            .WithTrackingName("FixtureCollection");

        IncrementalValueProvider<FixtureModel> model = collected
            .Select(static (values, _) => CreateModel(values))
            .WithTrackingName("FixtureModel");

        context.RegisterSourceOutput(model, static (productionContext, value) => Emit(productionContext, value));
    }

    private static FixtureInput CreateInput(ClassDeclarationSyntax declaration, CancellationToken cancellationToken)
    {
        string source = declaration.SyntaxTree.GetText(cancellationToken).ToString();
        return new FixtureInput(declaration.Identifier.ValueText, DetectMode(source), DetectDiagnosticValue(source));
    }

    private static FixtureModel CreateModel(ImmutableArray<FixtureInput> values)
    {
        FixtureInput[] ordered = values.OrderBy(value => value.ClassName, StringComparer.Ordinal).ToArray();
        string names = string.Join(",", ordered.Select(value => value.ClassName));
        string mode = ordered.Select(value => value.Mode).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        string? diagnosticValue = ordered.Select(value => value.DiagnosticValue).FirstOrDefault(value => value is not null);
        return new FixtureModel(names, mode, diagnosticValue);
    }

    private static void Emit(SourceProductionContext context, FixtureModel value)
    {
        if (value.Mode == "stdout")
        {
            Console.Out.Write("fixture-stdout");
            Console.Error.Write("fixture-stderr");
        }
        else if (value.Mode == "stdout-large")
        {
            byte[] bytes = Enumerable.Repeat((byte)'x', (1024 * 1024) + 4096).ToArray();
            Console.OpenStandardOutput().Write(bytes);
        }
        else if (value.Mode == "hang")
        {
            while (true)
            {
                Thread.SpinWait(100_000);
            }
        }
        else if (value.Mode == "cooperative-cancel")
        {
            while (true)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                Thread.SpinWait(100_000);
            }
        }
        else if (value.Mode == "evidence-overflow")
        {
            context.AddSource("LargeFixture.g.cs", SourceText.From(new string('x', 2_285_470), Encoding.UTF8));
        }
        else if (value.Mode == "crash")
        {
            Environment.FailFast("fixture crash requested");
        }
        else if (value.Mode == "exit")
        {
            Environment.Exit(77);
        }
        else if (value.Mode == "exit-after-completed")
        {
            new Thread(static () =>
            {
                Thread.Sleep(500);
                Environment.Exit(77);
            })
            {
                IsBackground = false,
                Name = "SourceGenAuditor fixture delayed exit",
            }.Start();
        }
        else if (value.Mode == "throw-before")
        {
            throw new InvalidOperationException("fixture throw before source");
        }
        else if (value.Mode == "linger")
        {
            new Thread(static () => Thread.Sleep(Timeout.Infinite))
            {
                IsBackground = false,
                Name = "SourceGenAuditor fixture lingering thread",
            }.Start();
        }
        else if (value.Mode == "spawn-descendant")
        {
            ProcessStartInfo start = new()
            {
                FileName = OperatingSystem.IsWindows() ? "ping" : "sleep",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (OperatingSystem.IsWindows())
            {
                start.ArgumentList.Add("127.0.0.1");
                start.ArgumentList.Add("-n");
                start.ArgumentList.Add("11");
            }
            else
            {
                start.ArgumentList.Add("10");
            }

            _ = Process.Start(start) ?? throw new InvalidOperationException("The pipe-holder descendant did not start.");
        }

        string escapedNames = value.ClassNames.Replace("\"", "\"\"", StringComparison.Ordinal);
        context.AddSource(
            "Fixture.g.cs",
            SourceText.From(
                $"namespace SourceGenAuditor.Generated; internal static class FixtureOutput {{ internal const string Names = @\"{escapedNames}\"; }}",
                Encoding.UTF8));

        if (value.DiagnosticValue is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(FixtureDiagnostic, Location.None, value.DiagnosticValue));
        }

        if (value.Mode == "unsupported-location")
        {
            Location location = Location.Create(
                "C:/private/unsupported.cs",
                new TextSpan(0, 0),
                new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0)));
            context.ReportDiagnostic(Diagnostic.Create(FixtureDiagnostic, location, "unsupported"));
        }

        if (value.Mode == "throw-after")
        {
            throw new InvalidOperationException("fixture throw after source");
        }
    }

    private static string DetectMode(string source)
    {
        foreach ((string Marker, string Mode) item in new[]
        {
            ("SGA_MODE_STDOUT_LARGE", "stdout-large"),
            ("SGA_MODE_STDOUT", "stdout"),
            ("SGA_MODE_HANG", "hang"),
            ("SGA_MODE_COOPERATIVE_CANCEL", "cooperative-cancel"),
            ("SGA_MODE_EVIDENCE_OVERFLOW", "evidence-overflow"),
            ("SGA_MODE_CRASH", "crash"),
            ("SGA_MODE_EXIT_AFTER_COMPLETED", "exit-after-completed"),
            ("SGA_MODE_EXIT", "exit"),
            ("SGA_MODE_THROW_BEFORE", "throw-before"),
            ("SGA_MODE_THROW_AFTER", "throw-after"),
            ("SGA_MODE_LINGER", "linger"),
            ("SGA_MODE_SPAWN_DESCENDANT", "spawn-descendant"),
            ("SGA_MODE_UNSUPPORTED_LOCATION", "unsupported-location"),
        })
        {
            if (source.Contains(item.Marker, StringComparison.Ordinal))
            {
                return item.Mode;
            }
        }

        return string.Empty;
    }

    private static string? DetectDiagnosticValue(string source)
    {
        const string marker = "SGA_DIAGNOSTIC:";
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        int end = source.IndexOfAny(['\r', '\n'], start);
        return (end < 0 ? source[start..] : source[start..end]).Trim();
    }

    private sealed record FixtureInput(string ClassName, string Mode, string? DiagnosticValue);

    private sealed record FixtureModel(string ClassNames, string Mode, string? DiagnosticValue);
}
