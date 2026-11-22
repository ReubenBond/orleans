using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Orleans.Testing.Simulation.Analyzers;

/// <summary>
/// Flags direct calls to the nondeterministic .NET BCL surface covered by Clockwork's built-in
/// deterministic rule set. Controlled time, identity, and random members raise <c>CW1001</c>. The analyzer resolves
/// the relevant framework symbols once per compilation and then matches operations by symbol identity,
/// so it is robust against namespace aliasing and using-static.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NondeterministicApiAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [
            DeterminismDiagnostics.ControlledApi,
            DeterminismDiagnostics.UnstableCollectionOrdering,
        ];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        Compilation compilation = context.Compilation;
        var known = new KnownTypes(compilation);
        if (!known.AnyResolved)
        {
            return;
        }

        context.RegisterOperationAction(ctx => AnalyzePropertyReference(ctx, known), OperationKind.PropertyReference);
        context.RegisterOperationAction(ctx => AnalyzeInvocation(ctx, known), OperationKind.Invocation);
        context.RegisterOperationAction(ctx => AnalyzeObjectCreation(ctx, known), OperationKind.ObjectCreation);
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context, KnownTypes known)
    {
        var operation = (IPropertyReferenceOperation)context.Operation;
        IPropertySymbol property = operation.Property;
        INamedTypeSymbol? type = property.ContainingType;
        if (type is null)
        {
            return;
        }

        string? ruleId = null;
        if (SymbolEqualityComparer.Default.Equals(type, known.DateTime) &&
            property.Name is "Now" or "UtcNow" or "Today")
        {
            ruleId = "clockwork.bcl.datetime." + property.Name.ToLowerInvariant();
        }
        else if (SymbolEqualityComparer.Default.Equals(type, known.DateTimeOffset) &&
            property.Name is "Now" or "UtcNow")
        {
            ruleId = "clockwork.bcl.datetimeoffset." + property.Name.ToLowerInvariant();
        }
        else if (SymbolEqualityComparer.Default.Equals(type, known.Environment) &&
            property.Name is "TickCount" or "TickCount64")
        {
            ruleId = "clockwork.bcl.environment." + property.Name.ToLowerInvariant();
        }
        else if (SymbolEqualityComparer.Default.Equals(type, known.Random) && property.Name == "Shared")
        {
            ruleId = "clockwork.bcl.random.shared";
        }
        else if (SymbolEqualityComparer.Default.Equals(type, known.TimeProvider) && property.Name == "System")
        {
            if (IsControlledProviderArgument(operation, known))
            {
                return;
            }

            ruleId = "clockwork.timeprovider.system";
        }

        if (ruleId is not null)
        {
            ReportControlled(context, operation.Syntax.GetLocation(), type.Name + "." + property.Name, ruleId);
        }
        else if (known.TryGetMetadataName(type, out string typeName)
            && InstrumentedApiInventory.Contains(typeName, property.Name)
            && (typeName != "System.Threading.Thread"
                || property.Name is not ("Priority" or "ApartmentState")
                || IsPropertyWrite(operation)))
        {
            ReportControlled(
                context,
                operation.Syntax.GetLocation(),
                type.Name + "." + property.Name,
                "clockwork.tasks.controlled");
        }

    }

    private static bool IsPropertyWrite(IPropertyReferenceOperation operation) =>
        operation.Parent switch
        {
            ISimpleAssignmentOperation assignment => ReferenceEquals(assignment.Target, operation),
            ICompoundAssignmentOperation assignment => ReferenceEquals(assignment.Target, operation),
            IIncrementOrDecrementOperation increment => ReferenceEquals(increment.Target, operation),
            _ => false,
        };

    private static bool IsControlledProviderArgument(
        IPropertyReferenceOperation operation,
        KnownTypes known)
    {
        if (operation.Parent is not IArgumentOperation argument)
        {
            return false;
        }

        if (argument.Parent is IInvocationOperation invocation
            && known.TryGetMetadataName(invocation.TargetMethod.ContainingType, out string invocationType))
        {
            return InstrumentedApiInventory.ContainsInvocation(invocationType, invocation.TargetMethod);
        }

        return argument.Parent is IObjectCreationOperation { Constructor: { } constructor }
            && known.TryGetMetadataName(constructor.ContainingType, out string constructorType)
            && InstrumentedApiInventory.Contains(constructorType, ".ctor");
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, KnownTypes known)
    {
        var operation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = operation.TargetMethod;
        INamedTypeSymbol? type = method.ContainingType;
        if (type is null)
        {
            return;
        }

        if (TryGetOrderSensitiveCollectionArgument(operation, known, out IOperation unordered))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DeterminismDiagnostics.UnstableCollectionOrdering,
                unordered.Syntax.GetLocation(),
                type.Name + "." + method.Name));
        }

        if (SymbolEqualityComparer.Default.Equals(type, known.Stopwatch))
        {
            if (method.Name == "GetTimestamp")
            {
                ReportControlled(context, operation.Syntax.GetLocation(), "Stopwatch.GetTimestamp", "clockwork.bcl.stopwatch.gettimestamp");
            }
            else if (method.Name == "GetElapsedTime" && method.Parameters.Length == 1)
            {
                ReportControlled(context, operation.Syntax.GetLocation(), "Stopwatch.GetElapsedTime", "clockwork.bcl.stopwatch.getelapsedtime");
            }

            return;
        }

        if (SymbolEqualityComparer.Default.Equals(type, known.Guid))
        {
            if (method.Name == "NewGuid")
            {
                ReportControlled(context, operation.Syntax.GetLocation(), "Guid.NewGuid", "clockwork.bcl.guid.newguid");
            }
            else if (method.Name == "CreateVersion7")
            {
                ReportControlled(context, operation.Syntax.GetLocation(), "Guid.CreateVersion7", "clockwork.bcl.guid.createversion7");
            }

            return;
        }

        if (SymbolEqualityComparer.Default.Equals(type, known.Task) &&
            method.IsStatic &&
            method.Name == "Delay")
        {
            ReportControlled(
                context,
                operation.Syntax.GetLocation(),
                "Task.Delay",
                "clockwork.tasks.delay");
            return;
        }

        if (SymbolEqualityComparer.Default.Equals(type, known.RandomNumberGenerator) &&
            method.IsStatic &&
            method.Name is "Create" or "Fill" or "GetBytes" or "GetInt32" or "GetHexString" or "GetItems" or "GetString" or "Shuffle")
        {
            ReportControlled(
                context,
                operation.Syntax.GetLocation(),
                "RandomNumberGenerator." + method.Name,
                "clockwork.bcl.rng." + method.Name.ToLowerInvariant());
            return;
        }

        if (known.TryGetMetadataName(type, out string typeName)
            && InstrumentedApiInventory.ContainsInvocation(typeName, method)
            && !InstrumentedApiInventory.DispatchesToOverride(typeName, method, operation.IsVirtual, operation.Instance?.Type))
        {
            ReportControlled(
                context,
                operation.Syntax.GetLocation(),
                type.Name + "." + method.Name,
                "clockwork.tasks.controlled");
        }
    }

    private static void AnalyzeObjectCreation(OperationAnalysisContext context, KnownTypes known)
    {
        var operation = (IObjectCreationOperation)context.Operation;
        if (operation.Constructor is not { } ctor)
        {
            return;
        }

        if (SymbolEqualityComparer.Default.Equals(ctor.ContainingType, known.Random))
        {
            string ruleId = ctor.Parameters.Length == 0
                ? "clockwork.bcl.random.ctor.unseeded"
                : "clockwork.bcl.random.ctor.seeded";
            string display = ctor.Parameters.Length == 0 ? "new Random()" : "new Random(int)";
            ReportControlled(context, operation.Syntax.GetLocation(), display, ruleId);
        }
        else if (known.TryGetMetadataName(ctor.ContainingType, out string typeName)
            && InstrumentedApiInventory.Contains(typeName, ".ctor")
            && InstrumentedApiInventory.ContainsInvocation(typeName, ctor))
        {
            ReportControlled(
                context,
                operation.Syntax.GetLocation(),
                "new " + ctor.ContainingType.Name + "(...)",
                "clockwork.tasks.controlled");
        }
    }

    private static bool TryGetOrderSensitiveCollectionArgument(
        IInvocationOperation operation,
        KnownTypes known,
        out IOperation unordered)
    {
        unordered = null!;
        IMethodSymbol method = operation.TargetMethod;
        string? containingType = method.ContainingType?.ToDisplayString();
        bool orderSensitiveLinq =
            containingType == "System.Linq.Enumerable" &&
            method.Name is "First" or "FirstOrDefault" or "Last" or "LastOrDefault" or
                "ElementAt" or "ElementAtOrDefault" or "SequenceEqual";
        bool deterministicText =
            containingType == "string" &&
            method.Name == "Join";
        if (!orderSensitiveLinq && !deterministicText)
        {
            return false;
        }

        if (method.Name == "SequenceEqual" &&
            operation.Arguments.Length >= 2 &&
            SyntaxFactory.AreEquivalent(
                Unwrap(operation.Arguments[0].Value).Syntax,
                Unwrap(operation.Arguments[1].Value).Syntax))
        {
            return false;
        }

        foreach (IArgumentOperation argument in operation.Arguments)
        {
            IOperation value = Unwrap(argument.Value);
            if (IsUnorderedCollection(value, known))
            {
                unordered = value;
                return true;
            }
        }

        return false;
    }

    private static bool IsUnorderedCollection(IOperation operation, KnownTypes known)
    {
        if (IsDictionaryOrHashSet(operation.Type, known))
        {
            return true;
        }

        return operation is IPropertyReferenceOperation property &&
            property.Property.Name is "Keys" or "Values" &&
            property.Instance is { } instance &&
            IsDictionaryOrHashSet(Unwrap(instance).Type, known);
    }

    private static bool IsDictionaryOrHashSet(ITypeSymbol? type, KnownTypes known) =>
        type is INamedTypeSymbol named &&
        (SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, known.Dictionary) ||
         SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, known.HashSet));

    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static void ReportControlled(OperationAnalysisContext context, Location location, string member, string ruleId) =>
        context.ReportDiagnostic(Diagnostic.Create(DeterminismDiagnostics.ControlledApi, location, member, ruleId));

    /// <summary>The framework symbols the analyzer matches against, resolved once per compilation.</summary>
    private sealed class KnownTypes
    {
        public KnownTypes(Compilation compilation)
        {
            DateTime = compilation.GetTypeByMetadataName("System.DateTime");
            DateTimeOffset = compilation.GetTypeByMetadataName("System.DateTimeOffset");
            Stopwatch = compilation.GetTypeByMetadataName("System.Diagnostics.Stopwatch");
            Environment = compilation.GetTypeByMetadataName("System.Environment");
            Guid = compilation.GetTypeByMetadataName("System.Guid");
            Random = compilation.GetTypeByMetadataName("System.Random");
            Task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            TimeProvider = compilation.GetTypeByMetadataName("System.TimeProvider");
            RandomNumberGenerator = compilation.GetTypeByMetadataName("System.Security.Cryptography.RandomNumberGenerator");
            Dictionary = compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
            HashSet = compilation.GetTypeByMetadataName("System.Collections.Generic.HashSet`1");

            var instrumentedTypes = ImmutableDictionary.CreateBuilder<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
            foreach (string metadataName in InstrumentedApiInventory.TypeMetadataNames)
            {
                if (compilation.GetTypeByMetadataName(metadataName) is { } type)
                {
                    instrumentedTypes[type] = metadataName;
                }
            }

            InstrumentedTypes = instrumentedTypes.ToImmutable();
        }

        public INamedTypeSymbol? DateTime { get; }

        public INamedTypeSymbol? DateTimeOffset { get; }

        public INamedTypeSymbol? Stopwatch { get; }

        public INamedTypeSymbol? Environment { get; }

        public INamedTypeSymbol? Guid { get; }

        public INamedTypeSymbol? Random { get; }

        public INamedTypeSymbol? Task { get; }

        public INamedTypeSymbol? TimeProvider { get; }

        public INamedTypeSymbol? RandomNumberGenerator { get; }

        public INamedTypeSymbol? Dictionary { get; }

        public INamedTypeSymbol? HashSet { get; }

        private ImmutableDictionary<INamedTypeSymbol, string> InstrumentedTypes { get; }

        public bool TryGetMetadataName(INamedTypeSymbol type, out string metadataName)
        {
            if (InstrumentedTypes.TryGetValue(type.OriginalDefinition, out string? resolved))
            {
                metadataName = resolved;
                return true;
            }

            metadataName = string.Empty;
            return false;
        }

        public bool AnyResolved =>
            new[] { DateTime, DateTimeOffset, Stopwatch, Environment, Guid, Random, Task, RandomNumberGenerator }
                .Any(static symbol => symbol is not null)
            || InstrumentedTypes.Count > 0;
    }
}
