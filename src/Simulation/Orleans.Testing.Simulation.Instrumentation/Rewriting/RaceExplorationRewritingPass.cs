// Portions of this file (the selection of instance field and brtrue/brfalse instructions, placement
// before a volatile prefix, and constructor/property-accessor exclusions) are adapted from Microsoft
// Coyote's Source/Test/Rewriting/Passes/Rewriting/MemoryAccessRewritingPass.cs, licensed under MIT:
//
//   Copyright (c) Microsoft Corporation.
//   Licensed under the MIT License.
//
// Clockwork-specific changes add static fields, array elements, safely classified indirect accesses,
// exact source/IL metadata, stack-preserving receiver/index capture, manifest entries, async/iterator
// state-machine handling, and exception-region/branch-target retargeting.

using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Runtime.Policy;
using Orleans.Testing.Simulation.Runtime.Racing;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>Injects opt-in scheduling points at supported memory and conditional branch instructions.</summary>
internal sealed class RaceExplorationRewritingPass : RewritePass
{
    private readonly MethodReference _readInstance;
    private readonly MethodReference _writeInstance;
    private readonly MethodReference _readStatic;
    private readonly MethodReference _writeStatic;
    private readonly MethodReference _readArray;
    private readonly MethodReference _writeArray;
    private readonly MethodReference _untrackedMemory;
    private readonly MethodReference _controlFlow;

    public RaceExplorationRewritingPass(RewriteSession session)
        : base(session)
    {
        ModuleDefinition module = session.TargetModule;
        _readInstance = Import(module, nameof(RaceInstrumentation.ReadInstance));
        _writeInstance = Import(module, nameof(RaceInstrumentation.WriteInstance));
        _readStatic = Import(module, nameof(RaceInstrumentation.ReadStaticField));
        _writeStatic = Import(module, nameof(RaceInstrumentation.WriteStaticField));
        _readArray = Import(module, nameof(RaceInstrumentation.ReadArray));
        _writeArray = Import(module, nameof(RaceInstrumentation.WriteArray));
        _untrackedMemory = Import(module, nameof(RaceInstrumentation.InterleaveUntrackedMemory));
        _controlFlow = Import(module, nameof(RaceInstrumentation.InterleaveControlFlow));
    }

    internal override void VisitMethod(MethodDefinition method)
    {
        // Coyote excludes constructors and property boilerplate. Clockwork follows that evidence while
        // still visiting MoveNext methods: generated state-machine fields receive schedule-only points,
        // and user-object/closure fields reached from the state machine retain full identity tracking.
        if (method.IsConstructor || method.IsGetter || method.IsSetter)
        {
            return;
        }

        base.VisitMethod(method);
    }

    protected override Instruction VisitInstruction(Instruction instruction)
    {
        if (Method is null || Processor is null)
        {
            return instruction;
        }

        switch (instruction.OpCode.Code)
        {
            case Code.Ldfld:
                InstrumentInstanceField(instruction, (FieldReference)instruction.Operand, isWrite: false);
                break;
            case Code.Stfld:
                InstrumentInstanceField(instruction, (FieldReference)instruction.Operand, isWrite: true);
                break;
            case Code.Ldsfld:
                InstrumentStaticField(instruction, (FieldReference)instruction.Operand, isWrite: false);
                break;
            case Code.Stsfld:
                InstrumentStaticField(instruction, (FieldReference)instruction.Operand, isWrite: true);
                break;
            case Code.Ldflda:
            case Code.Ldsflda:
                InstrumentUntracked(instruction, ((FieldReference)instruction.Operand).FullName + " address");
                break;
            case Code.Ldelem_Any:
            case Code.Ldelem_I:
            case Code.Ldelem_I1:
            case Code.Ldelem_I2:
            case Code.Ldelem_I4:
            case Code.Ldelem_I8:
            case Code.Ldelem_R4:
            case Code.Ldelem_R8:
            case Code.Ldelem_Ref:
            case Code.Ldelem_U1:
            case Code.Ldelem_U2:
            case Code.Ldelem_U4:
                InstrumentArrayRead(instruction);
                break;
            case Code.Stelem_Any:
            case Code.Stelem_I:
            case Code.Stelem_I1:
            case Code.Stelem_I2:
            case Code.Stelem_I4:
            case Code.Stelem_I8:
            case Code.Stelem_R4:
            case Code.Stelem_R8:
            case Code.Stelem_Ref:
                InstrumentArrayWrite(instruction);
                break;
            case Code.Ldelema:
            case Code.Ldind_I:
            case Code.Ldind_I1:
            case Code.Ldind_I2:
            case Code.Ldind_I4:
            case Code.Ldind_I8:
            case Code.Ldind_R4:
            case Code.Ldind_R8:
            case Code.Ldind_Ref:
            case Code.Ldind_U1:
            case Code.Ldind_U2:
            case Code.Ldind_U4:
            case Code.Ldobj:
            case Code.Stind_I:
            case Code.Stind_I1:
            case Code.Stind_I2:
            case Code.Stind_I4:
            case Code.Stind_I8:
            case Code.Stind_R4:
            case Code.Stind_R8:
            case Code.Stind_Ref:
            case Code.Stobj:
            case Code.Cpobj:
            case Code.Initobj:
                InstrumentUntracked(instruction, instruction.OpCode.Code.ToString());
                break;
            case Code.Brfalse:
            case Code.Brfalse_S:
            case Code.Brtrue:
            case Code.Brtrue_S:
                InstrumentControlFlow(instruction);
                break;
        }

        return instruction;
    }

