using System.Reflection;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Racing;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>Verifies the exact mutable and concurrent collection inventory instrumented in race mode.</summary>
public sealed class CollectionAccessGoldenTests
{
    private const string Fixture = """
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        namespace Fx;

        public static class Collections
        {
            public static void Mutate(List<int> values)
            {
                values.AddRange([1, 2]);
                values.Insert(0, 3);
                values.InsertRange(0, [4]);
                values.RemoveAt(0);
                values.RemoveAll(value => value < 0);
                values.RemoveRange(0, 1);
            }

            public static int Run()
            {
                var list = new List<int> { 1 };
                list.Add(2);
                int result = 0;
                foreach (int item in list)
                {
                    result += item;
                }

                var dictionary = new Dictionary<int, int>();
                dictionary[1] = 3;
                dictionary.TryGetValue(1, out int dictionaryValue);
                result += dictionaryValue;

                var set = new HashSet<int>();
                set.Add(4);
                result += set.Contains(4) ? 4 : 0;

                var bag = new ConcurrentBag<int>();
                bag.Add(5);
                bag.TryTake(out int bagValue);
                result += bagValue;

                var concurrentDictionary = new ConcurrentDictionary<int, int>();
                concurrentDictionary.TryAdd(1, 6);
                concurrentDictionary.TryGetValue(1, out int concurrentDictionaryValue);
                result += concurrentDictionaryValue;

                var queue = new ConcurrentQueue<int>();
                queue.Enqueue(7);
                queue.TryDequeue(out int queueValue);
                result += queueValue;

                var stack = new ConcurrentStack<int>();
                stack.Push(8);
                stack.TryPop(out int stackValue);
                return result + stackValue;
            }
        }
        """;

    [Fact]
    public void RaceModeInstrumentsExactCollectionInventoryWithoutChangingTypes()
    {
        using var context = RewriteTestContext.Create();
        string input = FixtureCompiler.Compile(
            "Fx.Collections",
            Fixture,
            context.Directory,
            FixtureSymbols.PortableFile,
            optimize: false);
        string output = Path.Combine(context.Directory, "Fx.Collections.rewritten.dll");
        RewriteResult result = context.Rewrite(input, output, EmptyRules(), new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [typeof(RaceInstrumentation).Assembly.Location],
            ReferenceSearchDirectories = [context.Directory],
            InstrumentRaceExploration = true,
        });

        result.EnsureSuccess();
        using ModuleDefinition module = context.LoadModule(output);
        MethodDefinition run = CecilInspect.GetMethod(module, "Fx.Collections", "Run");
        List<string> calls = CecilInspect.CallTargets(run);
        Assert.Contains(calls, call => call.Contains("RaceInstrumentation::ReadCollection", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.Contains("RaceInstrumentation::WriteCollection", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.Contains("RaceInstrumentation::InterleaveConcurrentCollection", StringComparison.Ordinal));

        string[] inventory =
        [
            "System.Collections.Generic.List`1",
            "System.Collections.Generic.Dictionary`2",
            "System.Collections.Generic.HashSet`1",
            "System.Collections.Concurrent.ConcurrentBag`1",
            "System.Collections.Concurrent.ConcurrentDictionary`2",
            "System.Collections.Concurrent.ConcurrentQueue`1",
            "System.Collections.Concurrent.ConcurrentStack`1",
        ];
        foreach (string typeName in inventory)
        {
            Assert.Contains(
                result.Manifest.Transformations,
                transformation =>
                    transformation.RuleId == "clockwork.race-exploration.collection-access" &&
                    transformation.Target.StartsWith(typeName, StringComparison.Ordinal));
        }

        string[] listMutators = ["AddRange", "Insert", "InsertRange", "RemoveAt", "RemoveAll", "RemoveRange"];
        foreach (string mutator in listMutators)
        {
            Assert.Contains(
                result.Manifest.Transformations,
                transformation =>
                    transformation.Target == $"System.Collections.Generic.List`1::{mutator}" &&
                    transformation.Replacement!.Contains("RaceInstrumentation::WriteCollection", StringComparison.Ordinal));
        }

        Assert.Contains(run.Body.Variables, variable =>
            variable.VariableType.FullName.StartsWith("System.Collections.Generic.List`1", StringComparison.Ordinal));
        Assert.DoesNotContain(module.GetTypes(), type => type.FullName.Contains("Wrapper", StringComparison.Ordinal));
    }

    [Fact]
    public void InstrumentedCollectionsPreserveCoveredCallSemantics()
    {
        using var context = RewriteTestContext.Create();
        string input = FixtureCompiler.Compile(
            "Fx.Collections.Execution",
            Fixture,
            context.Directory,
            FixtureSymbols.PortableFile,
            optimize: true);
        string output = Path.Combine(context.Directory, "Fx.Collections.Execution.rewritten.dll");
        RewriteResult result = context.Rewrite(input, output, EmptyRules(), new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [typeof(RaceInstrumentation).Assembly.Location],
            ReferenceSearchDirectories = [context.Directory],
            InstrumentRaceExploration = true,
        });
        result.EnsureSuccess();

        Assembly assembly = Assembly.LoadFile(output);
        MethodInfo run = assembly.GetType("Fx.Collections")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(36, Assert.IsType<int>(run.Invoke(null, null)));
    }

    [Fact]
    public void TailPrefixedCollectionCallIsLeftIntactAndClassifiedUnsupported()
    {
        const string source = """
            using System.Collections.Generic;
            namespace Fx;
            public static class TailCollection
            {
                public static int Count(List<int> values) => values.Count;
            }
            """;
        using var context = RewriteTestContext.Create();
        string input = FixtureCompiler.Compile(
            "Fx.TailCollection",
            source,
            context.Directory,
            FixtureSymbols.None,
            optimize: true);
        string modifiedInput = Path.Combine(context.Directory, "Fx.TailCollection.modified.dll");
        using (ModuleDefinition editable = ModuleDefinition.ReadModule(input))
        {
            MethodDefinition count = CecilInspect.GetMethod(editable, "Fx.TailCollection", "Count");
            Instruction call = Assert.Single(count.Body.Instructions, instruction => instruction.OpCode.Code == Code.Callvirt);
            count.Body.GetILProcessor().InsertBefore(call, Instruction.Create(OpCodes.Tail));
            editable.Write(modifiedInput);
        }

        string output = Path.Combine(context.Directory, "Fx.TailCollection.rewritten.dll");
        RewriteResult result = context.Rewrite(modifiedInput, output, EmptyRules(), new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [typeof(RaceInstrumentation).Assembly.Location],
            ReferenceSearchDirectories = [context.Directory],
            InstrumentRaceExploration = true,
        });
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(output);
        MethodDefinition rewritten = CecilInspect.GetMethod(module, "Fx.TailCollection", "Count");
        Instruction tail = Assert.Single(rewritten.Body.Instructions, instruction => instruction.OpCode == OpCodes.Tail);
        Assert.True(tail.Next!.OpCode.Code is Code.Call or Code.Callvirt);
        Assert.Equal(Code.Ret, tail.Next.Next!.OpCode.Code);
        Assert.False(CecilInspect.CallsAnyContaining(rewritten, "RaceInstrumentation"));
    }

    private static RewriteRuleSet EmptyRules() => new("clockwork.collection-tests", "1.0", []);
}
