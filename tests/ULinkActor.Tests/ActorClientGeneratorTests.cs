using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ULinkActor.SourceGenerator.Generators;

namespace ULinkActor.Tests;

public sealed class ActorClientGeneratorTests
{
    [Fact]
    public void Generator_reports_invalid_actor_client_shapes()
    {
        const string source = """
            using System.Threading.Tasks;
            using ULinkActor;

            [ActorClient]
            internal interface IInternalClient
            {
                ValueTask Send();
            }

            [ActorClient]
            public interface IGenericClient<T>
            {
                ValueTask Send();
            }

            [ActorClient]
            public interface IInvalidClient
            {
                ValueTask Send(int value);

                ValueTask Send(string value);

                Task InvalidReturn();

                ValueTask Generic<T>();

                ValueTask RefArg(ref int value);
            }
            """;

        Diagnostic[] diagnostics = GetGeneratorDiagnostics(source);

        Assert.Equal(
            [
                ActorClientGenerator.NonPublicInterfaceDiagnosticId,
                ActorClientGenerator.GenericInterfaceDiagnosticId,
                ActorClientGenerator.MethodOverloadDiagnosticId,
                ActorClientGenerator.MethodOverloadDiagnosticId,
                ActorClientGenerator.UnsupportedReturnTypeDiagnosticId,
                ActorClientGenerator.GenericMethodDiagnosticId,
                ActorClientGenerator.RefParameterDiagnosticId
            ],
            diagnostics.Select(static diagnostic => diagnostic.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Generator_does_not_report_valid_actor_client()
    {
        const string source = """
            using System.Threading.Tasks;
            using ULinkActor;

            [ActorClient]
            public interface IValidClient
            {
                ValueTask Send(int value);

                ValueTask<int> Query();
            }
            """;

        Diagnostic[] diagnostics = GetGeneratorDiagnostics(source);

        Assert.Empty(diagnostics);
    }

    private static Diagnostic[] GetGeneratorDiagnostics(string source)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ActorClientGeneratorTests",
            [syntaxTree],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ActorClientGenerator());
        driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out _,
            out ImmutableArray<Diagnostic> diagnostics);

        return diagnostics
            .Where(static diagnostic => diagnostic.Id.StartsWith("ULA", StringComparison.Ordinal))
            .OrderBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences()
    {
        string trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ??
            string.Empty;

        foreach (string path in trustedPlatformAssemblies.Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return MetadataReference.CreateFromFile(path);
            }
        }

        yield return MetadataReference.CreateFromFile(typeof(ActorSystem).Assembly.Location);
    }
}