    private void InstrumentInstanceField(Instruction instruction, FieldReference field, bool isWrite)
    {
        if (field.DeclaringType.IsValueType || HasPrefix(instruction, OpCodes.Volatile))
        {
            InstrumentUntracked(instruction, field.FullName);
            return;
        }

        List<Instruction> injected = [];
        if (isWrite)
        {
            VariableDefinition value = AddVariable(Module!.ImportReference(InflateFieldType(field.FieldType, field)));
            injected.Add(Instruction.Create(OpCodes.Stloc, value));
            injected.Add(Instruction.Create(OpCodes.Dup));
            AppendMemberMetadata(injected, field.FullName, instruction);
            injected.Add(Instruction.Create(OpCodes.Call, _writeInstance));
            injected.Add(Instruction.Create(OpCodes.Ldloc, value));
        }
        else
        {
            injected.Add(Instruction.Create(OpCodes.Dup));
            AppendMemberMetadata(injected, field.FullName, instruction);
            injected.Add(Instruction.Create(OpCodes.Call, _readInstance));
        }

        InsertAtAccess(instruction, injected);
        Record(instruction, field.FullName, isWrite ? _writeInstance : _readInstance);
    }

    private void InstrumentStaticField(Instruction instruction, FieldReference field, bool isWrite)
    {
        if (HasPrefix(instruction, OpCodes.Volatile))
        {
            InstrumentUntracked(instruction, field.FullName + " (volatile)");
            return;
        }

        List<Instruction> injected = [];
        injected.Add(Instruction.Create(OpCodes.Ldtoken, Module!.ImportReference(field.DeclaringType)));
        AppendMemberMetadata(injected, field.FullName, instruction);
        MethodReference target = isWrite ? _writeStatic : _readStatic;
        injected.Add(Instruction.Create(OpCodes.Call, target));
        InsertAtAccess(instruction, injected);
        Record(instruction, field.FullName, target);
    }

    private void InstrumentArrayRead(Instruction instruction)
    {
        VariableDefinition index = AddVariable(Module!.TypeSystem.IntPtr);
        List<Instruction> injected =
        [
            Instruction.Create(OpCodes.Conv_I),
            Instruction.Create(OpCodes.Stloc, index),
            Instruction.Create(OpCodes.Dup),
            Instruction.Create(OpCodes.Ldloc, index),
            Instruction.Create(OpCodes.Conv_I8),
        ];
        AppendSourceMetadata(injected, instruction);
        injected.Add(Instruction.Create(OpCodes.Call, _readArray));
        injected.Add(Instruction.Create(OpCodes.Ldloc, index));
        InsertAtAccess(instruction, injected);
        Record(instruction, "array element", _readArray);
    }

    private void InstrumentArrayWrite(Instruction instruction)
    {
        VariableDefinition value = AddVariable(ArrayStoreValueType(instruction));
        VariableDefinition index = AddVariable(Module!.TypeSystem.IntPtr);
        List<Instruction> injected =
        [
            Instruction.Create(OpCodes.Stloc, value),
            Instruction.Create(OpCodes.Conv_I),
            Instruction.Create(OpCodes.Stloc, index),
            Instruction.Create(OpCodes.Dup),
            Instruction.Create(OpCodes.Ldloc, index),
            Instruction.Create(OpCodes.Conv_I8),
        ];
        AppendSourceMetadata(injected, instruction);
        injected.Add(Instruction.Create(OpCodes.Call, _writeArray));
        injected.Add(Instruction.Create(OpCodes.Ldloc, index));
        injected.Add(Instruction.Create(OpCodes.Ldloc, value));
        InsertAtAccess(instruction, injected);
        Record(instruction, "array element", _writeArray);
    }

