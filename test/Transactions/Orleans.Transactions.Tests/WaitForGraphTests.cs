using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orleans.Runtime;
using Orleans.Transactions.DeadlockDetection;
using Xunit;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class WaitForGraphTests
    {
        private ITestOutputHelper output;

        public WaitForGraphTests(ITestOutputHelper output)
        {
            this.output = output;
            keyByTx.Clear();
            txByKey.Clear();
        }

        private static readonly IDictionary<string, Guid> txByKey = new Dictionary<string, Guid>();
        private static readonly IDictionary<Guid, string> keyByTx = new Dictionary<Guid, string>();
        // create a very fake participant id - we only need it to be unique up to k
        private static ParticipantId Res(string k) => new ParticipantId(k, null!, ParticipantId.Role.Resource);
        private static Guid Tx(string k)
        {
            if (txByKey.TryGetValue(k, out var id))
            {
                return id;
            }
            id = Guid.NewGuid();
            txByKey[k] = id;
            keyByTx[id] = k;
            return id;
        }

        private static string Key(Guid tx) => keyByTx.TryGetValue(tx, out var k) ? k : "NA";
        private static string Key(ParticipantId res) => res.Name;
        private static LockInfo Lock(string tx, string res) => LockInfo.ForLock(Res(res), Tx(tx));
        private static LockInfo Wait(string tx, string res) => LockInfo.ForWait(Res(res), Tx(tx));

        private static string FormatCycle(IEnumerable<LockInfo> cycle) => string.Join(",", cycle.Select(FormatLock));

        private static string FormatLock(LockInfo lockInfo) =>
            lockInfo.IsWait
                ? $"T{Key(lockInfo.TxId)}->R{Key(lockInfo.Resource)}"
                : $"R{Key(lockInfo.Resource)}->T{Key(lockInfo.TxId)}";

        private static void AssertSameLocks(IEnumerable<LockInfo> a, IEnumerable<LockInfo> b)
        {
            var sa = new HashSet<LockInfo>(a);
            var sb = new HashSet<LockInfo>(b);
            if (!sa.SetEquals(sb))
            {
                Assert.Fail($"expected {FormatCycle(sa)} to equal {FormatCycle(sb)}");
            }
        }

        [Fact]
        public void BasicConstruction()
        {
            var locks = new[] {Lock("0", "a"), Wait("1", "a"), Lock("1", "b"), Wait("0", "b")};
            var wfg = new WaitForGraph(locks);
            AssertSameLocks(wfg.ToLockKeys(), locks);
            var found = wfg.DetectCycles(out var cycles);
            Assert.True(found);
            foreach (var cycle in cycles)
            {
                this.output.WriteLine(FormatCycle(cycle));
            }
        }

        [Fact]
        public void DisjointMerge()
        {
            var first = new WaitForGraph(new[] {Lock("0", "a"), Wait("1", "a"), Lock("1", "b"), Wait("0", "b")});
            var second = new WaitForGraph(new[] {Lock("2", "c"), Wait("3", "c"), Lock("3", "d"), Wait("2", "d")});
            var changed = first.MergeWith(second, out var full);
            Assert.True(changed);

            var sub1 = full.GetConnectedSubGraph(new[] {Tx("0")}, Enumerable.Empty<ParticipantId>());
            var sub2 = full.GetConnectedSubGraph(new[] {Tx("2")}, Enumerable.Empty<ParticipantId>());

            AssertSameLocks(first.ToLockKeys(), sub1.ToLockKeys());
            AssertSameLocks(second.ToLockKeys(), sub2.ToLockKeys());

        }


        [Fact]
        public void DetectsCycles()
        {
            var graph = new WaitForGraph(new[]
            {
                Lock("0", "a"),
                Wait("0",  "b"),
                Lock("1", "b"),
                Wait("1", "a"),
            });

            Assert.True(graph.DetectCycles(out var cycles), "graph should have a cycle");
            Assert.Single(cycles);
            AssertSameLocks(graph.ToLockKeys(), cycles[0]);
            foreach (var cycle in cycles)
            {
                this.output.WriteLine(FormatCycle(cycle));
            }
        }

        [Fact]
        public void DoesNotDetectNonCycles()
        {
            var locks = new []
            {
                Lock("T1", "R3"),
                Wait("T1", "R0"),
                Lock("T0", "R0"),
                Wait("T0", "R1"),
                Lock("T2", "R1")
            };
            var graph = new WaitForGraph(locks);
            var expectedEdges = new[]
            {
                "RR0->TT0",
                "RR1->TT2",
                "RR3->TT1",
                "TT0->RR1",
                "TT1->RR0",
            };

            Assert.Equal(expectedEdges, graph.ToLockKeys().Select(FormatLock).Order());
            var connected = graph.GetConnectedSubGraph([Tx("T1")], []);
            Assert.Equal(expectedEdges, connected.ToLockKeys().Select(FormatLock).Order());
            Assert.False(graph.DetectCycles(out var cycles), "graph should not have a cycle");
            Assert.Empty(cycles);
        }

        [Fact]
        public void ConnectedSubGraphIgnoresUnknownNodes()
        {
            var graph = new WaitForGraph(new[] { Lock("0", "a") });
            var subGraph = graph.GetConnectedSubGraph(
                new[] { Guid.NewGuid() },
                new[] { new ParticipantId("missing", null!, ParticipantId.Role.Resource) });

            Assert.Empty(subGraph.ToLockKeys());
        }
    }
}