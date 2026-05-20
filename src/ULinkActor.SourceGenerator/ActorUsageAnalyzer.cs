using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ULinkActor.SourceGenerator;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ActorUsageAnalyzer : DiagnosticAnalyzer
{
    public const string SelfCallDiagnosticId = "ULA001";
    public const string BlockingWaitDiagnosticId = "ULA002";
    public const string DiscardedCallDiagnosticId = "ULA003";

    private static readonly DiagnosticDescriptor SelfCallRule = new(
        SelfCallDiagnosticId,
        "Do not call the current actor through its own ActorRef",
        "Actor self-call through '{0}' can deadlock; handle the work directly or send a separate message",
        "ULinkActor.Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor BlockingWaitRule = new(
        BlockingWaitDiagnosticId,
        "Do not block inside an actor",
        "Blocking on '{0}' inside an actor can stall the mailbox; use await instead",
        "ULinkActor.Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DiscardedCallRule = new(
        DiscardedCallDiagnosticId,
        "Do not discard actor call results",
        "Actor request call '{0}' returns a response and should be awaited, returned, or stored; use Send for fire-and-forget messages",
        "ULinkActor.Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(SelfCallRule, BlockingWaitRule, DiscardedCallRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }

        IMethodSymbol? method = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;

        if (method is null)
        {
            return;
        }

        bool isInsideActorType = IsInsideActorType(context);

        if (isInsideActorType && IsSelfCall(context, memberAccess, method))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                SelfCallRule,
                memberAccess.Name.GetLocation(),
                memberAccess.ToString()));
            return;
        }

        if (IsActorCall(method) && IsDiscardedInvocationResult(invocation))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiscardedCallRule,
                memberAccess.Name.GetLocation(),
                memberAccess.ToString()));
            return;
        }

        if (isInsideActorType && method.Name == "Wait" && IsTaskLike(method.ContainingType))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                BlockingWaitRule,
                memberAccess.Name.GetLocation(),
                memberAccess.ToString()));
        }
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        MemberAccessExpressionSyntax memberAccess = (MemberAccessExpressionSyntax)context.Node;

        if (memberAccess.Name.Identifier.ValueText != "Result" || !IsInsideActorType(context))
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol is not IPropertySymbol property)
        {
            return;
        }

        if (!IsTaskLike(property.ContainingType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            BlockingWaitRule,
            memberAccess.Name.GetLocation(),
            memberAccess.ToString()));
    }

    private static bool IsSelfCall(
        SyntaxNodeAnalysisContext context,
        MemberAccessExpressionSyntax memberAccess,
        IMethodSymbol method)
    {
        if (method.Name != "Call" || !IsActorRef(method.ContainingType))
        {
            return false;
        }

        if (memberAccess.Expression is not MemberAccessExpressionSyntax selfAccess ||
            selfAccess.Name.Identifier.ValueText != "Self")
        {
            return false;
        }

        ISymbol? selfSymbol = context.SemanticModel.GetSymbolInfo(
            selfAccess,
            context.CancellationToken).Symbol;

        return selfSymbol is IPropertySymbol
        {
            Name: "Self",
            ContainingType:
            {
                Name: "ActorContext",
                ContainingNamespace:
                {
                    Name: "ULinkActor",
                    ContainingNamespace.IsGlobalNamespace: true
                }
            }
        };
    }

    private static bool IsDiscardedInvocationResult(InvocationExpressionSyntax invocation)
    {
        if (invocation.Parent is ExpressionStatementSyntax)
        {
            return true;
        }

        if (invocation.Parent is not AssignmentExpressionSyntax assignment ||
            assignment.Parent is not ExpressionStatementSyntax ||
            !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            return false;
        }

        return assignment.Left is IdentifierNameSyntax identifier &&
            identifier.Identifier.ValueText == "_";
    }

    private static bool IsInsideActorType(SyntaxNodeAnalysisContext context)
    {
        ClassDeclarationSyntax? classDeclaration = context.Node.FirstAncestorOrSelf<ClassDeclarationSyntax>();

        if (classDeclaration is null)
        {
            return false;
        }

        if (context.SemanticModel.GetDeclaredSymbol(classDeclaration, context.CancellationToken) is not INamedTypeSymbol type)
        {
            return false;
        }

        return ImplementsActor(type);
    }

    private static bool ImplementsActor(INamedTypeSymbol type)
    {
        foreach (INamedTypeSymbol interfaceType in type.AllInterfaces)
        {
            if (interfaceType.Name == "IActor" &&
                interfaceType.ContainingNamespace is
                {
                    Name: "ULinkActor",
                    ContainingNamespace.IsGlobalNamespace: true
                })
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsActorRef(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        return type.Name == "ActorRef" &&
            type.ContainingNamespace is
            {
                Name: "ULinkActor",
                ContainingNamespace.IsGlobalNamespace: true
            };
    }

    private static bool IsActorCall(IMethodSymbol method)
    {
        return method.Name == "Call" && (IsActorRef(method.ContainingType) || IsActorSystem(method.ContainingType));
    }

    private static bool IsActorSystem(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        return type.Name == "ActorSystem" &&
            type.ContainingNamespace is
            {
                Name: "ULinkActor",
                ContainingNamespace.IsGlobalNamespace: true
            };
    }

    private static bool IsTaskLike(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        INamedTypeSymbol original = type.OriginalDefinition;
        string name = original.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return name is "global::System.Threading.Tasks.Task" or
            "global::System.Threading.Tasks.Task<TResult>" or
            "global::System.Threading.Tasks.ValueTask<TResult>";
    }
}