    private void InstrumentUntracked(Instruction instruction, string description)
    {
        List<Instruction> injected = [Instruction.Create(OpCodes.Ldstr, description)];
        AppendSourceMetadata(injected, instruction);
        injected.Add(Instruction.Create(OpCodes.Call, _untrackedMemory));
        InsertAtAccess(instruction, injected);
        Record(instruction, description, _untrackedMemory);
    }

    private void InstrumentControlFlow(Instruction instruction)
    {
        List<Instruction> injected = [];
        AppendSourceMetadata(injected, instruction);
        injected.Add(Instruction.Create(OpCodes.Call, _controlFlow));
        InsertBeforeAndRetarget(instruction, injected);
        Record(instruction, "conditional branch", _controlFlow);
    }

    private void InsertAtAccess(Instruction instruction, IReadOnlyList<Instruction> injected)
    {
        Instruction anchor = instruction;
        while (anchor.Previous is { } prefix && prefix.OpCode.OpCodeType == OpCodeType.Prefix)
        {
            anchor = prefix;
        }

        InsertBeforeAndRetarget(anchor, injected);
    }

    private static bool HasPrefix(Instruction instruction, OpCode expected)
    {
        for (Instruction? prefix = instruction.Previous;
             prefix is not null && prefix.OpCode.OpCodeType == OpCodeType.Prefix;
             prefix = prefix.Previous)
        {
            if (prefix.OpCode == expected)
            {
                return true;
            }
        }

        return false;
    }

    private VariableDefinition AddVariable(TypeReference type)
    {
        Method!.Body.InitLocals = true;
        var variable = new VariableDefinition(type);
        Method.Body.Variables.Add(variable);
        return variable;
    }

    private TypeReference ArrayStoreValueType(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Stelem_I1 or Code.Stelem_I2 or Code.Stelem_I4 => Module!.TypeSystem.Int32,
        Code.Stelem_I8 => Module!.TypeSystem.Int64,
        Code.Stelem_R4 => Module!.TypeSystem.Single,
        Code.Stelem_R8 => Module!.TypeSystem.Double,
        Code.Stelem_I => Module!.TypeSystem.IntPtr,
        Code.Stelem_Ref => Module!.TypeSystem.Object,
        Code.Stelem_Any => Module!.ImportReference((TypeReference)instruction.Operand),
        _ => throw new InvalidOperationException($"Unsupported array store opcode '{instruction.OpCode}'."),
    };

    private static TypeReference InflateFieldType(TypeReference type, FieldReference field) =>
        TypeReferenceStructure.Inflate(type, field);

    private void AppendMemberMetadata(List<Instruction> instructions, string member, Instruction site)
    {
        instructions.Add(Instruction.Create(OpCodes.Ldstr, member));
        AppendSourceMetadata(instructions, site);
    }

    private void AppendSourceMetadata(List<Instruction> instructions, Instruction site)
    {
        RewriteSession.TryGetSequencePoint(Method!, site, out string? file, out int line);
        instructions.Add(Instruction.Create(OpCodes.Ldstr, CecilNames.FullyQualifiedMethodName(Method!)));
        instructions.Add(Instruction.Create(OpCodes.Ldc_I4, site.Offset));
        instructions.Add(file is null ? Instruction.Create(OpCodes.Ldnull) : Instruction.Create(OpCodes.Ldstr, file));
        instructions.Add(Instruction.Create(OpCodes.Ldc_I4, line));
    }

    private void Record(Instruction site, string target, MethodReference replacement)
    {
        RewriteSession.TryGetSequencePoint(Method!, site, out string? file, out int line);
        Session.AddTransformation(new ManifestTransformation(
            "clockwork.race-exploration.scheduling-point",
            RewriteOperationKind.InjectSchedulingPoint,
            TransformationOutcome.Transformed,
            SimulationApiPolicy.Controlled,
            target,
            replacement.FullName,
            CecilNames.FullyQualifiedMethodName(Method!),
            site.Offset,
            file,
            line));
    }

    private static MethodReference Import(ModuleDefinition module, string name)
    {
        System.Reflection.MethodInfo method = typeof(RaceInstrumentation).GetMethods()
            .Single(candidate => candidate.Name == name);
        return module.ImportReference(method);
    }
}
