using System.Runtime.Loader;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests for the places a whole-type substitution reaches beyond member signatures, using the
/// <see cref="System.Threading.Barrier"/> and <see cref="System.Threading.CountdownEvent"/> substitutions: a base type
/// and an interface composed from a substituted type, an explicit implementation of such an interface, a generic
/// constraint on a type or a method, and the <c>constrained.</c> operand of a <c>foreach</c> over a list of a
/// substituted type. Each must name the replacement, or the rewritten assembly fails to load or verify. A type derived
/// from a substituted type cannot be retargeted and fails the rewrite.
/// </summary>
public sealed class SubstitutedTypeShapeGoldenTests
{
    private const string BclBarrier = "System.Threading.Barrier";
    private const string BclCountdownEvent = "System.Threading.CountdownEvent";
    private const string ControlledBarrier = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledBarrier";
    private const string ControlledCountdownEvent = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledCountdownEvent";

    internal const string Fixture = """
        using System.Collections;
        using System.Collections.Generic;
        using System.Threading;

        namespace Fx
        {
            public sealed class Barriers : List<Barrier>
            {
                public int Participants()
                {
                    int total = 0;
                    foreach (Barrier barrier in this)
                    {
                        total += barrier.ParticipantCount;
                    }

                    return total;
                }
            }

            public sealed class Registry : IEnumerable<CountdownEvent>
            {
                private readonly List<CountdownEvent> _events = new List<CountdownEvent>();

                public void Add(CountdownEvent countdown) => _events.Add(countdown);

                IEnumerator<CountdownEvent> IEnumerable<CountdownEvent>.GetEnumerator() => _events.GetEnumerator();

                IEnumerator IEnumerable.GetEnumerator() => _events.GetEnumerator();
            }

            public sealed class Holder<T>
                where T : IEnumerable<Barrier>
            {
                public Holder(T items) => Items = items;

                public T Items { get; }

                public int Participants()
                {
                    int total = 0;
                    foreach (Barrier barrier in Items)
                    {
                        total += barrier.ParticipantCount;
                    }

                    return total;
                }
            }

            public static class Shapes
            {
                public static int Sum<T>(T items)
                    where T : IEnumerable<Barrier>
                {
                    int total = 0;
                    foreach (Barrier barrier in items)
                    {
                        total += barrier.ParticipantCount;
                    }

                    return total;
                }

                public static int Participants<T>(T barrier)
                    where T : Barrier => barrier.ParticipantCount;

                public static string Run()
                {
                    var barriers = new Barriers { new Barrier(2), new Barrier(3) };
                    var registry = new Registry();
                    registry.Add(new CountdownEvent(4));
                    int initial = 0;
                    foreach (CountdownEvent countdown in registry)
                    {
                        initial += countdown.InitialCount;
                    }

                    return string.Join(
                        ",",
                        barriers.Participants(),
                        new Holder<Barriers>(barriers).Participants(),
                        Sum(barriers),
                        Participants(barriers[1]),
                        initial);
                }
            }
        }
        """;

    private const string DerivedFixture = """
        using System.Threading;

        namespace Fx
        {
            public sealed class Phased : Barrier
            {
                public Phased()
                    : base(1)
                {
                }
            }
        }
        """;

    private static string RuntimeAssemblyPath =>
        typeof(Orleans.Testing.Simulation.Shims.System.Threading.ControlledBarrier).Assembly.Location;

    [Fact]
    public void ComposedBaseTypesInterfacesConstraintsAndConstrainedOperandsAreRetargeted()
    {
        using var context = RewriteTestContext.Create();
        RewriteResult result = Rewrite(context, "Fx.SubstitutedShapes", Fixture);
        result.EnsureSuccess();

        string outputPath = Path.Combine(context.Directory, "Fx.SubstitutedShapes.rewritten.dll");
        using ModuleDefinition module = context.LoadModule(outputPath);

        Assert.Equal($"System.Collections.Generic.List`1<{ControlledBarrier}>", module.GetType("Fx.Barriers").BaseType.FullName);

        TypeDefinition registry = module.GetType("Fx.Registry");
        Assert.Contains(
            registry.Interfaces,
            implementation => implementation.InterfaceType.FullName ==
                $"System.Collections.Generic.IEnumerable`1<{ControlledCountdownEvent}>");
        MethodReference explicitImplementation = Assert.Single(
            registry.Methods.SelectMany(method => method.Overrides),
            reference => reference.DeclaringType.FullName.StartsWith("System.Collections.Generic.IEnumerable`1", StringComparison.Ordinal));
        Assert.Equal(
            $"System.Collections.Generic.IEnumerable`1<{ControlledCountdownEvent}>",
            explicitImplementation.DeclaringType.FullName);

        Assert.Equal(
            $"System.Collections.Generic.IEnumerable`1<{ControlledBarrier}>",
            Assert.Single(Assert.Single(module.GetType("Fx.Holder`1").GenericParameters).Constraints).ConstraintType.FullName);
        TypeDefinition shapes = module.GetType("Fx.Shapes");
        Assert.Equal(
            $"System.Collections.Generic.IEnumerable`1<{ControlledBarrier}>",
            Assert.Single(Assert.Single(shapes.Methods.Single(method => method.Name == "Sum").GenericParameters).Constraints).ConstraintType.FullName);
        Assert.Equal(
            ControlledBarrier,
            Assert.Single(Assert.Single(shapes.Methods.Single(method => method.Name == "Participants").GenericParameters).Constraints).ConstraintType.FullName);

        Instruction[] constrained =
        [
            .. module.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction => instruction.OpCode.Code == Code.Constrained),
        ];
        Assert.Contains(constrained, instruction =>
            ((TypeReference)instruction.Operand).FullName == $"System.Collections.Generic.List`1/Enumerator<{ControlledBarrier}>");

