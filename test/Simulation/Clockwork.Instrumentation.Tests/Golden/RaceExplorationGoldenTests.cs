using System.Reflection;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Racing;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>Golden coverage for the opt-in fine-grained race-exploration rewrite pass.</summary>
public sealed class RaceExplorationGoldenTests
{
    private const string Fixture = """
        using System;
        using System.Threading.Tasks;

        namespace Fx;

        public sealed class Subject
        {
            public int Value;
            public volatile int VolatileValue;
            public static int StaticValue;

            public Subject() => Value = 1;

            public int Property
            {
                get => Value;
                set => Value = value;
            }

            public int Run(int[] values, bool increment)
            {
                Value++;
                StaticValue = Value;
                VolatileValue = StaticValue;
                values[0] = Value;
                int result = values[0];
                if (increment)
                {
                    result++;
                }

                return result;
            }

            public async Task<int> AsyncRun()
            {
                await Task.Yield();
                return Value;
            }

            public Func<int> Capture() => () => Value;
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RaceModeInstrumentsSupportedMemoryAndControlFlowInDebugAndRelease(bool optimize)
    {
        using var context = RewriteTestContext.Create();
        string input = FixtureCompiler.Compile(
            "Fx.Race." + optimize,
            Fixture,
            context.Directory,
            FixtureSymbols.PortableFile,
            optimize);
        string output = Path.Combine(context.Directory, "Fx.Race.rewritten." + optimize + ".dll");

        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [typeof(RaceInstrumentation).Assembly.Location],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(typeof(RaceInstrumentation).Assembly.Location)!],
            InstrumentRaceExploration = true,
        };
        RewriteResult result = context.Rewrite(input, output, EmptyRules(), options);

        result.EnsureSuccess();
        var schedulingPoints = result.Manifest.Transformations
            .Where(t => t.RuleId == "clockwork.race-exploration.scheduling-point")
            .ToArray();
        Assert.All(schedulingPoints, transformation => Assert.True(transformation.ILOffset >= 0));
        Assert.All(
            schedulingPoints.Where(t => t.Method.Contains("Fx.Subject::Run", StringComparison.Ordinal)),
            transformation =>
            {
                Assert.NotNull(transformation.SourceFile);
                Assert.True(transformation.SourceLine > 0);
            });

        using ModuleDefinition module = context.LoadModule(output);
        MethodDefinition run = CecilInspect.GetMethod(module, "Fx.Subject", "Run");
        List<string> calls = CecilInspect.CallTargets(run);
        Assert.Contains(calls, target => target.Contains("RaceInstrumentation::ReadInstance", StringComparison.Ordinal));
        Assert.Contains(calls, target => target.Contains("RaceInstrumentation::WriteInstance", StringComparison.Ordinal));
        Assert.Contains(calls, target => target.Contains("RaceInstrumentation::ReadStatic", StringComparison.Ordinal));
        Assert.Contains(calls, target => target.Contains("RaceInstrumentation::WriteStatic", StringComparison.Ordinal));
        Assert.Contains(calls, target => target.Contains("RaceInstrumentation::ReadArray", StringComparison.Ordinal));
        Assert.Contains(calls, target => target.Contains("RaceInstrumentation::WriteArray", StringComparison.Ordinal));
        Assert.Contains(calls, target => target.Contains("RaceInstrumentation::InterleaveControlFlow", StringComparison.Ordinal));

        Instruction volatilePrefix = Assert.Single(run.Body.Instructions, i => i.OpCode == OpCodes.Volatile);
        Instruction? volatileSchedulingCall = run.Body.Instructions
            .TakeWhile(instruction => instruction != volatilePrefix)
            .Reverse()
            .FirstOrDefault(instruction =>
                instruction.OpCode.Code == Code.Call &&
                instruction.Operand is MethodReference reference &&
                reference.FullName.Contains("RaceInstrumentation", StringComparison.Ordinal));
        Assert.NotNull(volatileSchedulingCall);

        Assert.True(CecilInspect.AnyMethodCallsContaining(module, "RaceInstrumentation::ReadInstance"));
        Assert.True(CecilInspect.AnyMethodCallsContaining(module, "RaceInstrumentation::InterleaveUntrackedMemory"));
    }

    [Fact]
    public void ControlledModeInjectsNoFineGrainedSchedulingCalls()
    {
        using var context = RewriteTestContext.Create();
        string input = context.CompileFixture("Fx.Race.Controlled", Fixture);
        string output = Path.Combine(context.Directory, "Fx.Race.Controlled.rewritten.dll");

        RewriteResult result = context.Rewrite(input, output, EmptyRules(), new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [context.ShimPath],
            ReferenceSearchDirectories = [context.Directory],
        });

        result.EnsureSuccess();
        using ModuleDefinition module = context.LoadModule(output);
        Assert.False(CecilInspect.AnyMethodCallsContaining(module, "RaceInstrumentation"));
        using ModuleDefinition original = context.LoadModule(input);
        int originalInstructions = original.GetTypes().SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .Sum(method => method.Body.Instructions.Count);
        int rewrittenInstructions = module.GetTypes().SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .Sum(method => method.Body.Instructions.Count);
        Assert.Equal(originalInstructions, rewrittenInstructions);
        Assert.DoesNotContain(
            result.Manifest.Transformations,
            transformation => transformation.RuleId == "clockwork.race-exploration.scheduling-point");
    }

    [Fact]
    public void ConstructorsAndPropertyAccessorsFollowCoyoteExclusions()
    {
        using var context = RewriteTestContext.Create();
        string input = context.CompileFixture("Fx.Race.Exclusions", Fixture);
        string output = Path.Combine(context.Directory, "Fx.Race.Exclusions.rewritten.dll");

        RewriteResult result = context.Rewrite(input, output, EmptyRules(), new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [typeof(RaceInstrumentation).Assembly.Location],
            ReferenceSearchDirectories = [context.Directory],
            InstrumentRaceExploration = true,
        });

        result.EnsureSuccess();
        using ModuleDefinition module = context.LoadModule(output);
        Assert.False(CecilInspect.CallsAnyContaining(CecilInspect.GetMethod(module, "Fx.Subject", ".ctor"), "RaceInstrumentation"));
        Assert.False(CecilInspect.CallsAnyContaining(CecilInspect.GetMethod(module, "Fx.Subject", "get_Property"), "RaceInstrumentation"));
        Assert.False(CecilInspect.CallsAnyContaining(CecilInspect.GetMethod(module, "Fx.Subject", "set_Property"), "RaceInstrumentation"));
    }

    [Fact]
    public void GenericInstanceAndStaticFieldWritesProduceValidClosedIl()
    {
        const string genericFixture = """
            namespace Fx;

