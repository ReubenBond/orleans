using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Rewriting;

public sealed class TypeReferenceStructureTests
{
    [Fact]
    public void ContractValidationTerminatesForSelfReferentialGenericGraphs()
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("recursive", ModuleKind.Dll);
        TypeReference open = Type(module, "Recursive`2");
        GenericInstanceType targetType = RecursiveInstance(open, module.TypeSystem.Int32);
        GenericInstanceType replacementType = RecursiveInstance(open, module.TypeSystem.Int32);
        MethodReference target = StaticMethod(module, "Target", targetType);
        MethodReference replacement = StaticMethod(module, "Replacement", replacementType);

        bool valid = ReplacementContractValidator.TryValidate(
            RewriteOperationKind.RedirectCall,
            target,
            replacement,
            static (_, _) => false,
            out string? error);

        Assert.True(valid, error);
    }

    [Fact]
    public void IncompatibleSelfReferentialGenericGraphsFailDeterministically()
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("recursive", ModuleKind.Dll);
        TypeReference open = Type(module, "Recursive`2");
        GenericInstanceType targetType = RecursiveInstance(open, module.TypeSystem.Int32);
        GenericInstanceType replacementType = RecursiveInstance(open, module.TypeSystem.String);
        MethodReference target = StaticMethod(module, "Target", targetType);
        MethodReference replacement = StaticMethod(module, "Replacement", replacementType);

        bool valid = ReplacementContractValidator.TryValidate(
            RewriteOperationKind.RedirectCall,
            target,
            replacement,
            static (_, _) => false,
            out string? error);

        Assert.False(valid);
        Assert.Equal(
            "Replacement 'Fx.Recursive`2<#0,System.String> Fx.Contract::Replacement()' returns " +
            "'Fx.Recursive`2<#0,System.String>', but target 'Fx.Recursive`2<#0,System.Int32> " +
            "Fx.Contract::Target()' requires 'Fx.Recursive`2<#0,System.Int32>'.",
            error);
    }

    [Fact]
    public void StructuralComparisonHandlesCyclicSpecificationsAndFunctionPointers()
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("recursive", ModuleKind.Dll);
        FunctionPointerType left = FunctionPointer(module, arrayRank: 2);
        FunctionPointerType right = FunctionPointer(module, arrayRank: 2);
        FunctionPointerType different = FunctionPointer(module, arrayRank: 1);

        Assert.True(TypeReferenceStructure.AreEquivalent(left, null, right, null));
        Assert.False(TypeReferenceStructure.AreEquivalent(left, null, different, null));
    }

    [Fact]
    public void InflationMemoizesCyclesAndRespectsGenericParameterOwners()
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("recursive", ModuleKind.Dll);
        var ownerType = new TypeDefinition("Fx", "Owner`1", TypeAttributes.Public);
        var ownerParameter = new GenericParameter("TOwner", ownerType);
        ownerType.GenericParameters.Add(ownerParameter);
        var foreignType = new TypeDefinition("Fx", "Foreign`1", TypeAttributes.Public);
        var foreignParameter = new GenericParameter("TForeign", foreignType);
        foreignType.GenericParameters.Add(foreignParameter);
        var nested = new GenericInstanceType(Type(module, "Nested`1"));
        nested.GenericArguments.Add(foreignParameter);
        var closedOwner = new GenericInstanceType(ownerType);
        closedOwner.GenericArguments.Add(nested);
        var owner = new MethodReference("Read", ownerParameter, closedOwner);
        GenericInstanceType cycle = RecursiveInstance(Type(module, "Recursive`2"), ownerParameter);

        TypeReference inflatedParameter = TypeReferenceStructure.Inflate(ownerParameter, owner);
        var inflatedCycle = Assert.IsType<GenericInstanceType>(
            TypeReferenceStructure.Inflate(cycle, owner),
            exactMatch: true);

        var inflatedNested = Assert.IsType<GenericInstanceType>(inflatedParameter, exactMatch: true);
        Assert.Same(foreignParameter, Assert.Single(inflatedNested.GenericArguments));
        Assert.Same(inflatedCycle, inflatedCycle.GenericArguments[0]);
        var nestedInCycle = Assert.IsType<GenericInstanceType>(
            inflatedCycle.GenericArguments[1],
            exactMatch: true);
        Assert.Same(foreignParameter, Assert.Single(nestedInCycle.GenericArguments));
    }

    private static FunctionPointerType FunctionPointer(ModuleDefinition module, int arrayRank)
    {
        TypeReference modifier = Type(module, "Modifier");
        TypeReference open = Type(module, "Recursive`2");
        GenericInstanceType recursive = RecursiveInstance(open, module.TypeSystem.Int32);
        var functionPointer = new FunctionPointerType
        {
            CallingConvention = MethodCallingConvention.C,
            ReturnType = new PointerType(recursive),
        };
        functionPointer.Parameters.Add(new ParameterDefinition(
            new ByReferenceType(new ArrayType(recursive, arrayRank))));
        functionPointer.Parameters.Add(new ParameterDefinition(
            new RequiredModifierType(modifier, recursive)));
        functionPointer.Parameters.Add(new ParameterDefinition(
            new OptionalModifierType(modifier, new PinnedType(new SentinelType(recursive)))));
        return functionPointer;
    }

    private static GenericInstanceType RecursiveInstance(TypeReference open, TypeReference leaf)
    {
        var instance = new GenericInstanceType(open);
        instance.GenericArguments.Add(instance);
        instance.GenericArguments.Add(leaf);
        return instance;
    }

    private static MethodReference StaticMethod(ModuleDefinition module, string name, TypeReference returnType) =>
        new(name, returnType, Type(module, "Contract"));

    private static TypeReference Type(ModuleDefinition module, string name) =>
        new("Fx", name, module, module.TypeSystem.CoreLibrary);
}