        Assert.DoesNotContain(ReferencedTypeNames(module), name =>
            name.Contains(BclBarrier, StringComparison.Ordinal) || name.Contains(BclCountdownEvent, StringComparison.Ordinal));
        Assert.All(ReferencedMembers(module), member => Assert.True(
            member.Resolve() is not null,
            $"Rewritten reference '{member.FullName}' does not bind."));

        // Loading every type checks that the mapped base types, interface implementations, and constraints agree.
        var loadContext = new AssemblyLoadContext(nameof(ComposedBaseTypesInterfacesConstraintsAndConstrainedOperandsAreRetargeted), isCollectible: true);
        try
        {
            using FileStream image = File.OpenRead(outputPath);
            Type[] loaded = loadContext.LoadFromStream(image).GetTypes();
            Type holder = loaded.Single(type => type.FullName == "Fx.Holder`1");
            Assert.Equal(
                typeof(IEnumerable<Orleans.Testing.Simulation.Shims.System.Threading.ControlledBarrier>),
                Assert.Single(holder.GetGenericArguments()[0].GetGenericParameterConstraints()));
            Assert.Equal(
                typeof(List<Orleans.Testing.Simulation.Shims.System.Threading.ControlledBarrier>),
                loaded.Single(type => type.FullName == "Fx.Barriers").BaseType);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void TypeDerivedFromASubstitutedTypeFailsTheRewrite()
    {
        using var context = RewriteTestContext.Create();

        RewriteResult result = Rewrite(context, "Fx.DerivedBarrier", DerivedFixture);

        Assert.False(result.Succeeded);
        Assert.False(result.WasWritten);
        RewriteDiagnostic error = Assert.Single(
            result.Errors,
            diagnostic => diagnostic.Message.Contains("derives from", StringComparison.Ordinal));
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.Equal("Fx.Phased", error.Method);
        Assert.Contains("clockwork.barrier.type", error.Message, StringComparison.Ordinal);
    }

    private static RewriteResult Rewrite(RewriteTestContext context, string assemblyName, string source)
    {
        string fixturePath = context.CompileFixture(assemblyName, source);
        string outputPath = Path.Combine(context.Directory, assemblyName + ".rewritten.dll");
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [RuntimeAssemblyPath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(RuntimeAssemblyPath)!],
        };

        RewriteRuleSet ruleSet = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);
        return RewriteEngine.Rewrite(new RewriteRequest(fixturePath, outputPath, ruleSet, options));
    }

    private static IEnumerable<string> ReferencedTypeNames(ModuleDefinition module)
    {
        foreach (TypeDefinition type in module.GetTypes())
        {
            if (type.BaseType is not null)
            {
                yield return type.BaseType.FullName;
            }

            foreach (InterfaceImplementation implementation in type.Interfaces)
            {
                yield return implementation.InterfaceType.FullName;
            }

            foreach (GenericParameter parameter in type.GenericParameters.Concat(type.Methods.SelectMany(method => method.GenericParameters)))
            {
                foreach (GenericParameterConstraint constraint in parameter.Constraints)
                {
                    yield return constraint.ConstraintType.FullName;
                }
            }

            foreach (FieldDefinition field in type.Fields)
            {
                yield return field.FieldType.FullName;
            }

            foreach (MethodDefinition method in type.Methods)
            {
                yield return method.ReturnType.FullName;
                foreach (ParameterDefinition parameter in method.Parameters)
                {
                    yield return parameter.ParameterType.FullName;
                }

                foreach (MethodReference implemented in method.Overrides)
                {
                    yield return implemented.FullName;
                }

                if (!method.HasBody)
                {
                    continue;
                }

                foreach (VariableDefinition variable in method.Body.Variables)
                {
                    yield return variable.VariableType.FullName;
                }

                foreach (Instruction instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is MemberReference member)
                    {
                        yield return member.FullName;
                    }
                }
            }
        }
    }

    private static List<MemberReference> ReferencedMembers(ModuleDefinition module)
    {
        MethodDefinition[] methods = [.. module.GetTypes().SelectMany(type => type.Methods)];
        return
        [
            .. methods.SelectMany(method => method.Overrides),
            .. methods
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Select(instruction => instruction.Operand)
                .OfType<MemberReference>()
                .Where(member => member is MethodReference or FieldReference),
        ];
    }
}
