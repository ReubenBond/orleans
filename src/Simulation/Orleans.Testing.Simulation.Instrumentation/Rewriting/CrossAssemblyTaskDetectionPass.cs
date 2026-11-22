using System.Globalization;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Read-only rewriting pass that enforces cross-assembly task control. It flags every call site
/// that invokes a method in an <em>uncontrolled</em> assembly - one that is neither the assembly being
/// rewritten, nor part of the BCL (<c>System.*</c> / <c>mscorlib</c> / <c>netstandard</c>), nor the
/// Clockwork runtime shim - and returns a <see cref="System.Threading.Tasks.Task"/>,
/// <see cref="System.Threading.Tasks.ValueTask"/>, their generic forms, or any other custom awaitable (a
/// type exposing a parameterless <c>GetAwaiter</c>). Such a task's continuations are produced by code the
/// rewriter never saw and can therefore escape the deterministic scheduler; the pass surfaces each with a
/// precise <see cref="RewriteDiagnosticIds.UncontrolledTaskReturn"/> warning naming the callee and the
/// call-site method/IL offset (and source line when symbols are present) so the escape is never silently
/// accepted. The pass runs last, after controlled BCL calls have already been redirected onto the shim, so
/// controlled surfaces are not re-flagged. Framework-hosted I/O, including <c>HttpClient</c>, remains outside
/// this diagnostic boundary, so <c>System.*</c> callees are intentionally not flagged.
/// </summary>
internal sealed class CrossAssemblyTaskDetectionPass : RewritePass
{
    private const string TaskNamespace = "System.Threading.Tasks";
    private const string ShimAssemblyName = "Orleans.Testing.Simulation";

    private readonly HashSet<Instruction> _reported = [];

    public CrossAssemblyTaskDetectionPass(RewriteSession session)
        : base(session)
    {
    }

    protected override Instruction VisitInstruction(Instruction instruction)
    {
        if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) ||
            instruction.Operand is not MethodReference method ||
            !_reported.Add(instruction))
        {
            return instruction;
        }

        if (!IsUncontrolledExternalCallee(method))
        {
            return instruction;
        }

        AwaitableProbe probe = ProbeAwaitable(method, out string awaitableKind, out string? resolutionError);
        if (probe == AwaitableProbe.ResolutionFailed)
        {
            Session.AddDiagnostic(RewriteDiagnostic.Warning(
                RewriteDiagnosticIds.AwaitableResolutionFailed,
                $"Could not determine whether return type '{method.ReturnType.FullName}' from '{method.FullName}' is a custom awaitable: {resolutionError}",
                CecilNames.FullyQualifiedMethodName(Method!),
                instruction.Offset));
            Session.AddUnresolvedReference(method.ReturnType.Scope?.ToString() ?? method.ReturnType.FullName);
            return instruction;
        }

        if (probe != AwaitableProbe.Awaitable)
        {
            return instruction;
        }

        RewriteSession.TryGetSequencePoint(Method!, instruction, out string? file, out int line);
        string location = file is null
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $" ({file}:{line})");

        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"Call to '{method.FullName}' returns an uncontrolled {awaitableKind} from assembly '{AssemblyNameOf(method.DeclaringType)}'{location}; its continuations are not routed through the deterministic scheduler. Rewrite that assembly, await the result in rewritten code, or wrap it explicitly so the continuation cannot escape.");

        Session.AddDiagnostic(RewriteDiagnostic.Warning(
            RewriteDiagnosticIds.UncontrolledTaskReturn,
            message,
            CecilNames.FullyQualifiedMethodName(Method!),
            instruction.Offset));

        return instruction;
    }

    private bool IsUncontrolledExternalCallee(MethodReference method)
    {
        string assembly = AssemblyNameOf(method.DeclaringType);

        // The assembly currently being rewritten is controlled by definition.
        if (string.Equals(assembly, Session.TargetModule.Assembly?.Name?.Name, StringComparison.Ordinal))
        {
            return false;
        }

        // The Clockwork runtime shim (holds the controlled Task/Thread/ThreadPool surfaces) is controlled.
        if (string.Equals(assembly, ShimAssemblyName, StringComparison.Ordinal))
        {
            return false;
        }

        // The BCL is either controlled through explicit rules (already redirected before this pass) or is
        // outside this diagnostic boundary.
        return !IsFrameworkAssembly(assembly);
    }

    private static bool IsFrameworkAssembly(string assembly) =>
        assembly.Length == 0 ||
        string.Equals(assembly, "mscorlib", StringComparison.Ordinal) ||
        string.Equals(assembly, "netstandard", StringComparison.Ordinal) ||
        string.Equals(assembly, "System", StringComparison.Ordinal) ||
        assembly.StartsWith("System.", StringComparison.Ordinal);

    private static AwaitableProbe ProbeAwaitable(
        MethodReference method,
        out string kind,
        out string? resolutionError)
    {
        TypeReference returnType = method.ReturnType;
        resolutionError = null;

        if (returnType.Namespace == TaskNamespace)
        {
            switch (returnType.Name)
            {
                case "Task":
                case "Task`1":
                    kind = "Task";
                    return AwaitableProbe.Awaitable;
                case "ValueTask":
                case "ValueTask`1":
                    kind = "ValueTask";
                    return AwaitableProbe.Awaitable;
            }
        }

        AwaitableProbe customProbe = ProbeParameterlessGetAwaiter(returnType, out resolutionError);
        if (customProbe == AwaitableProbe.Awaitable)
        {
            kind = "custom awaitable";
            return AwaitableProbe.Awaitable;
        }

        kind = string.Empty;
        return customProbe;
    }

    private static AwaitableProbe ProbeParameterlessGetAwaiter(
        TypeReference returnType,
        out string? resolutionError)
    {
        resolutionError = null;
        if (returnType.IsGenericParameter || returnType.IsByReference || returnType.IsPointer)
        {
            return AwaitableProbe.NotAwaitable;
        }

        TypeDefinition? definition;
        try
        {
            definition = returnType.Resolve();
        }
        catch (AssemblyResolutionException ex)
        {
            resolutionError = ex.Message;
            return AwaitableProbe.ResolutionFailed;
        }

        if (definition is null)
        {
            resolutionError = $"Type '{returnType.FullName}' could not be resolved.";
            return AwaitableProbe.ResolutionFailed;
        }

        while (definition is not null)
        {
            foreach (MethodDefinition candidate in definition.Methods)
            {
                if (candidate.Name == "GetAwaiter" && candidate.Parameters.Count == 0 && !candidate.IsStatic)
                {
                    return AwaitableProbe.Awaitable;
                }
            }

            try
            {
                TypeReference? baseType = definition.BaseType;
                definition = baseType?.Resolve();
                if (baseType is not null && definition is null)
                {
                    resolutionError = $"Base type '{baseType.FullName}' could not be resolved.";
                    return AwaitableProbe.ResolutionFailed;
                }
            }
            catch (AssemblyResolutionException ex)
            {
                resolutionError = ex.Message;
                return AwaitableProbe.ResolutionFailed;
            }
        }

        return AwaitableProbe.NotAwaitable;
    }

    private static string AssemblyNameOf(TypeReference type) =>
        type.Scope switch
        {
            AssemblyNameReference assemblyName => assemblyName.Name,
            ModuleDefinition module => module.Assembly?.Name?.Name ?? string.Empty,
            _ => type.Module?.Assembly?.Name?.Name ?? string.Empty,
        };

    private enum AwaitableProbe
    {
        NotAwaitable,
        Awaitable,
        ResolutionFailed,
    }
}
