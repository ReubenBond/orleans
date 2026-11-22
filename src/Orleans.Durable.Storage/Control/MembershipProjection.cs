using Orleans.Runtime;

namespace Orleans.Durable.Storage.Control;

/// <summary>A registered node's standing in the controller grain's membership view (orleans.md §5.7).</summary>
internal enum MembershipStanding
{
    /// <summary>Its claims stand and it may be given work.</summary>
    Live,

    /// <summary>Its silo shuts down: its claims stand, and it is given no work.</summary>
    Departing,

    /// <summary>Its claims ended.</summary>
    Dead,
}

/// <summary>
/// Maps what membership shows of a registered node's silo to the node's standing (orleans.md §5.7), one pure function the
/// controller grain's projection applies to every registered node: <c>Dead</c> is dead, and a silo membership no longer
/// lists whose generation is older than the observer's own was declared dead before the observer joined; shutting down
/// and stopping are departing; every other status, and a silo absent at the observer's generation or a newer one, is live.
/// There is no other standing.
/// </summary>
internal static class MembershipProjection
{
    /// <summary>Returns the standing of a node registered at a silo of <paramref name="registeredGeneration"/>.</summary>
    /// <param name="status">The silo's status in the membership snapshot, or <see langword="null"/> when the snapshot does not list it.</param>
    /// <param name="registeredGeneration">The generation of the silo the registry names for the node.</param>
    /// <param name="observerGeneration">The generation of the observing silo.</param>
    public static MembershipStanding Standing(SiloStatus? status, int registeredGeneration, int observerGeneration) => status switch
    {
        SiloStatus.Dead => MembershipStanding.Dead,
        SiloStatus.ShuttingDown or SiloStatus.Stopping => MembershipStanding.Departing,
        SiloStatus.None or SiloStatus.Created or SiloStatus.Joining or SiloStatus.Active => MembershipStanding.Live,
        null => registeredGeneration < observerGeneration ? MembershipStanding.Dead : MembershipStanding.Live,

        // A status Orleans adds later stands as every status but the three above does.
        _ => MembershipStanding.Live,
    };
}
