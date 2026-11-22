using System.Buffers;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The token chain and versions a writer of rows stores in each key's defining rows (JS-I10, HI-I4), as Journals'
/// admission will: each row's version follows the key's previous decided row's, and a key's defining row in a segment
/// stores the token the key's previous write leaves, <c>Plan</c> replayed by the archive store's pure members, from
/// <c>Origin</c>. A test proposes rows through it, one proposal of a key at a time, and settles each by its outcome, so a
/// fenced row joins no chain.
/// </summary>
internal sealed class ArchiveChains
{
    private readonly Func<RowToken, ArchivePlan> _begin;
    private readonly Dictionary<string, KeyChain> _keys = [with(StringComparer.Ordinal)];
    private readonly Lock _lock = new();

    /// <summary>Initializes a new instance of the <see cref="ArchiveChains"/> class over a store's <c>Plan</c>.</summary>
    public ArchiveChains(IArchiveStore store)
        : this(store.Begin)
    {
    }

    private ArchiveChains(Func<RowToken, ArchivePlan> begin) => _begin = begin;

    /// <summary>A writer which breaks the chain, which the chain's checkers must catch.</summary>
    public enum ChainMutant
    {
        /// <summary>The writer.</summary>
        None,

        /// <summary>Stores, in a key's next defining row, the token its previous write started from: the wrong parent.</summary>
        WrongParent,

        /// <summary>Plans a key's chain through rows which were fenced, as if they were decided.</summary>
        PlansFenced,

        /// <summary>Stores, in a key's next defining row, its version base with the count reset to 0: a version reset mid-chain.</summary>
        ResetsVersion,
    }

    /// <summary>Gets or sets the mutant the writer runs as.</summary>
    public ChainMutant Mutant { get; set; }

    /// <summary>Gets a value indicating whether <see cref="ChainMutant.PlansFenced"/> has actually mutated a subsequent row's chain.</summary>
    public bool PlannedThroughFenced { get; private set; }

    /// <summary>Returns chains over the empty token, the LSM's: their <c>Plan</c> of every write.</summary>
    public static ArchiveChains WithEmptyTokens() => new(static token => LedgerMapCoverage.Begin(token, "store of empty tokens"));

    /// <summary>
    /// Rebases <paramref name="key"/>, as a primary which loads a key no listed ledger holds a row of does (journals.md
    /// §8.3): its version becomes <c>(versionBase, 0)</c>, and its next write starts from <paramref name="token"/>.
    /// </summary>
    public void Rebase(ReadOnlySpan<byte> key, RowToken versionBase, RowToken token)
    {
        lock (_lock)
        {
            _keys[Convert.ToHexString(key)] = new KeyChain { Version = new RowVersion(versionBase, 0), Start = token };
        }
    }

    /// <summary>Begins a proposal's rows, which <see cref="Pending.Settle"/> joins to the chains, or not, by its outcome.</summary>
    public Pending Propose() => new(this);

    /// <summary>
    /// Writes <paramref name="rows"/>, which the caller keeps, into one batch of <paramref name="writer"/> and proposes it,
    /// settling the chains by its outcome: decided when <c>Durable</c>, not when <c>Fenced</c>, and, when
    /// <c>Ambiguous</c>, as <paramref name="ambiguous"/> says.
    /// </summary>
    /// <exception cref="InvalidOperationException">The proposal was ambiguous, and no <paramref name="ambiguous"/> was given.</exception>
    public async ValueTask<ProposalOutcome> ProposeAsync(LedgerWriter writer, IReadOnlyList<Row> rows, Func<bool>? ambiguous = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rows);
        var pending = Propose();
        ProposalOutcome outcome;
        using (var batch = await writer.CreateBatchAsync(rows.Count, (int)Math.Min(MaxLength(rows), writer.MaxBatchLength), cancellationToken))
        {
            foreach (var row in rows)
            {
                _ = pending.AddRow(batch, row.Key.Span, row.Kind, row.Body.AsReadOnlySequence());
            }

            outcome = await writer.ProposeAsync(batch, cancellationToken);
        }

