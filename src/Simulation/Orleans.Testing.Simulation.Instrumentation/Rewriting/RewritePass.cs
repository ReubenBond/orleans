// Portions of this file (the IL visitor traversal - VisitAssembly/Module/Type/Method/Instruction,
// the Replace helper that fixes up branch targets and exception-handler boundaries when an
// instruction is replaced, and the SimplifyMacros/OptimizeMacros offset-fix pattern) are adapted
// from Microsoft Coyote's Source/Test/Rewriting/Passes/Pass.cs and
// Source/Test/Rewriting/Passes/Rewriting/RewritingPass.cs, licensed under the MIT License:
//
//   Copyright (c) Microsoft Corporation.
//   Licensed under the MIT License.
//
// See THIRD-PARTY-NOTICES.md for the adaptation record. Clockwork-specific changes: passes share a
// RewriteSession (rule index, replacement resolver, manifest/diagnostic sinks) instead of Coyote's
// engine-owned collections; generic-type/method helpers are generalized beyond a single argument;
// and the traversal records source mappings for the instrumentation manifest.

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Base class for a pass that traverses a module's IL using a visitor pattern and, where it applies
/// a transformation, edits the current method body. Concrete passes override the visit hooks they
/// need. Shared engine state (the rule index, replacement resolver, and manifest/diagnostic sinks)
/// is reached through <see cref="Session"/>.
/// </summary>
internal abstract class RewritePass
{
    protected RewritePass(RewriteSession session)
    {
        Session = session;
    }

    /// <summary>Gets the shared per-rewrite session state.</summary>
    protected RewriteSession Session { get; }

    /// <summary>Gets the module currently being visited.</summary>
    protected ModuleDefinition? Module { get; private set; }


    /// <summary>Gets the method currently being visited.</summary>
    protected MethodDefinition? Method { get; private set; }

    /// <summary>Gets the IL processor for the current method body.</summary>
    protected ILProcessor? Processor { get; private set; }

    /// <summary>Gets or sets a value indicating whether the current method body was modified.</summary>
    protected internal bool IsMethodBodyModified { get; set; }

    /// <summary>Visits a module before its types are visited.</summary>
    internal virtual void VisitModule(ModuleDefinition module)
    {
        Module = module;
        Method = null;
        Processor = null;
    }

    /// <summary>Visits a type before its members are visited.</summary>
    internal virtual void VisitType(TypeDefinition type)
    {
        Method = null;
        Processor = null;
    }

    /// <summary>Visits a method, walking its body instructions.</summary>
    internal virtual void VisitMethod(MethodDefinition method)
    {
        Method = method;
        Processor = null;

        if (method.IsAbstract || !method.HasBody)
        {
            return;
        }

        Processor = method.Body.GetILProcessor();
        VisitMethodBody(method.Body);

        Instruction? instruction = method.Body.Instructions.FirstOrDefault();
        while (instruction is not null)
        {
            instruction = VisitInstruction(instruction);
            instruction = instruction.Next;
        }
    }

    /// <summary>Visits the current method's body before its instructions are walked.</summary>
    protected virtual void VisitMethodBody(MethodBody body)
    {
    }

    /// <summary>
    /// Visits a single instruction. Returns the last instruction produced by any edit (so traversal
    /// continues from the right place), or the original instruction if unchanged.
    /// </summary>
    protected virtual Instruction VisitInstruction(Instruction instruction) => instruction;

    /// <summary>Called after all types in the module have been visited.</summary>
    internal virtual void CompleteVisit()
    {
    }

    /// <summary>
    /// Recomputes instruction offsets and widens any now-out-of-range short branches. Must be run
    /// after inserting or replacing instructions so the written method body is valid.
    /// </summary>
    internal static void FixInstructionOffsets(MethodDefinition method)
    {
        method.Body.SimplifyMacros();
        method.Body.OptimizeMacros();
    }

    /// <summary>
    /// Replaces <paramref name="instruction"/> with <paramref name="replacement"/>, re-pointing any
    /// branch targets and exception-handler boundaries that referenced the old instruction so the
    /// method body stays valid.
    /// </summary>
    protected void Replace(Instruction instruction, Instruction replacement)
    {
        IsMethodBodyModified = true;
        Processor!.Replace(instruction, replacement);

        MethodBody body = Processor.Body;
        if (body.HasExceptionHandlers)
        {
            foreach (ExceptionHandler handler in body.ExceptionHandlers)
            {
                if (handler.TryStart == instruction)
                {
                    handler.TryStart = replacement;
                }

                if (handler.TryEnd == instruction)
                {
                    handler.TryEnd = replacement;
                }

                if (handler.FilterStart == instruction)
                {
                    handler.FilterStart = replacement;
                }

                if (handler.HandlerStart == instruction)
                {
                    handler.HandlerStart = replacement;
                }

                if (handler.HandlerEnd == instruction)
                {
                    handler.HandlerEnd = replacement;
                }
            }
        }

        foreach (Instruction current in body.Instructions)
        {
            if (current.Operand is Instruction target && target == instruction)
            {
                current.Operand = replacement;
            }
            else if (current.Operand is Instruction[] targets)
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    if (targets[i] == instruction)
                    {
                        targets[i] = replacement;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Inserts instructions immediately before <paramref name="target"/> and redirects incoming
    /// control-flow and exception-region boundaries to the first injected instruction.
    /// </summary>
    protected void InsertBeforeAndRetarget(Instruction target, IReadOnlyList<Instruction> instructions)
    {
        if (instructions.Count == 0)
        {
            return;
        }

        IsMethodBodyModified = true;
        Instruction first = instructions[0];
        foreach (Instruction instruction in instructions)
        {
            Processor!.InsertBefore(target, instruction);
        }

        MethodBody body = Processor!.Body;
        if (body.HasExceptionHandlers)
        {
            foreach (ExceptionHandler handler in body.ExceptionHandlers)
            {
                if (handler.TryStart == target)
                {
                    handler.TryStart = first;
                }

                if (handler.TryEnd == target)
                {
                    handler.TryEnd = first;
                }

                if (handler.FilterStart == target)
                {
                    handler.FilterStart = first;
                }

                if (handler.HandlerStart == target)
                {
                    handler.HandlerStart = first;
                }

                if (handler.HandlerEnd == target)
                {
                    handler.HandlerEnd = first;
                }
            }
        }

        foreach (Instruction current in body.Instructions)
        {
            if (instructions.Contains(current))
            {
                continue;
            }

            if (current.Operand is Instruction branchTarget && branchTarget == target)
            {
                current.Operand = first;
            }
            else if (current.Operand is Instruction[] targets)
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    if (targets[i] == target)
                    {
                        targets[i] = first;
                    }
                }
            }
        }
    }
}
