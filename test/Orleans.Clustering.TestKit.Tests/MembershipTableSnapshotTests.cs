using Orleans.Runtime;
using Xunit;
using static Orleans.Clustering.TestKit.MembershipTableTestData;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class MembershipTableSnapshotTests
{
    private static ClusteringMembershipSnapshot Snapshot(MembershipEntry entry, int version = 4, string tableToken = "table-4", string rowToken = "row-1")
        => ClusteringMembershipSnapshot.Capture(new(Tuple.Create(entry, rowToken), new(version, tableToken)));

    [Fact]
    public void Capture_DetachesEntriesAndNestedSuspectLists()
    {
        var entry = CreateEntry(1);
        var suspects = entry.SuspectTimes!;
        var captured = Snapshot(entry);
        MutateCaller(entry);
        suspects.Clear();
        var row = Assert.Single(captured.Rows).Value.Entry;
        Assert.Equal("host-1", row.HostName);
        Assert.Equal(SiloStatus.Created, row.Status);
        Assert.Equal(22001, row.ProxyPort);
        Assert.Equal(T0, row.IAmAliveTime);
        Assert.Equal(2, row.Suspects.Length);
        Assert.Equal("127.0.0.1:11001@10", row.Suspects[0].Identity);
        Assert.Equal(T0.AddSeconds(-10), row.Suspects[0].Time);
    }

    [Theory]
    [InlineData("IAmAliveTime")]
    [InlineData("row ETag")]
    [InlineData("table ETag")]
    [InlineData("table integer")]
    public void CompareComplete_DetectsHeartbeatAndBothTokenChanges(string field)
    {
        var entry = CreateEntry(1);
        var expected = Snapshot(entry);
        if (field == "IAmAliveTime") entry.IAmAliveTime = T2;
        var actual = Snapshot(entry, field == "table integer" ? 5 : 4,
            field == "table ETag" ? "different-table" : "table-4", field == "row ETag" ? "different-row" : "row-1");
        Assert.Contains(field, expected.CompareComplete(actual)!);
        Assert.Null(expected.CompareComplete(Snapshot(CreateEntry(1))));
    }

    [Theory]
    [InlineData("Identity")]
    [InlineData("Status")]
    [InlineData("ProxyPort")]
    [InlineData("HostName")]
    [InlineData("SiloName")]
    [InlineData("RoleName")]
    [InlineData("UpdateZone")]
    [InlineData("FaultZone")]
    [InlineData("StartTime")]
    [InlineData("SuspectTimes")]
    public void CompareVersioned_IncludesEveryFieldExceptHeartbeatAndRowEtag(string field)
    {
        var expected = Snapshot(CreateEntry(1));
        var changed = CreateEntry(1);
        switch (field)
        {
            case "Identity": changed.SiloAddress = CreateSuccessor(changed).SiloAddress; break;
            case "Status": changed.Status = SiloStatus.Joining; break;
            case "ProxyPort": changed.ProxyPort++; break;
            case "HostName": changed.HostName = "changed-host"; break;
            case "SiloName": changed.SiloName = "changed-name"; break;
            case "RoleName": changed.RoleName = "changed-role"; break;
            case "UpdateZone": changed.UpdateZone++; break;
            case "FaultZone": changed.FaultZone++; break;
            case "StartTime": changed.StartTime = T1; break;
            case "SuspectTimes": changed.SuspectTimes![0] = Tuple.Create(CreateEntry(8).SiloAddress, T2); break;
        }
        Assert.NotNull(expected.CompareVersioned(Snapshot(changed)));
        var heartbeatOnly = CreateEntry(1);
        heartbeatOnly.IAmAliveTime = T2;
        Assert.Null(expected.CompareVersioned(Snapshot(heartbeatOnly, rowToken: "heartbeat-token")));
        Assert.Contains("IAmAliveTime", expected.CompareComplete(Snapshot(heartbeatOnly, rowToken: "heartbeat-token"))!);
    }

    [Fact]
    public void CompareVersioned_IgnoresEnumerationOrderAndNormalizesEmptySuspects()
    {
        var first = CreateEntry(1);
        var second = CreateEntry(2);
        second.SuspectTimes = null;
        var expected = ClusteringMembershipSnapshot.Capture(new(new List<Tuple<MembershipEntry, string>>
            { Tuple.Create(first, "a"), Tuple.Create(second, "b") }, new(3, "v")));
        first.SuspectTimes!.Reverse();
        second.SuspectTimes = [];
        var actual = ClusteringMembershipSnapshot.Capture(new(new List<Tuple<MembershipEntry, string>>
            { Tuple.Create(second, "b"), Tuple.Create(first, "a") }, new(3, "v")));
        Assert.Null(expected.CompareComplete(actual));
        Assert.Equal(2, actual.Rows.Count);
        second.SuspectTimes.Add(Tuple.Create(first.SiloAddress, T0));
        Assert.Contains("SuspectTimes", expected.Rows[second.SiloAddress.ToParsableString()].Entry.Difference(MembershipEntrySnapshot.Capture(second), false)!);
    }

    [Fact]
    public void CompareVersioned_DistinguishesGenerationsAtSameEndpoint()
    {
        var first = CreateEntry(1);
        var successor = CreateSuccessor(first);
        var expected = Snapshot(first);
        Assert.Contains("missing identity", expected.CompareVersioned(Snapshot(successor))!);
        Assert.Equal(first.SiloAddress.Endpoint, successor.SiloAddress.Endpoint);
        Assert.NotEqual(first.SiloAddress.ToParsableString(), successor.SiloAddress.ToParsableString());
    }

    [Fact]
    public void CompareVersioned_DetectsTimestampOnlyChangeForExistingSuspector()
    {
        var entry = CreateEntry(1);
        var expected = Snapshot(entry);
        var voter = entry.SuspectTimes![0].Item1;
        entry.SuspectTimes[0] = Tuple.Create(voter, T2);
        Assert.Contains("SuspectTimes", expected.CompareVersioned(Snapshot(entry))!);
        Assert.Equal(voter.ToParsableString(), expected.Rows[entry.SiloAddress.ToParsableString()].Entry.Suspects[0].Identity);
        Assert.Equal(T0.AddSeconds(-10), expected.Rows[entry.SiloAddress.ToParsableString()].Entry.Suspects[0].Time);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("heartbeat")]
    [InlineData("first-vote")]
    [InlineData("last-vote")]
    public void GetEffectiveUpdateTime_UsesStartHeartbeatAndEveryVote(string newest)
    {
        var entry = CreateEntry(1);
        if (newest == "start") entry.StartTime = T2;
        if (newest == "heartbeat") entry.IAmAliveTime = T2;
        if (newest == "first-vote") entry.SuspectTimes![0] = Tuple.Create(CreateEntry(2).SiloAddress, T2);
        if (newest == "last-vote") entry.SuspectTimes![1] = Tuple.Create(CreateEntry(2).SiloAddress, T2);
        var effective = MembershipEntrySnapshot.Capture(entry).GetEffectiveUpdateTime();
        Assert.Equal(T2, effective);
        Assert.Equal(DateTimeKind.Utc, effective.Kind);
    }

    [Fact]
    public void History_DeadCompactionPreservesTerminalIdentity()
    {
        var dead = CreateEntry(1, status: SiloStatus.Dead);
        var history = new MembershipHistory();
        history.Observe(Snapshot(dead));
        history.Observe(ClusteringMembershipSnapshot.Capture(new(new TableVersion(5, "table-5"))));
        Assert.True(history.IsTerminal(dead.SiloAddress.ToParsableString()));
        dead.Status = SiloStatus.Active;
        var exception = Assert.Throws<ClusteringConformanceException>(() => history.Observe(Snapshot(dead, 6, "table-6")));
        Assert.Contains("terminal identity", exception.Message);
    }

    [Fact]
    public void History_SameVersionRejectsDeadRowRemoval()
    {
        var dead = CreateEntry(1, status: SiloStatus.Dead);
        var history = new MembershipHistory();
        history.Observe(Snapshot(dead));

        var exception = Assert.Throws<ClusteringConformanceException>(() =>
            history.Observe(ClusteringMembershipSnapshot.Capture(new(new TableVersion(4, "table-4")))));

        Assert.Contains("same-version drift", exception.Message);
        Assert.Contains("missing identity=" + dead.SiloAddress.ToParsableString(), exception.Message);
        Assert.True(history.IsTerminal(dead.SiloAddress.ToParsableString()));
    }

    [Fact]
    public void Cleanup_BatchedSnapshots_RequireVersionedCoherentDeletions()
    {
        var first = CreateEntry(1, status: SiloStatus.Dead);
        var second = CreateEntry(2, status: SiloStatus.Dead);
        var live = CreateEntry(3, status: SiloStatus.Active);
        var before = ClusteringMembershipSnapshot.Capture(new(new List<Tuple<MembershipEntry, string>>
        {
            Tuple.Create(first, "r1"), Tuple.Create(second, "r2"), Tuple.Create(live, "r3")
        }, new(10, "v10")));
        var intermediate = before with { Version = 11, TableEtag = "v11", Rows = before.Rows.Remove(first.SiloAddress.ToParsableString()) };
        var final = intermediate with { Version = 12, TableEtag = "v12", Rows = intermediate.Rows.Remove(second.SiloAddress.ToParsableString()) };

        MembershipTableTestRunner.AssertCleanup(before, intermediate, T1, requireAllEligible: false);
        MembershipTableTestRunner.AssertCleanup(intermediate, final, T1);
        MembershipTableTestRunner.AssertCleanup(before, final, T1);
        var history = new MembershipHistory();
        history.Observe(before);
        history.Observe(intermediate);
        history.Observe(final);
        Assert.Equal(SiloStatus.Active, Assert.Single(final.Rows).Value.Entry.Status);
        Assert.Equal(12, final.Version);
        Assert.Equal(3, before.Rows.Count);

        var skippedEmptyBatch = intermediate with { Version = 12, TableEtag = "v12" };
        Assert.Contains("cleanup version", Assert.Throws<ClusteringConformanceException>(() =>
            MembershipTableTestRunner.AssertCleanup(before, skippedEmptyBatch, T1, requireAllEligible: false)).Message);
        var unversioned = intermediate with { Version = before.Version, TableEtag = before.TableEtag };
        Assert.Contains("cleanup version", Assert.Throws<ClusteringConformanceException>(() =>
            MembershipTableTestRunner.AssertCleanup(before, unversioned, T1, requireAllEligible: false)).Message);
        Assert.Throws<ClusteringConformanceException>(() =>
            MembershipTableTestRunner.AssertCleanup(before, before with { Version = 11, TableEtag = "v11" }, T1, requireAllEligible: false));
    }

    [Theory]
    [InlineData(SiloStatus.Active)]
    [InlineData(SiloStatus.Dead)]
    public void History_SameVersionRejectsChangedLiveOrRetainedDeadFields(SiloStatus status)
    {
        var entry = CreateEntry(1, status: status);
        var history = new MembershipHistory();
        history.Observe(Snapshot(entry));
        entry.HostName = "illicit-drift";
        var exception = Assert.Throws<ClusteringConformanceException>(() => history.Observe(Snapshot(entry)));
        Assert.Contains("same-version drift", exception.Message);
        Assert.Contains(".HostName: expected=host-1, observed=illicit-drift", exception.Message);
    }
}
