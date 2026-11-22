using System.Text;
using JournalOperation = Orleans.Durable.Storage.Testing.HistoryOperation<Orleans.Durable.Storage.Testing.JournalInput, Orleans.Durable.Storage.Testing.JournalOutput>;

namespace Orleans.Durable.Storage.Testing.Tests;

/// <summary>
/// The journal model specifies Journals's journal semantics and write outcomes with opaque versions (journals.md §2):
/// writes admitted at a name of the journal's state, each accepted entry naming its state by a version which never
/// named one, rejections which report a name of the current state unless deposed, <c>ReplaceRequired</c> for an
/// <c>Append</c> at a name of the current state, rebases and the LSM's forgetting as steps of the environment, and reads
/// of the fold with a name of its state.
/// </summary>
public sealed class JournalModelTests
{
    private const string Origin = JournalModel.Origin;
    private static readonly ReadOnlyMemory<byte> A = Payload("a");
    private static readonly ReadOnlyMemory<byte> B = Payload("b");
    private static readonly ReadOnlyMemory<byte> C = Payload("c");

    /// <summary>
    /// A read returns the content its opener began: a <c>Replace</c>'s snapshot, an empty one included, after which the
    /// journal exists, and every later append; a <c>Delete</c> leaves it absent and empty, and an append after it begins
    /// new content; an <c>Expired</c> of the deleted journal leaves it absent, at the version it reports.
    /// </summary>
    [Fact]
    public void ReadsReturnTheFoldOfTheEntries()
    {
        var model = JournalModel.Instance;
        var state = model.Init();
        Assert.True(model.Step(state, JournalInput.Read("j"), JournalOutput.Read(false, Origin, default), out _));

        state = Accept(state, JournalInput.Append("j", Origin, A), "v1");
        state = Accept(state, JournalInput.Append("j", "v1", B), "v2");
        Assert.True(model.Step(state, JournalInput.Read("j"), Read(true, "v2", "ab"), out _));
        Assert.False(model.Step(state, JournalInput.Read("j"), Read(true, "v2", "ba"), out _));
        Assert.False(model.Step(state, JournalInput.Read("j"), Read(true, "v2", "a"), out _));
        Assert.False(model.Step(state, JournalInput.Read("j"), Read(false, "v2", "ab"), out _));

        var emptied = Accept(state, JournalInput.Replace("j", "v2", default), "v3");
        Assert.True(model.Step(emptied, JournalInput.Read("j"), Read(true, "v3", string.Empty), out _));
        Assert.False(model.Step(emptied, JournalInput.Read("j"), Read(false, "v3", string.Empty), out _));

        state = Accept(state, JournalInput.Replace("j", "v2", C), "v3");
        state = Accept(state, JournalInput.Append("j", "v3", A), "v4");
        Assert.True(model.Step(state, JournalInput.Read("j"), Read(true, "v4", "ca"), out _));

        state = Accept(state, JournalInput.Delete("j", "v4"), "v5");
        Assert.True(model.Step(state, JournalInput.Read("j"), Read(false, "v5", string.Empty), out _));
        Assert.False(model.Step(state, JournalInput.Expire("j", "v4"), JournalOutput.Accepted("v6"), out _));

        var appended = Accept(state, JournalInput.Append("j", "v5", B), "v6");
        Assert.True(model.Step(appended, JournalInput.Read("j"), Read(true, "v6", "b"), out _));
        Assert.False(model.Step(appended, JournalInput.Expire("j", "v6"), JournalOutput.Accepted("v7"), out _));

        state = Accept(state, JournalInput.Expire("j", "v5"), "v6");
        Assert.True(model.Step(state, JournalInput.Read("j"), Read(false, "v6", string.Empty), out _));
        Assert.Equal("v7", Accept(state, JournalInput.Append("j", "v6", A), "v7").Version);
    }

