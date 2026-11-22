using System.Diagnostics;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// Claim-scoped warm state for one primary claim of one open ledger (journals.md §8.3, §8.8, JS-I6): holds each touched
/// key's <see cref="JournalState"/> and <see cref="JournalPlanning"/> in memory for the life of the claim, tracks its
/// byte budget, and evicts least-recently-used idle keys when the budget is exceeded.
/// </summary>
/// <remarks>
/// <para>
/// Warm state is never persisted to the durable state database: a process restart starts with empty warm state, and the
/// primary discards a claim's warm state when the claim ends (JS-I6). While the claim is active, its owning
/// <see cref="LedgerAdmission"/> serializes every mutation of this instance under its admission lock.
/// </para>
/// </remarks>
internal sealed class JournalWarmState(long budgetBytes)
{
    private readonly Dictionary<JournalRowKey, WarmEntry> _entries = [];
    private WarmEntry? _idleHead;
    private WarmEntry? _idleTail;
    private int _idleCount;
    private long _bytes;

    /// <summary>Gets the maximum bytes of warm state retained before idle LRU eviction runs.</summary>
    public long BudgetBytes { get; } = budgetBytes;

    /// <summary>Gets the currently accounted bytes held by this claim's warm state.</summary>
    public long Bytes => _bytes;

    /// <summary>Unidles an existing warm entry already known to this admission without hashing its key again.</summary>
    public void Touch(WarmEntry entry)
    {
        Debug.Assert(!entry.Evicted, "Touch(WarmEntry) only touches a live warm entry.");
        if (entry.IsIdle)
        {
            UnlinkIdle(entry);
        }

        AssertInvariants();
    }