        pending.Settle(outcome.TryGetValue(out ProposalOutcome.Durable _)
            || outcome.TryGetValue(out ProposalOutcome.Ambiguous _) && (ambiguous ?? throw new InvalidOperationException("The proposal was ambiguous."))());
        return outcome;
    }

    /// <summary>Returns the most bytes a batch of <paramref name="rows"/> takes, with tokens and version bases at their longest.</summary>
    public static long MaxLength(IReadOnlyList<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        long length = LedgerBatch.MaxHeaderLength(rows.Count);
        foreach (var row in rows)
        {
            length += LedgerBatch.MaxRowLength(row.Key.Length, RowToken.MaxLength, RowToken.MaxLength, row.Body.Length);
        }

        return length;
    }

    /// <summary>
    /// The chain of one key: its version, its last write's segment, the token that write starts from, and its commands, with
    /// the count an opener among them superseded.
    /// </summary>
    private sealed class KeyChain
    {
        public RowVersion Version { get; set; }

        public LedgerSegmentId? Segment { get; set; }

        public RowToken Start { get; set; }

        public List<ArchiveCommand> Commands { get; init; } = [];

        public int Superseded { get; set; }

        public bool IncludesFenced { get; set; }

        public KeyChain Clone() => new() { Version = Version, Segment = Segment, Start = Start, Commands = [.. Commands], Superseded = Superseded, IncludesFenced = IncludesFenced };
    }

    /// <summary>One proposal's rows, whose chains it holds apart until it settles.</summary>
    internal sealed class Pending(ArchiveChains chains)
    {
        private readonly Dictionary<string, KeyChain> _tentative = [with(StringComparer.Ordinal)];

        /// <summary>
        /// Writes one row of <paramref name="key"/> into <paramref name="batch"/>: a defining row stores the version of the
        /// key's previous row and the token its previous write leaves; a referencing row its segment's.
        /// </summary>
        /// <returns>The row's version.</returns>
        public RowVersion AddRow(LedgerBatch batch, ReadOnlySpan<byte> key, RowKind kind, ReadOnlySequence<byte> body)
        {
            ArgumentNullException.ThrowIfNull(batch);
            var name = Convert.ToHexString(key);
            if (!_tentative.TryGetValue(name, out var chain))
            {
                lock (chains._lock)
                {
                    chain = chains._keys.TryGetValue(name, out var committed) ? committed.Clone() : new KeyChain();
                }

                _tentative.Add(name, chain);
            }

            var held = batch.HeldVersion(key);
            var segment = batch.Segment ?? default;
            RowVersion before;
            if (held == default)
            {
                before = chains.Mutant == ChainMutant.ResetsVersion && chain.Segment is not null ? new RowVersion(chain.Version.Base, 0) : chain.Version;
                if (chain.Segment is { } previous && previous != segment)
                {
                    var token = chains.Mutant == ChainMutant.WrongParent ? chain.Start : chains.PlanOf(chain);
                    (chain.Start, chain.Segment, chain.Superseded) = (token, segment, 0);
                    chain.Commands.Clear();
                }
                else if (chain.Segment is null)
                {
                    chain.Segment = segment;
                }
            }
            else
            {
                before = held;
            }

            var command = kind == RowKind.Delete ? ArchiveCommand.Delete(body.ToArray()) : ArchiveCommand.Of(kind, new byte[body.Length]);
            if (command.IsOpener)
            {
                chain.Superseded += chain.Commands.Count;
                chain.Commands.Clear();
            }

            chain.Commands.Add(command);
            chain.Version = batch.AddRow(key, kind, before, chain.Start, body);
            return chain.Version;
        }

        /// <summary>Joins the proposal's rows to their keys' chains when they were decided, or, under a mutant, always.</summary>
        public void Settle(bool decided)
        {
            if (!decided && chains.Mutant != ChainMutant.PlansFenced)
            {
                return;
            }

            lock (chains._lock)
            {
                foreach (var (name, chain) in _tentative)
                {
                    if (!decided)
                    {
                        chain.IncludesFenced = true;
                    }
                    else if (chain.IncludesFenced)
                    {
                        chains.PlannedThroughFenced = true;
                    }

                    chains._keys[name] = chain;
                }
            }
        }
    }

    private RowToken PlanOf(KeyChain chain)
    {
        var plan = _begin(chain.Start);
        plan.Supersede(chain.Superseded);
        foreach (var command in chain.Commands)
        {
            plan.Add(command);
        }

        return plan.End();
    }
}
