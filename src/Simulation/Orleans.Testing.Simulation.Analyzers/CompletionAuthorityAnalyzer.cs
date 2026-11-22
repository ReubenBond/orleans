using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Orleans.Testing.Simulation.Analyzers;

/// <summary>
/// <para>
/// Enforces, in the Clockwork runtime assembly, that every completion Clockwork performs for simulation code runs through
/// <c>Orleans.Testing.Simulation.Runtime.Tasks.SimulationCompletion</c>. The BCL runs a completed task's continuations where the completing
/// call runs, and queues them to the real thread pool unless no synchronization context and the default scheduler are
/// current; <c>SimulationCompletion</c> establishes that, so a completion member called anywhere else can send simulation
/// work to the thread pool.
/// </para>
/// <para>
/// <c>CW1004</c> reports a call of, or a delegate to, a completion member (<c>TaskCompletionSource</c>'s and
/// <c>TaskCompletionSource&lt;TResult&gt;</c>'s <c>Set*</c> and <c>TrySet*</c>,
/// <c>ManualResetValueTaskSourceCore&lt;TResult&gt;</c>'s <c>SetResult</c> and <c>SetException</c>, and
/// <c>CancellationTokenSource</c>'s <c>Cancel</c>, <c>CancelAsync</c>, and <c>CancelAfter</c>) unless it is inside
/// <c>SimulationCompletion</c> outside a lambda or local function, or directly inside a synchronous lambda, or is a
/// method group, passed to a parameter marked
/// <c>Orleans.Testing.Simulation.Runtime.Tasks.CompletionCallbackAttribute</c>, which runs only through <c>SimulationCompletion</c>. Outside
/// <c>SimulationCompletion</c>, such a parameter may only be passed on to another such parameter. The rule is active
/// only in the assembly which defines <c>SimulationCompletion</c>.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CompletionAuthorityAnalyzer : DiagnosticAnalyzer
{
    private const string AuthorityTypeName = "Orleans.Testing.Simulation.Runtime.Tasks.SimulationCompletion";
    private const string CallbackAttributeTypeName = "Orleans.Testing.Simulation.Runtime.Tasks.CompletionCallbackAttribute";

    private static readonly ImmutableHashSet<string> TaskCompletionSourceMembers = ImmutableHashSet.Create(
        "SetResult",
        "TrySetResult",
        "SetException",
        "TrySetException",
        "SetCanceled",
        "TrySetCanceled",
        "SetFromTask",
        "TrySetFromTask");

    private static readonly ImmutableHashSet<string> ValueTaskSourceCoreMembers = ImmutableHashSet.Create("SetResult", "SetException");

    private static readonly ImmutableHashSet<string> CancellationTokenSourceMembers =
        ImmutableHashSet.Create("Cancel", "CancelAsync", "CancelAfter");

    /// <summary>
    /// <c>CW1004</c>: Clockwork code calls, or makes a delegate to, a completion member outside <c>SimulationCompletion</c>.
    /// </summary>
    public static readonly DiagnosticDescriptor CompletionOutsideAuthority = new(
        id: "CW1004",
        title: "Clockwork completes tasks through SimulationCompletion",
        messageFormat: "'{0}' completes a task, or cancels a token, outside SimulationCompletion; call it in a lambda passed to SimulationCompletion.Run",
        category: "Clockwork.Runtime",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every completion Clockwork performs for simulation code runs through SimulationCompletion, with no synchronization context and the default scheduler current, so the BCL runs the continuations it completes inline on the simulation's logical thread rather than queuing them to the real thread pool.");

    /// <summary>
    /// <c>CW1004</c>: a completion callback flows somewhere other than another completion callback parameter.
    /// </summary>
    public static readonly DiagnosticDescriptor CompletionCallbackEscapes = new(
        id: "CW1004",
        title: "Clockwork completes tasks through SimulationCompletion",
        messageFormat: "Completion callback '{0}' may only be passed to another [CompletionCallback] parameter, which runs it through SimulationCompletion",
        category: "Clockwork.Runtime",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A [CompletionCallback] delegate may call completion members, so it runs only through SimulationCompletion.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [CompletionOutsideAuthority, CompletionCallbackEscapes];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        INamedTypeSymbol? authority = context.Compilation.GetTypeByMetadataName(AuthorityTypeName);
        if (authority is null || !SymbolEqualityComparer.Default.Equals(authority.ContainingAssembly, context.Compilation.Assembly))
        {
            return;
        }

        INamedTypeSymbol? callbackAttribute = context.Compilation.GetTypeByMetadataName(CallbackAttributeTypeName);
        INamedTypeSymbol?[] taskCompletionSources =
        [
            context.Compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskCompletionSource"),
            context.Compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskCompletionSource`1"),
        ];
        INamedTypeSymbol? valueTaskSourceCore =
            context.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1");
        INamedTypeSymbol? cancellationTokenSource = context.Compilation.GetTypeByMetadataName("System.Threading.CancellationTokenSource");

        bool IsCompletionMember(IMethodSymbol method)
        {
            INamedTypeSymbol type = method.ContainingType.OriginalDefinition;
            if (taskCompletionSources.Any(candidate => SymbolEqualityComparer.Default.Equals(type, candidate)))
            {
                return TaskCompletionSourceMembers.Contains(method.Name);
            }

            if (SymbolEqualityComparer.Default.Equals(type, valueTaskSourceCore))
            {
                return ValueTaskSourceCoreMembers.Contains(method.Name);
            }

            return SymbolEqualityComparer.Default.Equals(type, cancellationTokenSource)
                && CancellationTokenSourceMembers.Contains(method.Name);
        }

        bool IsCallbackParameter(IParameterSymbol parameter) =>
            callbackAttribute is not null
            && parameter.OriginalDefinition.GetAttributes().Any(
                attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, callbackAttribute));

        bool IsInsideAuthority(ISymbol containingSymbol)
        {
            for (INamedTypeSymbol? type = containingSymbol.ContainingType; type is not null; type = type.ContainingType)
            {
                if (SymbolEqualityComparer.Default.Equals(type, authority))
                {
                    return true;
                }
            }

            return false;
        }

        // Whether the value of 'operation' is passed, through conversions and delegate creation, to a callback parameter.
        bool IsPassedAsCompletionCallback(IOperation operation)
        {
            IOperation? parent = operation.Parent;
            while (parent is IDelegateCreationOperation or IConversionOperation)
            {
                parent = parent.Parent;
            }

            return parent is IArgumentOperation { Parameter: { } parameter } && IsCallbackParameter(parameter);
        }

        // A completion member may be used directly inside a synchronous lambda passed as a completion callback, or
        // anywhere in SimulationCompletion outside a lambda or local function. An async lambda runs everything after its
        // first await outside the completion, and a local function may be invoked from anywhere it escapes to.
        bool IsAuthorized(IOperation use, ISymbol containingSymbol)
        {
            for (IOperation? operation = use.Parent; operation is not null; operation = operation.Parent)
            {
                switch (operation)
                {
                    case IAnonymousFunctionOperation function:
                        return !function.Symbol.IsAsync && IsPassedAsCompletionCallback(function);
                    case ILocalFunctionOperation:
                        return false;
                }
            }

            return IsInsideAuthority(containingSymbol);
        }

        context.RegisterOperationAction(
            operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;
                if (IsCompletionMember(invocation.TargetMethod)
                    && !IsAuthorized(invocation, operationContext.ContainingSymbol))
                {
                    Report(operationContext, CompletionOutsideAuthority, invocation, invocation.TargetMethod);
                }
            },
            OperationKind.Invocation);

        context.RegisterOperationAction(
            operationContext =>
            {
                var reference = (IMethodReferenceOperation)operationContext.Operation;
                if (IsCompletionMember(reference.Method)
                    && !IsPassedAsCompletionCallback(reference)
                    && !IsAuthorized(reference, operationContext.ContainingSymbol))
                {
                    Report(operationContext, CompletionOutsideAuthority, reference, reference.Method);
                }
            },
            OperationKind.MethodReference);

        context.RegisterOperationAction(
            operationContext =>
            {
                var reference = (IParameterReferenceOperation)operationContext.Operation;
                if (IsCallbackParameter(reference.Parameter)
                    && !IsPassedAsCompletionCallback(reference)
                    && !IsInsideAuthority(operationContext.ContainingSymbol))
                {
                    operationContext.ReportDiagnostic(Diagnostic.Create(
                        CompletionCallbackEscapes,
                        reference.Syntax.GetLocation(),
                        reference.Parameter.Name));
                }
            },
            OperationKind.ParameterReference);
    }

    private static void Report(OperationAnalysisContext context, DiagnosticDescriptor descriptor, IOperation use, IMethodSymbol member) =>
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor,
            use.Syntax.GetLocation(),
            member.ContainingType.Name + "." + member.Name));
}
