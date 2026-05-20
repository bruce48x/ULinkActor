using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ULinkActor.SourceGenerator;

[Generator]
public sealed class ActorClientGenerator : IIncrementalGenerator
{
    public const string NonPublicInterfaceDiagnosticId = "ULA101";
    public const string GenericInterfaceDiagnosticId = "ULA102";
    public const string MethodOverloadDiagnosticId = "ULA103";
    public const string UnsupportedReturnTypeDiagnosticId = "ULA104";
    public const string GenericMethodDiagnosticId = "ULA105";
    public const string RefParameterDiagnosticId = "ULA106";

    private static readonly DiagnosticDescriptor NonPublicInterfaceRule = new(
        NonPublicInterfaceDiagnosticId,
        "Actor client interfaces must be public",
        "Actor client interface '{0}' must be public",
        "ULinkActor.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GenericInterfaceRule = new(
        GenericInterfaceDiagnosticId,
        "Generic actor client interfaces are not supported",
        "Actor client interface '{0}' must not be generic",
        "ULinkActor.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MethodOverloadRule = new(
        MethodOverloadDiagnosticId,
        "Actor client method overloads are not supported",
        "Actor client method '{0}' is overloaded; use unique method names",
        "ULinkActor.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedReturnTypeRule = new(
        UnsupportedReturnTypeDiagnosticId,
        "Actor client methods must return ValueTask or ValueTask<T>",
        "Actor client method '{0}' must return ValueTask or ValueTask<T>",
        "ULinkActor.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GenericMethodRule = new(
        GenericMethodDiagnosticId,
        "Generic actor client methods are not supported",
        "Actor client method '{0}' must not be generic",
        "ULinkActor.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RefParameterRule = new(
        RefParameterDiagnosticId,
        "Actor client by-reference parameters are not supported",
        "Actor client method '{0}' parameter '{1}' must not use ref, out, or in",
        "ULinkActor.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<INamedTypeSymbol?> candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InterfaceDeclarationSyntax { AttributeLists.Count: > 0 },
                static (ctx, _) => GetActorClientInterface(ctx))
            .Where(static symbol => symbol is not null);

        IncrementalValueProvider<ImmutableArray<INamedTypeSymbol?>> clientTypes = candidates.Collect();

