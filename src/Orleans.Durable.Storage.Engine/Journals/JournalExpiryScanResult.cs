namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// What one expiry scan proposed (journals.md §8.6), by each <c>Expired</c>'s outcome. Every outcome but a failure is
/// expected: a candidate is a hint, so an <c>Expired</c> of a journal written or deleted again since, or of a key with an
/// entry in flight, or no longer warm, is <c>Rejected</c>, and one whose ledger moved is <c>Moved</c>, <c>Fenced</c>, or <c>Ambiguous</c>;
/// the next scan proposes again whatever its warm state still holds deleted.
/// </summary>
/// <param name="Expired">The <c>Expired</c>s accepted, each journal now absent.</param>
/// <param name="Rejected">The <c>Expired</c>s rejected against the key's warm state, or for its key holding none (JS-I1).</param>
/// <param name="Moved">The <c>Expired</c>s whose key's open ledger the node no longer serves.</param>
/// <param name="Fenced">The <c>Expired</c>s a seal or the store's disposal fenced.</param>
/// <param name="Ambiguous">The <c>Expired</c>s whose outcome the writer's disposal left unknown.</param>
/// <param name="Failed">The proposals which failed, as a fault of their admission's own fails them, which assert nothing; the scan goes on past them.</param>
public readonly record struct JournalExpiryScanResult(int Expired, int Rejected, int Moved, int Fenced, int Ambiguous, int Failed)
{
    /// <summary>Gets the number of <c>Expired</c>s the scan proposed.</summary>
    public int Proposed => Expired + Rejected + Moved + Fenced + Ambiguous + Failed;
}
