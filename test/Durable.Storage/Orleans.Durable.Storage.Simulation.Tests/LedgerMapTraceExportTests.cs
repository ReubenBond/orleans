using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// <see cref="LedgerMapTraceExport"/> renders a ledger map trace as the Quint module which replays it against
/// <c>specs/ledgers/LedgerMap.qnt</c> (docs/spec/verification.md §5.1), mapping each write's value onto the model's
/// steps and each decided batch onto a write or a marker.
/// </summary>
public sealed class LedgerMapTraceExportTests
{
    private static readonly (KeyRange Lower, KeyRange Upper) Halves = KeyRange.Root.Children;

    /// <summary>
    /// The bootstrap is the model's init; batches apply in slot order by their durable answers, a marker among them; a
    /// split, and then a group commit of a takeover and a drop, become the model's steps in the fold's order, the drop
    /// after the drain to its <c>T</c>, each write's value checked by <c>mapIs</c>; keys and ranges keep their top bits.
    /// </summary>
    [Fact]
    public void AScriptedHistoryRendersAsTheTraceModule()
    {
        var trace = new LedgerMapTrace();
        trace.Applied(Value(1, 2, Open(1, KeyRange.Root)));
        trace.Durable(new LedgerId(1), new Slot(0), 0x00);
        trace.Durable(new LedgerId(1), new Slot(2), 0x00);
        trace.Applied(Value(2, 4, Closed(1, KeyRange.Root, 3), Open(2, Halves.Lower), Open(3, Halves.Upper)));
        trace.Durable(new LedgerId(3), new Slot(0), 0x80);
        trace.Applied(Value(3, 5, Closed(2, Halves.Lower, 0), Open(3, Halves.Upper), Open(4, Halves.Lower)));
        trace.Batch(new LedgerId(1), new LedgerMapTraceBatch(0, 0x00, StartsSegment: true, Defining: true));
        trace.Batch(new LedgerId(1), new LedgerMapTraceBatch(1, null, StartsSegment: true, Defining: false));
        trace.Batch(new LedgerId(1), new LedgerMapTraceBatch(2, 0x00, StartsSegment: false, Defining: true));
        trace.Batch(new LedgerId(3), new LedgerMapTraceBatch(0, 0x80, StartsSegment: true, Defining: true));

        var module = LedgerMapTraceExport.Render(trace, "LedgerMapTraceScripted1", "a scripted history", keyBits: 2, tracesWrites: true);

        Assert.Contains("module LedgerMapTraceScripted1 {", module, StringComparison.Ordinal);
        Assert.Contains(
            "  import LedgerMap(KeyBits = 2, MaxLedgers = 4, MaxJsn = 2, MaxMarkers = 1, Mutation = \"none\").* from \"../../specs/ledgers/LedgerMap\"",
            module,
            StringComparison.Ordinal);
        Assert.Equal(
            """
              run traceReplay = init.expect(Invariants)
                .then(write(1, 0, false)).expect(and { Invariants, wrote(1, 0, 0, true) })
                .then(marker(1)).expect(and { Invariants, marked(1, 1) })
                .then(write(1, 0, false)).expect(and { Invariants, wrote(1, 2, 0, true) })
                .then(split(1)).expect(and { Invariants, mapIs(Set((1, 0, 0, false), (2, 0, 1, true), (3, 1, 1, true)), Set((1, 3)), 4) }) // installs 2, 3
                .then(write(3, 2, false)).expect(and { Invariants, wrote(3, 0, 2, true) })
                .then(takeover(2)).expect(Invariants) // installs 4
                .then(drain(1, 3)).expect(Invariants)
                .then(drop(1)).expect(and { Invariants, mapIs(Set((2, 0, 1, false), (3, 1, 1, true), (4, 0, 1, true)), Set((2, 0)), 5) })
            }

            """.ReplaceLineEndings(),
            Run(module));
    }

    /// <summary>
    /// A trace without writes, as the committers' simulation records: a write which lists no ledger, or only raises the
    /// floor, is no step; a merge closes both siblings; and no recorded <c>T</c> is a model tail, so a drop takes no drain.
    /// </summary>
    [Fact]
    public void ATraceWithoutWritesComparesNoTailsAndDrainsNothing()
    {
        var trace = new LedgerMapTrace();
        trace.Applied(Value(1, 1));
        trace.Applied(Value(2, 2, Open(1, KeyRange.Root)));
        trace.Applied(Value(3, 4, Closed(1, KeyRange.Root, 7), Open(2, Halves.Lower), Open(3, Halves.Upper)));
        trace.Applied(Value(4, 5, Closed(2, Halves.Lower, 5), Closed(3, Halves.Upper, 9), Open(4, KeyRange.Root)));
        trace.Applied(Value(5, 5, Closed(2, Halves.Lower, 5), Closed(3, Halves.Upper, 9), Open(4, KeyRange.Root)));
        trace.Applied(Value(6, 5, Open(4, KeyRange.Root)));

        var module = LedgerMapTraceExport.Render(trace, "LedgerMapTraceScripted2", "a scripted history", keyBits: 1, tracesWrites: false);

        Assert.Equal(
            """
              run traceReplay = init.expect(Invariants)
                .then(split(1)).expect(and { Invariants, mapIs(Set((1, 0, 0, false), (2, 0, 1, true), (3, 1, 1, true)), Set(), 4) }) // installs 2, 3
                .then(merge(2, 3)).expect(Invariants) // installs 4
                .then(drop(1)).expect(and { Invariants, mapIs(Set((2, 0, 1, false), (3, 1, 1, false), (4, 0, 0, true)), Set(), 5) })
                .then(drop(2)).expect(Invariants)
                .then(drop(3)).expect(and { Invariants, mapIs(Set((4, 0, 0, true)), Set(), 5) })
            }

            """.ReplaceLineEndings(),
            Run(module));
    }

