// This file is a design-level adaptation of Microsoft Coyote's MIT-licensed collection rewriting
// coverage in Source/Test/Rewriting/Types/Collections and TypeRewritingPass.cs:
//
//   Copyright (c) Microsoft Corporation.
//   Licensed under the MIT License.
//
// Clockwork preserves the concrete .NET 11 collection types and injects receiver-first access calls
// at direct concrete member invocations instead of substituting wrapper subclasses.

using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Runtime.Policy;
using Orleans.Testing.Simulation.Runtime.Racing;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>Instruments direct accesses to supported mutable and concurrent collection types.</summary>
internal sealed class CollectionAccessRewritingPass : RewritePass
{
    private static readonly HashSet<string> MutableCollections =
    [
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.HashSet`1",
    ];

    private static readonly HashSet<string> ConcurrentCollections =
    [
        "System.Collections.Concurrent.ConcurrentBag`1",
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "System.Collections.Concurrent.ConcurrentQueue`1",
        "System.Collections.Concurrent.ConcurrentStack`1",
    ];

    private static readonly HashSet<string> WriteMemberNames =
    [
        "set_Item",
        "Add",
        "AddRange",
        "AddOrUpdate",
        "Clear",
        "Dequeue",
        "Enqueue",
        "EnsureCapacity",
        "ExceptWith",
        "GetOrAdd",
        "IntersectWith",
        "Insert",
        "InsertRange",
        "Pop",
        "Push",
        "Remove",
        "RemoveAll",
        "RemoveAt",
        "RemoveRange",
        "RemoveWhere",
        "Reverse",
        "Sort",
        "SymmetricExceptWith",
        "TrimExcess",
        "TryAdd",
        "TryDequeue",
        "TryPop",
        "TryRemove",
        "TryTake",
        "TryUpdate",
        "UnionWith",
    ];

    private readonly MethodReference _readCollection;
    private readonly MethodReference _writeCollection;
    private readonly MethodReference _concurrentCollection;

    public CollectionAccessRewritingPass(RewriteSession session)
        : base(session)
    {
        _readCollection = Import(session.TargetModule, nameof(RaceInstrumentation.ReadCollection));
        _writeCollection = Import(session.TargetModule, nameof(RaceInstrumentation.WriteCollection));
        _concurrentCollection = Import(
            session.TargetModule,
            nameof(RaceInstrumentation.InterleaveConcurrentCollection));
    }

    protected override Instruction VisitInstruction(Instruction instruction)
    {
        if (Method is null ||
            instruction.OpCode.Code is not (Code.Call or Code.Callvirt) ||
            instruction.Operand is not MethodReference called ||
            !called.HasThis)
        {
            return instruction;
        }

        string typeName = called.DeclaringType.GetElementType().FullName;
        bool isMutable = MutableCollections.Contains(typeName);
        bool isConcurrent = ConcurrentCollections.Contains(typeName);
        if (!isMutable && !isConcurrent)
        {
            return instruction;
        }

        for (Instruction? prefix = instruction.Previous;
             prefix is not null && prefix.OpCode.OpCodeType == OpCodeType.Prefix;
             prefix = prefix.Previous)
        {
            if (prefix.OpCode == OpCodes.Tail)
            {
                return instruction;
            }
        }

        VariableDefinition receiver = AddVariable(Inflate(called.DeclaringType, called));
        var parameters = new VariableDefinition[called.Parameters.Count];
        var before = new List<Instruction>();
        for (int index = called.Parameters.Count - 1; index >= 0; index--)
        {
            VariableDefinition parameter = AddVariable(Inflate(called.Parameters[index].ParameterType, called));
            parameters[index] = parameter;
            before.Add(Instruction.Create(OpCodes.Stloc, parameter));
        }

        before.Add(Instruction.Create(OpCodes.Stloc, receiver));
        before.Add(Instruction.Create(OpCodes.Ldloc, receiver));
        foreach (VariableDefinition parameter in parameters)
        {
            before.Add(Instruction.Create(OpCodes.Ldloc, parameter));
        }

        InsertBeforeAndRetarget(instruction, before);

        MethodReference provider = isConcurrent
            ? _concurrentCollection
            : IsWrite(called.Name) ? _writeCollection : _readCollection;
        var after = new List<Instruction> { Instruction.Create(OpCodes.Ldloc, receiver) };
        string member = $"{typeName}::{called.Name}";
        after.Add(Instruction.Create(OpCodes.Ldstr, member));
        AppendSourceMetadata(after, instruction);
        after.Add(Instruction.Create(OpCodes.Call, provider));
        InsertAfter(instruction, after);
        Record(instruction, member, provider);
        return after[^1];
    }

    private void InsertAfter(Instruction target, IReadOnlyList<Instruction> instructions)
    {
        Instruction cursor = target;
        foreach (Instruction instruction in instructions)
        {
            Processor!.InsertAfter(cursor, instruction);
            cursor = instruction;
        }

        IsMethodBodyModified = true;
    }

    private VariableDefinition AddVariable(TypeReference type)
    {
        Method!.Body.InitLocals = true;
        var variable = new VariableDefinition(Module!.ImportReference(type));
        Method.Body.Variables.Add(variable);
        return variable;
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
            "clockwork.race-exploration.collection-access",
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

    private static bool IsWrite(string name) =>
        WriteMemberNames.Contains(name) ||
        name.StartsWith("set_", StringComparison.Ordinal);

    private static TypeReference Inflate(TypeReference type, MethodReference owner) =>
        TypeReferenceStructure.Inflate(type, owner);

    private static MethodReference Import(ModuleDefinition module, string name)
    {
        System.Reflection.MethodInfo method = typeof(RaceInstrumentation).GetMethods()
            .Single(candidate => candidate.Name == name);
        return module.ImportReference(method);
    }
}