    /// <summary>
    /// JS-I1: a write is accepted only at the journal's version; JS-I10: the version it reports is one the journal never
    /// held. JS-I4: <c>Moved</c>, <c>Throttled</c>, and <c>Fenced</c> have no effect.
    /// </summary>
    [Fact]
    public void WritesApplyOnlyAtTheJournalsVersion()
    {
        var model = JournalModel.Instance;
        var state = Accept(model.Init(), JournalInput.Append("j", Origin, A), "v1");

        Assert.False(model.Step(state, JournalInput.Append("j", Origin, B), JournalOutput.Accepted("v2"), out _));
        Assert.False(model.Step(state, JournalInput.Expire("j", "v1"), JournalOutput.Accepted("v2"), out _));
        Assert.False(model.Step(state, JournalInput.Append("j", "v1", B), JournalOutput.Accepted("v1"), out _));
        Assert.False(model.Step(state, JournalInput.Append("j", "v1", B), JournalOutput.Accepted(Origin), out _));

        foreach (var noEffect in new[] { JournalOutput.Moved, JournalOutput.Throttled, JournalOutput.Fenced })
        {
            Assert.True(model.Step(state, JournalInput.Append("j", "v1", B), noEffect, out var next));
            Assert.Same(state, next);
        }
    }

    /// <summary>
    /// A rejection reports a name of the current state, never the write's own expected version; an <c>Expired</c> may be
    /// rejected at its own version, for its retention or its key's entries in flight. A rejection which reports a version
    /// which never named a state is a rebase, which names the current state too. Only a deposed owner, answering from its
    /// stale applied state (journals.md §2.2), may report a version which named an earlier state, or a fresh one, a rebase
    /// of its stale state which no answer named, which binds nothing: its version is a hint.
    /// </summary>
    [Fact]
    public void ARejectionReportsANameOfTheCurrentStateUnlessDeposed()
    {
        var model = JournalModel.Instance;
        var state = Accept(Accept(model.Init(), JournalInput.Append("j", Origin, A), "v1"), JournalInput.Delete("j", "v1"), "v2");

        Assert.True(model.Step(state, JournalInput.Append("j", "v1", B), JournalOutput.Rejected("v2"), out var next));
        Assert.Same(state, next);
        Assert.True(model.Step(state, JournalInput.Expire("j", "v2"), JournalOutput.Rejected("v2"), out _));
        Assert.False(model.Step(state, JournalInput.Append("j", "v2", B), JournalOutput.Rejected("v1"), out _));
        Assert.False(model.Step(state, JournalInput.Append("j", "v2", B), JournalOutput.Rejected("v2"), out _));
        Assert.False(model.Step(state, JournalInput.Append("j", "v1", B), JournalOutput.Rejected("v1"), out _));

        Assert.True(model.Step(state, JournalInput.Append("j", "v2", B), JournalOutput.Rejected("v1", deposed: true), out next));
        Assert.Same(state, next);
        Assert.True(model.Step(state, JournalInput.Append("j", "v2", B), JournalOutput.Rejected("r9", deposed: true), out next));
        Assert.Same(state, next);
        Assert.False(model.Step(state, JournalInput.Append("j", "v2", B), JournalOutput.Rejected("v2", deposed: true), out _));

        Assert.True(model.Step(state, JournalInput.Append("j", "v2", B), JournalOutput.Rejected("r1"), out next));
        Assert.Equal("r1", next.Version);
        Assert.True(next.Names.SetEquals(["v2", "r1"]));
        Assert.True(next.Deleted);
    }

    /// <summary>
    /// <c>ReplaceRequired</c> answers only an <c>Append</c>, decided at its owner's applied state after the version check:
    /// its expected version names the current state, a fresh one binding as a rebase, or, from a deposed owner, a version
    /// which named an earlier state; it has no effect (JS-I4).
    /// </summary>
    [Fact]
    public void ReplaceRequiredAnswersOnlyAnAppendAtANameOfTheCurrentState()
    {
        var model = JournalModel.Instance;
        var state = Accept(model.Init(), JournalInput.Append("j", Origin, A), "v1");

        Assert.True(model.Step(state, JournalInput.Append("j", "v1", B), JournalOutput.ReplaceRequired, out var next));
        Assert.Same(state, next);
        Assert.False(model.Step(state, JournalInput.Append("j", Origin, B), JournalOutput.ReplaceRequired, out _));
        Assert.True(model.Step(state, JournalInput.Append("j", Origin, B), JournalOutput.ReplaceRequiredBy(deposed: true), out next));
        Assert.Same(state, next);
        Assert.True(model.Step(state, JournalInput.Append("j", "r1", B), JournalOutput.ReplaceRequired, out next));
        Assert.Equal("r1", next.Version);
        Assert.False(model.Step(state, JournalInput.Replace("j", "v1", B), JournalOutput.ReplaceRequired, out _));
        Assert.False(model.Step(state, JournalInput.Delete("j", "v1"), JournalOutput.ReplaceRequired, out _));
    }

