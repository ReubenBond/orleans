// This pass is adapted from Microsoft Coyote's
// Source/Test/Rewriting/Passes/Rewriting/ExceptionFilterRewritingPass.cs, licensed under the MIT License:
//
//   Copyright (c) Microsoft Corporation.
//   Licensed under the MIT License.
//
// See THIRD-PARTY-NOTICES.md for the adaptation record. Clockwork-specific changes: the guard is resolved
// through the shared RewriteSession's replacement resolver (rather than Coyote's typeof-based import) so it
// targets the Clockwork runtime shim; the injected guard rethrows Clockwork's internal
// ControlledOperationAbortSignal instead of Coyote's ExecutionCanceled/ThreadInterrupted exceptions; the
// async-state-machine detection recognises Clockwork's substituted controlled builder types as well as the
// BCL builders; and the pass records a manifest transformation for every hardened handler.

using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Runtime.Policy;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Rewriting pass that hardens user exception handling so a rewritten assembly's broad
/// <c>catch (Exception)</c> / <c>catch</c> blocks and exception <c>filter</c>s cannot swallow the
/// scheduler's internal control-flow signal. At the start of each such handler it injects
/// <c>dup; call SimulationExceptionGuard.ThrowIfControlSignal(object)</c>, which rethrows the signal (and
/// only the signal) before the user handler runs. Narrow typed catches, finally blocks, rethrow-only
/// handlers, and compiler-generated async-state-machine handlers are left untouched, so ordinary
/// application exception handling is unchanged.
/// </summary>
internal sealed class ExceptionHardeningRewritingPass : RewritePass
{
    private const string GuardType = "Orleans.Testing.Simulation.Runtime.SimulationExceptionGuard";
    private const string GuardMethod = "ThrowIfControlSignal";
    private const string ObjectFullName = "System.Object";

    private readonly RewriteReplacement _guardReplacement;
    private MethodReference? _guard;
    private bool _guardResolutionFailed;

    public ExceptionHardeningRewritingPass(RewriteSession session, string shimAssemblyName)
        : base(session) =>
        _guardReplacement = RewriteReplacement.Method(shimAssemblyName, GuardType, GuardMethod, ObjectFullName);

    internal override void VisitMethod(MethodDefinition method)
    {
        // Sets Method/Processor and performs a harmless no-op instruction walk (this pass edits handler
        // boundaries, not individual instructions).
        base.VisitMethod(method);

        if (method.IsAbstract || !method.HasBody || !method.Body.HasExceptionHandlers)
        {
            return;
        }

        if (!TryResolveGuard())
        {
            return;
        }

        // Snapshot the handler list: AddThrowIfControlSignal repoints boundaries but never adds handlers.
        foreach (ExceptionHandler handler in method.Body.ExceptionHandlers.ToArray())
        {
            VisitExceptionHandler(handler);
        }
    }

    private void VisitExceptionHandler(ExceptionHandler handler)
    {
        if (IsAsyncStateMachineExceptionHandler(handler) || IsRethrowHandler(handler))
        {
            // Never instrument the compiler-generated catch of an async state machine (it faults the task
            // through the builder), or a handler that only rethrows.
            return;
        }

        if (handler.FilterStart is null)
        {
            if (handler.CatchType is null)
            {
                // A finally / fault block: nothing to harden.
                return;
            }

            string name = handler.CatchType.FullName;
            if (name == ObjectFullName || name == typeof(Exception).FullName)
            {
                AddThrowIfControlSignal(handler);
            }
        }
        else
        {
            // A filter selects which exceptions it catches; a user predicate could match the control
            // signal, so guard the handler entry.
            AddThrowIfControlSignal(handler);
        }
    }

