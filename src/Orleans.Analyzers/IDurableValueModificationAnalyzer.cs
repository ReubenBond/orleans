#nullable enable

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class IDurableValueModificationAnalyzer : DiagnosticAnalyzer
    {
        public const string RuleId = "ORLEANS0014";
        private const string Category = "Usage";
        private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.IDurableValueModificationTitle), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.IDurableValueModificationMessageFormat), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.IDurableValueModificationDescription), Resources.ResourceManager, typeof(Resources));

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            RuleId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeSyntaxNode, SyntaxKind.SimpleMemberAccessExpression);
        }

        private void AnalyzeSyntaxNode(SyntaxNodeAnalysisContext context)
        {
            var memberAccessExpr = (MemberAccessExpressionSyntax)context.Node;

            // Check if it's accessing the 'Value' property
            if (memberAccessExpr.Name.Identifier.ValueText != "Value")
            {
                return;
            }

            // Get the symbol for the expression being accessed (e.g., 'state' in 'state.Value')
            var symbolInfo = context.SemanticModel.GetSymbolInfo(memberAccessExpr.Expression);
            if (symbolInfo.Symbol is not { } accessedSymbol)
            {
                return;
            }

            // Get the type of the symbol being accessed
            ITypeSymbol? accessedType = null;
            if (accessedSymbol is ILocalSymbol localSymbol)
            {
                accessedType = localSymbol.Type;
            }
            else if (accessedSymbol is IParameterSymbol parameterSymbol)
            {
                accessedType = parameterSymbol.Type;
            }
            else if (accessedSymbol is IFieldSymbol fieldSymbol)
            {
                accessedType = fieldSymbol.Type;
            }
            else if (accessedSymbol is IPropertySymbol propertySymbol)
            {
                accessedType = propertySymbol.Type;
            }

            if (accessedType is null)
            {
                return;
            }

            // Check if the type is IDurableValue<T>
            if (accessedType is INamedTypeSymbol namedTypeSymbol &&
                namedTypeSymbol.IsGenericType &&
                namedTypeSymbol.ConstructedFrom.ToDisplayString() == "Orleans.Runtime.IDurableValue<T>")
            {
                // Now check the context of this 'Value' access.
                // We want to warn if it's part of a further member access or invocation on the left side of an assignment.

                var parent = memberAccessExpr.Parent;

                // Case 1: Direct assignment to a property/field of the Value (e.g., state.Value.Age = 2)
                if (parent is MemberAccessExpressionSyntax parentMemberAccess &&
                    parentMemberAccess.Parent is AssignmentExpressionSyntax assignment &&
                    assignment.Left == parentMemberAccess)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccessExpr.GetLocation()));
                    return;
                }

                // Case 2: Invocation of a method on the Value or its members (e.g., state.Value.List.Add(item))
                if (parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax invocation } &&
                    invocation.Expression == parent)
                {
                     context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccessExpr.GetLocation()));
                     return;
                }

                 // Case 3: Invocation directly on the Value property if it's a delegate or similar
                 if (parent is InvocationExpressionSyntax directInvocation && directInvocation.Expression == memberAccessExpr)
                 {
                     context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccessExpr.GetLocation()));
                     return;
                 }

                 // Case 4: Passing the Value or its members as ref/out arguments (less common but still modification)
                 if (parent is ArgumentSyntax argumentSyntax && (argumentSyntax.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argumentSyntax.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
                 {
                     context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccessExpr.GetLocation()));
                     return;
                 }

                 // Case 5: Using increment/decrement operators (e.g., state.Value.Count++)
                 if (parent is PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax)
                 {
                     context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccessExpr.GetLocation()));
                     return;
                 }
            }
        }
    }
}