        context.RegisterSourceOutput(clientTypes, static (ctx, clients) =>
        {
            INamedTypeSymbol[] uniqueClients = clients
                .Where(static client => client is not null)
                .Select(static client => client!)
                .GroupBy(
                    static client => client.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    StringComparer.Ordinal)
                .Select(static group => group.First())
                .OrderBy(static client => client.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
                .ToArray();

            List<INamedTypeSymbol> validClients = new(uniqueClients.Length);

            foreach (INamedTypeSymbol client in uniqueClients)
            {
                if (ValidateClient(ctx, client))
                {
                    validClients.Add(client);
                }
            }

            if (validClients.Count == 0)
            {
                return;
            }

            string source = GenerateSource(validClients);
            ctx.AddSource("ULinkActorClientExtensions.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }

    private static INamedTypeSymbol? GetActorClientInterface(GeneratorSyntaxContext context)
    {
        InterfaceDeclarationSyntax declaration = (InterfaceDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type)
        {
            return null;
        }

        return HasActorClientAttribute(type) ? type : null;
    }

    private static bool HasActorClientAttribute(INamedTypeSymbol type)
    {
        foreach (AttributeData attribute in type.GetAttributes())
        {
            INamedTypeSymbol? attributeType = attribute.AttributeClass;

            if (attributeType is
                {
                    Name: "ActorClientAttribute",
                    ContainingNamespace:
                    {
                        Name: "ULinkActor",
                        ContainingNamespace.IsGlobalNamespace: true
                    }
                })
            {
                return true;
            }
        }

        return false;
    }

    private static bool ValidateClient(SourceProductionContext context, INamedTypeSymbol clientType)
    {
        bool valid = true;

        if (clientType.DeclaredAccessibility != Accessibility.Public)
        {
            ReportDiagnostic(context, NonPublicInterfaceRule, clientType, clientType.Name);
            valid = false;
        }

        if (clientType.IsGenericType)
        {
            ReportDiagnostic(context, GenericInterfaceRule, clientType, clientType.Name);
            valid = false;
        }

        IMethodSymbol[] methods = GetClientMethods(clientType);

        foreach (IGrouping<string, IMethodSymbol> overloadGroup in methods.GroupBy(static method => method.Name, StringComparer.Ordinal))
        {
            if (overloadGroup.Count() <= 1)
            {
                continue;
            }

            foreach (IMethodSymbol method in overloadGroup)
            {
                ReportDiagnostic(context, MethodOverloadRule, method, method.Name);
                valid = false;
            }
        }

        foreach (IMethodSymbol method in methods)
        {
            if (method.IsGenericMethod)
            {
                ReportDiagnostic(context, GenericMethodRule, method, method.Name);
                valid = false;
            }

            if (method.ReturnType is not INamedTypeSymbol returnType ||
                (!IsValueTask(returnType) && !IsValueTaskOfT(returnType)))
            {
                ReportDiagnostic(context, UnsupportedReturnTypeRule, method, method.Name);
                valid = false;
            }

            foreach (IParameterSymbol parameter in method.Parameters)
            {
                if (parameter.RefKind != RefKind.None)
                {
                    ReportDiagnostic(context, RefParameterRule, parameter, method.Name, parameter.Name);
                    valid = false;
                }
            }
        }

        return valid;
    }

    private static void ReportDiagnostic(
        SourceProductionContext context,
        DiagnosticDescriptor rule,
        ISymbol symbol,
        params object?[] messageArgs)
    {
        context.ReportDiagnostic(Diagnostic.Create(
            rule,
            symbol.Locations.FirstOrDefault(static location => location.IsInSource),
            messageArgs));
    }

    private static string GenerateSource(IReadOnlyList<INamedTypeSymbol> clientTypes)
    {
        StringBuilder source = new();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("#nullable enable");
        source.AppendLine();

        foreach (INamedTypeSymbol clientType in clientTypes)
        {
            GenerateClient(source, clientType);
        }

        return source.ToString();
    }

    private static void GenerateClient(StringBuilder source, INamedTypeSymbol clientType)
    {
        string? namespaceName = clientType.ContainingNamespace.IsGlobalNamespace
            ? null
            : clientType.ContainingNamespace.ToDisplayString();

        if (namespaceName is not null)
        {
            source.AppendLine($"namespace {namespaceName}");
            source.AppendLine("{");
            source.AppendLine();
        }

        string interfaceName = clientType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string clientName = GetClientName(clientType);
        string implementationName = clientName + "ActorClient";
        string extensionName = clientName + "ActorClientExtensions";
        string extensionMethodName = "As" + clientName;

        IMethodSymbol[] methods = GetClientMethods(clientType);

        foreach (IMethodSymbol method in methods)
        {
            string requestName = GetRequestName(clientName, method);

            if (method.Parameters.Length == 0)
            {
                source.AppendLine($"public readonly record struct {requestName};");
            }
            else
            {
                source.Append($"public readonly record struct {requestName}(");

                for (int i = 0; i < method.Parameters.Length; i++)
                {
                    if (i > 0)
                    {
                        source.Append(", ");
                    }

                    IParameterSymbol parameter = method.Parameters[i];
                    source.Append(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                    source.Append(' ');
                    source.Append(ToPropertyName(parameter.Name));
                }

                source.AppendLine(");");
            }
        }

        if (methods.Length > 0)
        {
            source.AppendLine();
        }

        source.AppendLine($"internal sealed class {implementationName} : {interfaceName}");
        source.AppendLine("{");
        source.AppendLine("    private readonly global::ULinkActor.ActorRef actor;");
        source.AppendLine("    private readonly global::System.TimeSpan callTimeout;");
        source.AppendLine();
        source.AppendLine($"    public {implementationName}(global::ULinkActor.ActorRef actor, global::System.TimeSpan callTimeout)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(actor);");
        source.AppendLine();
        source.AppendLine("        if (callTimeout <= global::System.TimeSpan.Zero)");
        source.AppendLine("        {");
        source.AppendLine("            throw new global::System.ArgumentOutOfRangeException(nameof(callTimeout), \"Call timeout must be greater than zero.\");");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        this.actor = actor;");
        source.AppendLine("        this.callTimeout = callTimeout;");
        source.AppendLine("    }");

        foreach (IMethodSymbol method in methods)
        {
            GenerateMethod(source, method, clientName);
        }

        source.AppendLine("}");
        source.AppendLine();

        source.AppendLine($"public static class {extensionName}");
        source.AppendLine("{");
        source.AppendLine($"    public static {interfaceName} {extensionMethodName}(this global::ULinkActor.ActorRef actor, global::System.TimeSpan callTimeout)");
        source.AppendLine("    {");
        source.AppendLine($"        return new {implementationName}(actor, callTimeout);");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine($"    public static {interfaceName} {extensionMethodName}<TMessage>(this global::ULinkActor.ActorRef<TMessage> actor, global::System.TimeSpan callTimeout)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(actor);");
        source.AppendLine($"        return new {implementationName}(actor.Untyped, callTimeout);");
        source.AppendLine("    }");
        source.AppendLine("}");
        source.AppendLine();

        if (namespaceName is not null)
        {
            source.AppendLine("}");
            source.AppendLine();
        }
    }

    private static void GenerateMethod(StringBuilder source, IMethodSymbol method, string clientName)
    {
        string returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string methodName = SanitizeIdentifier(method.Name);
        string requestName = GetRequestName(clientName, method);

        source.AppendLine();
        source.Append($"    public {returnType} {methodName}(");

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            if (i > 0)
            {
                source.Append(", ");
            }

            IParameterSymbol parameter = method.Parameters[i];
            source.Append(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            source.Append(' ');
            source.Append(SanitizeIdentifier(parameter.Name));
        }

        source.AppendLine(")");
        source.AppendLine("    {");
        source.Append("        ");

        INamedTypeSymbol returnTypeSymbol = (INamedTypeSymbol)method.ReturnType;

        if (IsValueTask(returnTypeSymbol))
        {
            source.Append("return actor.Send(");
            AppendRequestConstruction(source, requestName, method);
            source.AppendLine(");");
        }
        else if (IsValueTaskOfT(returnTypeSymbol))
        {
            ITypeSymbol responseType = returnTypeSymbol.TypeArguments[0];
            source.Append($"return actor.Call<{responseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>(");
            AppendRequestConstruction(source, requestName, method);
            source.AppendLine(", callTimeout);");
        }
        source.AppendLine("    }");
    }

    private static IMethodSymbol[] GetClientMethods(INamedTypeSymbol clientType)
    {
        return clientType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary && !method.IsStatic)
            .ToArray();
    }

    private static void AppendRequestConstruction(StringBuilder source, string requestName, IMethodSymbol method)
    {
        source.Append("new ");
        source.Append(requestName);
        source.Append('(');

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            if (i > 0)
            {
                source.Append(", ");
            }

            source.Append(SanitizeIdentifier(method.Parameters[i].Name));
        }

        source.Append(')');
    }

    private static string GetClientName(INamedTypeSymbol clientType)
    {
        string name = SanitizeIdentifier(clientType.Name);

        if (name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]))
        {
            return name[1..];
        }

        return name;
    }

    private static string GetRequestName(string clientName, IMethodSymbol method)
    {
        return clientName + SanitizeIdentifier(method.Name) + "Request";
    }

    private static bool IsValueTask(INamedTypeSymbol type)
    {
        return type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ==
            "global::System.Threading.Tasks.ValueTask";
    }

    private static bool IsValueTaskOfT(INamedTypeSymbol type)
    {
        return type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ==
            "global::System.Threading.Tasks.ValueTask<TResult>";
    }

    private static string ToPropertyName(string value)
    {
        string identifier = SanitizeIdentifier(value);

        if (identifier.Length == 0)
        {
            return "Value";
        }

        if (identifier.Length == 1)
        {
            return identifier.ToUpperInvariant();
        }

        return char.ToUpperInvariant(identifier[0]) + identifier[1..];
    }

    private static string SanitizeIdentifier(string value)
    {
        StringBuilder builder = new(value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];

            if ((i == 0 && (char.IsLetter(ch) || ch == '_')) ||
                (i > 0 && (char.IsLetterOrDigit(ch) || ch == '_')))
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('_');
            }
        }

        return builder.Length == 0 ? "Value" : builder.ToString();
    }
}