    private void AddThrowIfControlSignal(ExceptionHandler handler)
    {
        Instruction previousStart = handler.HandlerStart;
        var duplicate = Instruction.Create(OpCodes.Dup);
        var guardCall = Instruction.Create(OpCodes.Call, _guard);

        Processor!.InsertBefore(previousStart, duplicate);
        Processor.InsertBefore(previousStart, guardCall);
        handler.HandlerStart = duplicate;

        // Re-point any other handler boundary that pointed at the old handler start so adjacent regions
        // (a preceding try/handler that ended exactly where this one began) do not now overlap the guard.
        foreach (ExceptionHandler other in Processor.Body.ExceptionHandlers)
        {
            if (other.TryEnd == previousStart)
            {
                other.TryEnd = duplicate;
            }

            if (other.HandlerEnd == previousStart)
            {
                other.HandlerEnd = duplicate;
            }
        }

        IsMethodBodyModified = true;
        RecordTransformation(previousStart);
    }

    private void RecordTransformation(Instruction site)
    {
        RewriteSession.TryGetSequencePoint(Method!, site, out string? file, out int line);
        Session.AddTransformation(new ManifestTransformation(
            "clockwork.exceptions.harden",
            RewriteOperationKind.WrapAfterCall,
            TransformationOutcome.Transformed,
            SimulationApiPolicy.Controlled,
            "System.Exception::catch",
            _guardReplacement.ToCanonicalString(),
            CecilNames.FullyQualifiedMethodName(Method!),
            site.Offset,
            file,
            line));
    }

    private bool TryResolveGuard()
    {
        if (_guard is not null)
        {
            return true;
        }

        if (_guardResolutionFailed)
        {
            return false;
        }

        if (Session.Resolver.TryResolveMethod(Session.TargetModule, _guardReplacement, out MethodReference open, out _, out string? error))
        {
            _guard = open;
            return true;
        }

        _guardResolutionFailed = true;
        Session.AddDiagnostic(RewriteDiagnostic.Error(
            RewriteDiagnosticIds.UnresolvedReplacement,
            $"{error} Exception hardening is enabled but its guard '{_guardReplacement.ToCanonicalString()}' " +
            "could not be resolved; supply the Clockwork assembly."));
        Session.AddUnresolvedReference(_guardReplacement.ToCanonicalString());
        return false;
    }

    // Adapted from Coyote: a handler that only stores/loads the exception and then (re)throws is a plain
    // rethrow and needs no guard.
    private static bool IsRethrowHandler(ExceptionHandler handler)
    {
        Code previousOpCode = Code.Nop;
        bool isRethrowing = false;
        Instruction? instruction = handler.HandlerStart;
        while (instruction is not null && instruction != handler.HandlerEnd)
        {
            Code opCode = instruction.OpCode.Code;
            if (opCode is Code.Throw or Code.Rethrow)
            {
                isRethrowing = true;
                break;
            }

            if (opCode != Code.Nop && opCode != Code.Pop)
            {
                if (previousOpCode != Code.Nop && !IsStoreLoadOpCodeMatching(previousOpCode, opCode))
                {
                    break;
                }

                previousOpCode = opCode;
            }

            instruction = instruction.Next;
        }

        return isRethrowing;
    }

    private static bool IsStoreLoadOpCodeMatching(Code storeCode, Code loadCode) =>
        storeCode is Code.Stloc_0 ? loadCode is Code.Ldloc_0 :
        storeCode is Code.Stloc_1 ? loadCode is Code.Ldloc_1 :
        storeCode is Code.Stloc_2 ? loadCode is Code.Ldloc_2 :
        storeCode is Code.Stloc_3 && loadCode is Code.Ldloc_3;

    // Adapted from Coyote and generalized: recognise the compiler-generated catch that faults an async
    // state machine through either a BCL builder or Clockwork's substituted controlled builder, so it is
    // left to propagate faults normally.
    private static bool IsAsyncStateMachineExceptionHandler(ExceptionHandler handler)
    {
        Instruction? instruction = handler.HandlerStart;
        while (instruction is not null && instruction != handler.HandlerEnd)
        {
            if (instruction.Operand is MethodReference method &&
                method.Name == "SetException")
            {
                string typeName = method.DeclaringType.Name;
                if (typeName.Contains("AsyncTaskMethodBuilder", StringComparison.Ordinal) ||
                    typeName.Contains("AsyncValueTaskMethodBuilder", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            instruction = instruction.Next;
        }

        return false;
    }
}