    /// <summary>
    /// A writer writes only at a version an answer gave it, so a write accepted at a version which never named a state is
    /// refused, though a rejection or a read naming it would bind it.
    /// </summary>
    [Fact]
    public void AWriteIsAcceptedOnlyAtAVersionTheJournalHeld()
    {
        var model = JournalModel.Instance;
        var state = Accept(model.Init(), JournalInput.Append("j", Origin, A), "v1");

        Assert.False(model.Step(state, JournalInput.Append("j", "r1", B), JournalOutput.Accepted("v2"), out _));
        Assert.True(model.Step(state, JournalInput.Read("j"), Read(true, "r1", "a"), out var rebased));
        Assert.True(model.Step(rebased, JournalInput.Append("j", "r1", B), JournalOutput.Accepted("v2"), out _));
    }

    /// <summary>
    /// Once an <c>Expired</c> is the opener, the LSM may forget the key entirely (JS-I10): <c>Origin</c> then names the
    /// absent state, a new lifetime whose <c>Origin</c>-based versions may repeat the earlier lifetime's. Before an
    /// <c>Expired</c>, <c>Origin</c> names no state but the first, so a read or a write at it is refused.
    /// </summary>
    [Fact]
    public void AnExpiredKeyMayBeForgottenAndStartsANewLifetimeAtOrigin()
    {
        JournalOperation[] forgotten =
        [
            new(0, JournalInput.Append("j", Origin, A), 0, JournalOutput.Accepted("v1"), 10),
            new(0, JournalInput.Delete("j", "v1"), 20, JournalOutput.Accepted("v2"), 30),
            new(0, JournalInput.Expire("j", "v2"), 40, JournalOutput.Accepted("v3"), 50),
            new(1, JournalInput.Read("j"), 60, Read(false, Origin, string.Empty), 70),
            new(0, JournalInput.Append("j", Origin, B), 80, JournalOutput.Accepted("v1"), 90),
            new(1, JournalInput.Read("j"), 100, Read(true, "v1", "b"), 110),
        ];
        JournalOperation[] deletedOnly =
        [
            new(0, JournalInput.Append("j", Origin, A), 0, JournalOutput.Accepted("v1"), 10),
            new(0, JournalInput.Delete("j", "v1"), 20, JournalOutput.Accepted("v2"), 30),
            new(1, JournalInput.Read("j"), 40, Read(false, Origin, string.Empty), 50),
        ];

        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(JournalModel.Instance, forgotten, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(JournalModel.Instance, deletedOnly, TestContext.Current.CancellationToken));
    }
    /// <summary>
    /// A rebase names the journal's state by a version which never named a state, its content unchanged (journals.md
    /// §8.3): a stale writer is rejected once with the rebased version, a read answers it, and the writer's next entry at
    /// it is accepted. The state keeps its earlier name beside it, which a primary holding the key warm accepts, but once an
    /// entry has given the journal a new state neither names it, and no answer names a state by a version which named an
    /// earlier one; a rebase keeps the content.
    /// </summary>
    [Fact]
    public void ARebaseGivesTheJournalsStateASecondName()
    {
        JournalOperation[] rebased =
        [
            new(0, JournalInput.Append("j", Origin, A), 0, JournalOutput.Accepted("v1"), 10),
            new(0, JournalInput.Append("j", "v1", B), 20, JournalOutput.Rejected("r1"), 30),
            new(1, JournalInput.Read("j"), 40, Read(true, "r1", "a"), 50),
            new(0, JournalInput.Append("j", "r1", B), 60, JournalOutput.Accepted("r2"), 70),
            new(1, JournalInput.Read("j"), 80, Read(true, "r2", "ab"), 90),
        ];
        JournalOperation[] eitherName =
        [
            new(0, JournalInput.Append("j", Origin, A), 0, JournalOutput.Accepted("v1"), 10),
            new(1, JournalInput.Read("j"), 20, Read(true, "r1", "a"), 30),
            new(0, JournalInput.Append("j", "v1", B), 40, JournalOutput.Accepted("v2"), 50),
            new(1, JournalInput.Read("j"), 60, Read(true, "v2", "ab"), 70),
        ];
        JournalOperation[] rebasedNameOfAnEarlierState =
        [
            new(0, JournalInput.Append("j", Origin, A), 0, JournalOutput.Accepted("v1"), 10),
            new(1, JournalInput.Read("j"), 20, Read(true, "r1", "a"), 30),
            new(0, JournalInput.Append("j", "v1", B), 40, JournalOutput.Accepted("v2"), 50),
            new(1, JournalInput.Append("j", "r1", C), 60, JournalOutput.Accepted("v3"), 70),
        ];
        JournalOperation[] backToAnOldVersion =
        [
            new(0, JournalInput.Append("j", Origin, A), 0, JournalOutput.Accepted("v1"), 10),
            new(0, JournalInput.Append("j", "v1", B), 20, JournalOutput.Accepted("v2"), 30),
            new(1, JournalInput.Read("j"), 40, Read(true, "v1", "ab"), 50),
        ];
        JournalOperation[] contentChanged =
        [
            new(0, JournalInput.Append("j", Origin, A), 0, JournalOutput.Accepted("v1"), 10),
            new(1, JournalInput.Read("j"), 20, Read(true, "r1", "b"), 30),
        ];

        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(JournalModel.Instance, rebased, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(JournalModel.Instance, eitherName, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(JournalModel.Instance, rebasedNameOfAnEarlierState, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(JournalModel.Instance, backToAnOldVersion, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(JournalModel.Instance, contentChanged, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An ambiguous write takes effect where it applies, leaving the journal at a version the model learns from the next
    /// answer which reports one it never held; recorded with an unbounded response, it may instead be linearized after
    /// every other operation, so a history in which it never took effect is linearizable too.
    /// </summary>
    [Fact]
    public void AnAmbiguousWriteMayOrMayNotTakeEffect()
    {
        JournalOperation ambiguous = new(0, JournalInput.Append("j", Origin, A), 0, JournalOutput.Ambiguous, HistoryOperation.Unbounded);
        JournalOperation[] tookEffect =
        [
            ambiguous,
            new(1, JournalInput.Read("j"), 10, Read(true, "v1", "a"), 20),
            new(1, JournalInput.Append("j", "v1", B), 30, JournalOutput.Accepted("v2"), 40),
        ];
        JournalOperation[] tookEffectUnread =
        [
            ambiguous,
            new(1, JournalInput.Append("j", Origin, B), 10, JournalOutput.Rejected("v1"), 20),
            new(1, JournalInput.Append("j", "v1", B), 30, JournalOutput.Accepted("v2"), 40),
        ];
        JournalOperation[] noEffect =
        [
            ambiguous,
            new(1, JournalInput.Read("j"), 10, Read(false, Origin, string.Empty), 20),
            new(1, JournalInput.Append("j", Origin, B), 30, JournalOutput.Accepted("v1"), 40),
            new(1, JournalInput.Read("j"), 50, Read(true, "v1", "b"), 60),
        ];
        JournalOperation[] tookEffectThenVanished =
        [
            ambiguous,
            new(1, JournalInput.Read("j"), 10, Read(true, "v1", "a"), 20),
            new(1, JournalInput.Read("j"), 30, Read(false, Origin, string.Empty), 40),
        ];

        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(JournalModel.Instance, tookEffect, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(JournalModel.Instance, tookEffectUnread, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(JournalModel.Instance, noEffect, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(JournalModel.Instance, tookEffectThenVanished, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// JS-I3: a read which begins after an acknowledgement reflects it; concurrent with it, either answer is
    /// linearizable. Journals with distinct keys are independent partitions.
    /// </summary>
    [Fact]
    public void ReadsReflectEveryEarlierAcknowledgement()
    {
        JournalOperation[] concurrent =
        [
            new(0, JournalInput.Append("j", Origin, A), 0, JournalOutput.Accepted("v1"), 20),
            new(1, JournalInput.Read("j"), 10, Read(false, Origin, string.Empty), 30),
            new(2, JournalInput.Read("j"), 15, Read(true, "v1", "a"), 25),
            new(3, JournalInput.Read("k"), 15, Read(false, Origin, string.Empty), 25),
        ];
        JournalOperation[] stale =
        [
            new(0, JournalInput.Append("j", Origin, A), 0, JournalOutput.Accepted("v1"), 20),
            new(3, JournalInput.Append("k", Origin, B), 0, JournalOutput.Accepted("w1"), 20),
            new(1, JournalInput.Read("j"), 30, Read(true, "v1", "a"), 40),
            new(2, JournalInput.Read("k"), 30, Read(false, Origin, string.Empty), 40),
        ];

        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(JournalModel.Instance, concurrent, TestContext.Current.CancellationToken));
        var analysis = LinearizabilityChecker.CheckVerbose(JournalModel.Instance, stale, TestContext.Current.CancellationToken);
        Assert.Equal(LinearizabilityVerdict.Illegal, analysis.Verdict);
        Assert.Equal([LinearizabilityVerdict.Ok, LinearizabilityVerdict.Illegal], analysis.Partitions.Select(static partition => partition.Verdict));
    }

    /// <summary>
    /// A read is answered, <c>Moved</c>, or without an answer (journals.md §2.3); a write's other outcomes are not a read's.
    /// </summary>
    [Fact]
    public void ReadsAreAnsweredMovedOrUnanswered()
    {
        var model = JournalModel.Instance;
        var state = Accept(model.Init(), JournalInput.Append("j", Origin, A), "v1");
        var read = JournalInput.Read("j");

        Assert.True(model.Step(state, read, JournalOutput.Moved, out var next));
        Assert.Same(state, next);
        Assert.True(model.Step(state, read, JournalOutput.Ambiguous, out _));
        Assert.False(model.Step(state, read, JournalOutput.Rejected("v1"), out _));
        Assert.False(model.Step(state, read, JournalOutput.ReplaceRequired, out _));
        Assert.False(model.Step(state, read, JournalOutput.Throttled, out _));
        Assert.False(model.Step(state, read, JournalOutput.Fenced, out _));
    }

    /// <summary>
    /// Equal journals are equal states with equal hash codes, whatever the payloads' buffers and the order their versions
    /// were held in.
    /// </summary>
    [Fact]
    public void EqualJournalsAreEqualStates()
    {
        var first = Accept(Accept(JournalModel.Instance.Init(), JournalInput.Append("j", Origin, A), "v1"), JournalInput.Append("j", "v1", B), "v2");
        var second = Accept(Accept(JournalModel.Instance.Init(), JournalInput.Append("j", Origin, Payload("a")), "v1"), JournalInput.Append("j", "v1", Payload("b")), "v2");
        var other = Accept(Accept(JournalModel.Instance.Init(), JournalInput.Append("j", Origin, B), "v1"), JournalInput.Append("j", "v1", A), "v2");
        var replaced = Accept(Accept(JournalModel.Instance.Init(), JournalInput.Append("j", Origin, A), "v1"), JournalInput.Replace("j", "v1", B), "v2");
        var otherHistory = Accept(Accept(JournalModel.Instance.Init(), JournalInput.Append("j", Origin, A), "w1"), JournalInput.Append("j", "w1", B), "v2");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, other);
        Assert.NotEqual(first, replaced);
        Assert.NotEqual(first, otherHistory);
        Assert.Equal("ab"u8.ToArray(), first.Content);
        Assert.Equal("v2 [61, 62]", first.ToString());
        Assert.Equal("v3 deleted", Accept(first, JournalInput.Delete("j", "v2"), "v3").ToString());
    }

    [Fact]
    public void OperationsDescribeThemselves()
    {
        var model = JournalModel.Instance;

        Assert.Equal("append(j, AAA, 61) -> Accepted(v1)", model.DescribeOperation(JournalInput.Append("j", Origin, A), JournalOutput.Accepted("v1")));
        Assert.Equal("delete(j, v4) -> Rejected(v5)", model.DescribeOperation(JournalInput.Delete("j", "v4"), JournalOutput.Rejected("v5")));
        Assert.Equal("append(j, v4, 61) -> ReplaceRequired", model.DescribeOperation(JournalInput.Append("j", "v4", A), JournalOutput.ReplaceRequired));
        Assert.Equal("read(j) -> exists at v2 [6162]", model.DescribeOperation(JournalInput.Read("j"), Read(true, "v2", "ab")));
        Assert.Equal(
            "replace(j, v1, 3031323334353637…(10 bytes)) -> Ambiguous",
            model.DescribeOperation(JournalInput.Replace("j", "v1", Payload("0123456789")), JournalOutput.Ambiguous));
    }

    [Fact]
    public void WritesNeedAnExpectedVersion() =>
        _ = Assert.Throws<ArgumentNullException>(() => JournalInput.Append("j", null!, A));

    private static JournalModelState Accept(JournalModelState state, JournalInput input, string version)
    {
        Assert.True(JournalModel.Instance.Step(state, input, JournalOutput.Accepted(version), out var next), $"{input.Kind} at {input.Expected} was not accepted.");
        return next;
    }

    private static JournalOutput Read(bool exists, string version, string content) => JournalOutput.Read(exists, version, Payload(content));

    private static ReadOnlyMemory<byte> Payload(string text) => Encoding.UTF8.GetBytes(text);
}