            public sealed class GenericState<T>
            {
                public T? Value;
                public static T? Shared;

                public void Set(T value)
                {
                    Value = value;
                    Shared = value;
                }
            }
            """;
        using var context = RewriteTestContext.Create();
        string input = FixtureCompiler.Compile(
            "Fx.GenericRace",
            genericFixture,
            context.Directory,
            FixtureSymbols.PortableFile,
            optimize: false);
        string output = Path.Combine(context.Directory, "Fx.GenericRace.rewritten.dll");

        RewriteResult result = context.Rewrite(input, output, EmptyRules(), new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [typeof(RaceInstrumentation).Assembly.Location],
            ReferenceSearchDirectories = [context.Directory],
            InstrumentRaceExploration = true,
        });

        result.EnsureSuccess();
        using ModuleDefinition module = context.LoadModule(output);
        MethodDefinition set = CecilInspect.GetMethod(module, "Fx.GenericState`1", "Set");
        Assert.True(CecilInspect.CallsAnyContaining(set, "RaceInstrumentation::WriteInstance"));
        Assert.True(CecilInspect.CallsAnyContaining(set, "RaceInstrumentation::WriteStaticField"));
    }

    [Fact]
    public void IndirectAccessInstrumentationPreservesCompletePrefixChain()
    {
        using var context = RewriteTestContext.Create();
        string input = FixtureCompiler.Compile(
            "Fx.PrefixRace",
            "namespace Fx { public static class PrefixRace { public static int Read(ref int value) => value; } }",
            context.Directory,
            FixtureSymbols.None,
            optimize: true);
        string modifiedInput = Path.Combine(context.Directory, "Fx.PrefixRace.modified.dll");
        using (ModuleDefinition editable = ModuleDefinition.ReadModule(input))
        {
            MethodDefinition read = CecilInspect.GetMethod(editable, "Fx.PrefixRace", "Read");
            Instruction indirect = Assert.Single(read.Body.Instructions, instruction => instruction.OpCode.Code == Code.Ldind_I4);
            ILProcessor processor = read.Body.GetILProcessor();
            processor.InsertBefore(indirect, Instruction.Create(OpCodes.Unaligned, (byte)1));
            processor.InsertBefore(indirect, Instruction.Create(OpCodes.Volatile));
            editable.Write(modifiedInput);
        }

        string output = Path.Combine(context.Directory, "Fx.PrefixRace.rewritten.dll");
        RewriteResult result = context.Rewrite(modifiedInput, output, EmptyRules(), new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [typeof(RaceInstrumentation).Assembly.Location],
            ReferenceSearchDirectories = [context.Directory],
            InstrumentRaceExploration = true,
        });

        result.EnsureSuccess();
        using ModuleDefinition module = context.LoadModule(output);
        MethodDefinition rewritten = CecilInspect.GetMethod(module, "Fx.PrefixRace", "Read");
        Instruction unaligned = Assert.Single(rewritten.Body.Instructions, instruction => instruction.OpCode == OpCodes.Unaligned);
        Assert.Equal(OpCodes.Volatile, unaligned.Next!.OpCode);
        Assert.Equal(Code.Ldind_I4, unaligned.Next.Next!.OpCode.Code);
        Assert.Contains(
            rewritten.Body.Instructions.TakeWhile(instruction => instruction != unaligned),
            instruction =>
                instruction.OpCode.Code == Code.Call &&
                instruction.Operand is MethodReference method &&
                method.Name == nameof(RaceInstrumentation.InterleaveUntrackedMemory));
    }

    private static RewriteRuleSet EmptyRules() => new("clockwork.race-tests", "1.0", []);
}