    /// <summary>Gets an existing warm entry or creates a new unloaded one, unidling it if it was idle.</summary>
    public WarmEntry Touch(JournalRowKey key, long entryOverhead)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new WarmEntry(key, entryOverhead);
            _entries.Add(key, entry);
            _bytes += entry.Bytes;
        }
        else if (entry.IsIdle)
        {
            UnlinkIdle(entry);
        }

        AssertInvariants();
        return entry;
    }

    /// <summary>Records a loaded state and planning fold on <paramref name="entry"/>, updating accounted bytes.</summary>
    public void SetLoaded(WarmEntry entry, JournalState? applied, JournalPlanning planning)
    {
        if (!entry.Evicted)
        {
            _bytes -= entry.Bytes;
        }

        entry.Applied = applied;
        entry.Planning = planning;
        if (!entry.Evicted)
        {
            _bytes += entry.Bytes;
        }

        AssertInvariants();
    }

    /// <summary>Updates <paramref name="entry"/>'s applied state after a durable batch applies, updating accounted bytes.</summary>
    public void SetApplied(WarmEntry entry, JournalState? applied)
    {
        if (!entry.Evicted)
        {
            _bytes -= entry.Bytes;
        }

        entry.Applied = applied;
        if (!entry.Evicted)
        {
            _bytes += entry.Bytes;
        }

        AssertInvariants();
    }

    /// <summary>Removes <paramref name="entry"/> from warm state (e.g. failed load or eviction).</summary>
    public void Remove(WarmEntry entry)
    {
        if (entry.IsIdle)
        {
            UnlinkIdle(entry);
        }

        if (_entries.Remove(entry.RowKey))
        {
            _bytes -= entry.Bytes;
        }

        entry.Evicted = true;
        AssertInvariants();
    }

    /// <summary>
    /// Marks <paramref name="entry"/> idle at the most-recently-used end (or least-recently-used end when
    /// <paramref name="leastRecent"/> is <see langword="true"/>) and evicts least-recently-used idle keys past
    /// <see cref="BudgetBytes"/>, invoking <paramref name="onEvict"/> for each evicted entry.
    /// </summary>
    public void IdleAndEvict(WarmEntry entry, bool leastRecent, Action<WarmEntry>? onEvict)
    {
        if (entry.Evicted || entry.IsIdle)
        {
            return;
        }

        if (leastRecent)
        {
            LinkIdleHead(entry);
        }
        else
        {
            LinkIdleTail(entry);
        }

        while (_bytes > BudgetBytes && _idleHead is { } victim)
        {
            UnlinkIdle(victim);
            victim.Evicted = true;
            _ = _entries.Remove(victim.RowKey);
            _bytes -= victim.Bytes;
            onEvict?.Invoke(victim);
        }

        AssertInvariants();
    }

    /// <summary>Discards every entry held for this claim when the claim ends (JS-I6).</summary>
    public void Clear()
    {
        foreach (var entry in _entries.Values)
        {
            entry.IdlePrev = null;
            entry.IdleNext = null;
            entry.IsIdle = false;
            entry.Evicted = true;
        }

        _entries.Clear();
        _idleHead = null;
        _idleTail = null;
        _idleCount = 0;
        _bytes = 0;
        AssertInvariants();
    }

    private void LinkIdleHead(WarmEntry entry)
    {
        entry.IsIdle = true;
        entry.IdlePrev = null;
        entry.IdleNext = _idleHead;
        if (_idleHead is not null)
        {
            _idleHead.IdlePrev = entry;
        }
        else
        {
            _idleTail = entry;
        }

        _idleHead = entry;
        _idleCount++;
    }

    private void LinkIdleTail(WarmEntry entry)
    {
        entry.IsIdle = true;
        entry.IdlePrev = _idleTail;
        entry.IdleNext = null;
        if (_idleTail is not null)
        {
            _idleTail.IdleNext = entry;
        }
        else
        {
            _idleHead = entry;
        }

        _idleTail = entry;
        _idleCount++;
    }

    private void UnlinkIdle(WarmEntry entry)
    {
        if (entry.IdlePrev is { } prev)
        {
            prev.IdleNext = entry.IdleNext;
        }
        else
        {
            _idleHead = entry.IdleNext;
        }

        if (entry.IdleNext is { } next)
        {
            next.IdlePrev = entry.IdlePrev;
        }
        else
        {
            _idleTail = entry.IdlePrev;
        }

        entry.IdlePrev = null;
        entry.IdleNext = null;
        entry.IsIdle = false;
        _idleCount--;
    }

    [Conditional("DEBUG")]
    private void AssertInvariants()
    {
        Debug.Assert(_bytes >= 0, "Warm-state accounted bytes are non-negative.");
        if (_entries.Count == 0)
        {
            Debug.Assert(_bytes == 0, "Warm-state bytes are zero when no entries are tracked.");
        }
        else
        {
            Debug.Assert(_bytes > 0, "Warm-state bytes are positive when entries are tracked.");
        }

        Debug.Assert(_idleCount >= 0 && _idleCount <= _entries.Count, "Idle count is bounded by tracked entries.");
        if (_idleCount == 0)
        {
            Debug.Assert(_idleHead is null && _idleTail is null, "Idle list is empty when idle count is zero.");
        }
        else
        {
            Debug.Assert(_idleHead is not null && _idleTail is not null, "Idle list has head and tail when idle count is positive.");
        }
    }

    /// <summary>One key's warm-state slot within a claim's <see cref="JournalWarmState"/>.</summary>
    internal sealed class WarmEntry(JournalRowKey rowKey, long overhead)
    {
        private readonly long _overhead = overhead;

        public JournalRowKey RowKey { get; } = rowKey;

        public JournalState? Applied { get; set; }

        public JournalPlanning? Planning { get; set; }

        public WarmEntry? IdlePrev { get; set; }

        public WarmEntry? IdleNext { get; set; }

        public bool IsIdle { get; set; }

        public bool Evicted { get; set; }

        public long Bytes => _overhead + RowKey.Length + (Applied is null ? 0 : JournalState.EstimatedBytes) + (Planning is null ? 0 : JournalPlanning.EstimatedBytes);
    }
}