    /// <summary>
    /// A proposal answered durable at a slot no scan holds, as one past its ledger's recorded <c>T</c> would be, is the
    /// model's write where its answer arrived, after the close, which the model refuses.
    /// </summary>
    [Fact]
    public void ADurableAnswerNoScanHoldsIsAWriteWhereItArrived()
    {
        var trace = new LedgerMapTrace();
        trace.Applied(Value(1, 2, Open(1, KeyRange.Root)));
        trace.Applied(Value(2, 3, Closed(1, KeyRange.Root, 0), Open(2, KeyRange.Root)));
        trace.Durable(new LedgerId(1), new Slot(0), 0x40);

        var module = LedgerMapTraceExport.Render(trace, "LedgerMapTraceScripted3", "a scripted history", keyBits: 2, tracesWrites: true);

        Assert.EndsWith(
            """
                .then(takeover(1)).expect(and { Invariants, mapIs(Set((1, 0, 0, false), (2, 0, 0, true)), Set((1, 0)), 3) }) // installs 2
                .then(write(1, 1, false)).expect(and { Invariants, landed(1, 0, 1) }) // durable at slot 0, which no scan holds
            }

            """.ReplaceLineEndings(),
            module.ReplaceLineEndings(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A value which is no sequence of the model's steps, or a range deeper than the model's keys, fails the rendering
    /// rather than becoming a trace the mapping bent to fit.
    /// </summary>
    [Fact]
    public void AValueTheModelCannotExpressFailsTheRendering()
    {
        var closesWithoutSuccessor = new LedgerMapTrace();
        closesWithoutSuccessor.Applied(Value(1, 2, Open(1, KeyRange.Root)));
        closesWithoutSuccessor.Applied(Value(2, 3, Closed(1, KeyRange.Root, 0), Open(2, Halves.Lower)));
        var tooDeep = new LedgerMapTrace();
        tooDeep.Applied(Value(1, 2, Open(1, KeyRange.Root)));
        tooDeep.Applied(Value(2, 4, Closed(1, KeyRange.Root, 0), Open(2, Halves.Lower), Open(3, Halves.Upper)));
        tooDeep.Applied(Value(3, 6, Closed(1, KeyRange.Root, 0), Closed(2, Halves.Lower, 0), Open(3, Halves.Upper), Open(4, Halves.Lower.Children.Lower), Open(5, Halves.Lower.Children.Upper)));

        Assert.Contains("which no takeover, split, or merge", Assert.Throws<InvalidOperationException>(() => LedgerMapTraceExport.Render(closesWithoutSuccessor, "M", "a scripted history", 1, tracesWrites: false)).Message, StringComparison.Ordinal);
        Assert.Contains("deeper than the model's 1 key bits", Assert.Throws<InvalidOperationException>(() => LedgerMapTraceExport.Render(tooDeep, "M", "a scripted history", 1, tracesWrites: false)).Message, StringComparison.Ordinal);
    }

    private static string Run(string module)
    {
        var lines = module.ReplaceLineEndings().Split(Environment.NewLine);
        return string.Join(Environment.NewLine, lines[Array.FindIndex(lines, static line => line.StartsWith("  run traceReplay", StringComparison.Ordinal))..]);
    }

    private static LedgerMapValue Value(ulong epoch, ulong next, params LedgerMetadata[] ledgers) => new(epoch, new LedgerId(next), 0, [.. ledgers]);

    private static LedgerMetadata Open(ulong id, KeyRange range) =>
        new(new LedgerId(id), range, [LedgerMapWorld.Node(1)], 1, new PrimaryClaim(LedgerMapWorld.Node(1), new SequencerInstanceId(new Guid((int)id, 0, 0, [0x80, 0, 0, 0, 0, 0, 0, 1]))), LedgerStatus.Open.Instance);

    private static LedgerMetadata Closed(ulong id, KeyRange range, long t) => Open(id, range).Close(new Slot(t));
}
