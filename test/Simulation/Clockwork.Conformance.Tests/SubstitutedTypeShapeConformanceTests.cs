namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for whole-type substitutions reaching beyond member signatures: a base type and an
/// interface composed from a substituted type (<c>List&lt;Barrier&gt;</c>, <c>IEnumerable&lt;CountdownEvent&gt;</c>), an
/// explicit implementation of such an interface, generic constraints naming one, and the <c>constrained.</c> operand of
/// a <c>foreach</c> over a list of one. The rewritten probe must load, bind, and compute what the uninstrumented probe
/// computes against the real BCL, in both Release and Debug codegen.
/// </summary>
public sealed class SubstitutedTypeShapeConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System.Collections;
        using System.Collections.Generic;
        using System.Threading;

        namespace Conf
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

            public static class ShapeProbe
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
                    registry.Add(new CountdownEvent(5));
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
                        initial,
                        barriers.GetType().BaseType!.GetGenericArguments()[0].Name == "Barrier" ? "bcl" : "controlled");
                }
            }
        }
        """;

    private const string ProbeType = "Conf.ShapeProbe";

    private readonly RewriteFixture _fixture = new();

    public static TheoryData<bool> Optimizations => new() { true, false };

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void SubstitutedShapesRunAsTheBcl(bool optimize)
    {
        string suffix = optimize ? "Rel" : "Dbg";
        UninstrumentedProbe bcl = _fixture.CompileUninstrumented("Conf.ShapesBcl" + suffix, ProbeType, Source, optimize);
        StagedProbe staged = _fixture.StageControlledTasks("Conf.Shapes" + suffix, ProbeType, Source, optimize);
        using var host = new SimulationHost(Start);

        string expected = (string)bcl.Method("Run").Invoke(null, null)!;
        string actual = (string)host.Invoke(staged.Method("Run"))!;

        Assert.Equal("5,5,5,3,9,bcl", expected);
        Assert.Equal("5,5,5,3,9,controlled", actual);
    }

    public void Dispose() => _fixture.Dispose();
}
