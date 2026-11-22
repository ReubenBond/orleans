namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A node's share of the cluster's archive-log request budget as the node holds it (orleans.md §5.6): none until a reply
/// first assigns one, so the node drains nothing it has not been given; then the share the latest assigning reply gave it;
/// and, once <see cref="ArchiveLogRequestShare.ReplyIntervals"/> control intervals pass since that reply, at most the
/// node's floor over its current registry view (OR-I22). A reply which assigns no share, one the controller had no room
/// for or one to a replaced process, keeps the share and does not restart the deadline.
/// </summary>
/// <remarks>
/// The deadline is applied where the rate is read, <see cref="RateAt"/>, by the drains' pacer before each request, not
/// where a report is sent, which a hung report call could hold. Safe from any thread.
/// </remarks>
/// <param name="floor">The node's floor now, <see cref="ArchiveLogRequestShare.Floor"/> over its current registry view.</param>
/// <param name="controlInterval">The control interval, at which the node reports.</param>
internal sealed class ArchiveLogNodeShare(Func<double> floor, TimeSpan controlInterval)
{
    private readonly TimeSpan _deadline = controlInterval * ArchiveLogRequestShare.ReplyIntervals;
    private Assignment? _assigned;

    /// <summary>Gets the share the latest assigning reply gave, or <see langword="null"/> before the first.</summary>
    public double? Assigned => Volatile.Read(ref _assigned) is { } assigned ? assigned.Share : null;

    /// <summary>Takes a reply received at <paramref name="at"/>: a share it assigns restarts the deadline; none changes nothing.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The share is not positive and finite: no controller assigns one.</exception>
    public void Observe(double? share, DateTimeOffset at)
    {
        if (share is { } assigned)
        {
            ArchiveLogRequestShare.CheckRate(assigned, nameof(share));
            Volatile.Write(ref _assigned, new Assignment(assigned, at));
        }
    }

    /// <summary>
    /// Returns the requests a second the node's drains make at most at <paramref name="now"/>: the assigned share, or at most
    /// the floor once <see cref="ArchiveLogRequestShare.ReplyIntervals"/> control intervals passed since it was assigned; a
    /// share below the floor stays. <see langword="null"/> before the first share.
    /// </summary>
    public double? RateAt(DateTimeOffset now) =>
        Volatile.Read(ref _assigned) is not { } assigned ? null
        : now - assigned.At >= _deadline ? Math.Min(assigned.Share, floor())
        : assigned.Share;

    /// <summary>A share and when its reply arrived, published whole.</summary>
    private sealed record Assignment(double Share, DateTimeOffset At);
}
