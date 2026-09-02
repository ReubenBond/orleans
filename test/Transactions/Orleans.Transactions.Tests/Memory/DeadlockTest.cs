using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.Transactions.DeadlockDetection;
using Orleans.Transactions.TestKit;
using Orleans.Transactions.TestKit.Base.Grains;
using Xunit;

namespace Orleans.Transactions.Tests.Memory
{


    public class TestDeadlockListener : IDeadlockListener
    {
        private IGrainFactory grainFactory;

        public TestDeadlockListener(IGrainFactory grainFactory)
        {
            this.grainFactory = grainFactory;
        }

        public void DeadlockDetected(IEnumerable<LockInfo> locks, DateTime analysisStartedAt, bool detectedLocally, int requestsToDetection,
            TimeSpan analysisDuration) =>
            this.grainFactory.GetGrain<IDeadlockEventCollector>(DeadlockFixture.CollectorKey).ReportEvent(new DeadlockEvent
            {
                Duration = analysisDuration, Local = detectedLocally, Locks = locks.ToArray(),
                IsDefinite = false, RequestCount = requestsToDetection, StartTime = analysisStartedAt,
                Deadlocked = true
            }).Ignore();

        public void DeadlockNotDetected(DateTime analysisStartedAt, int requestsMade, TimeSpan analysisDuration, bool isDefinite) =>
            this.grainFactory.GetGrain<IDeadlockEventCollector>(DeadlockFixture.CollectorKey).ReportEvent(new DeadlockEvent
            {
                Duration = analysisDuration, RequestCount = requestsMade, Deadlocked = false,
                Local = false, Locks = null, IsDefinite = isDefinite,  StartTime = analysisStartedAt
            }).Ignore();
    }

    public class DeadlockFixture : MemoryTransactionsFixture
    {
        public static long CollectorKey { get; } = BitConverter.ToInt64(Guid.NewGuid().ToByteArray());

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<DeadlockConfigurator>();
            builder.Options.InitialSilosCount = 5;
        }

        public class DeadlockConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .ConfigureServices(
                        services => services.UseTransactionalDeadlockDetection()
                            .AddSingleton<IDeadlockListener>((sp) =>
                                ActivatorUtilities.CreateInstance<TestDeadlockListener>(sp)));
            }
        }
    }

    public class DeadlockTest : TransactionTestRunnerBase, IClassFixture<DeadlockFixture>
    {
        public DeadlockTest(DeadlockFixture fixture, ITestOutputHelper output) : base(fixture.GrainFactory, output.WriteLine)
        {
        }

        [Fact]
        public async Task DeadlocksAreReported()
        {
            var collector = this.grainFactory.GetGrain<IDeadlockEventCollector>(DeadlockFixture.CollectorKey);
            await collector.Clear();
            var analysisStartedAfter = DateTime.UtcNow;
            var tasks = new Task[]
            {
                this.Coordinator.RunOrdered(0, 1, 2, 3, 4, 5, 6, 7, 8),
                this.Coordinator.RunOrdered(8, 7, 6, 5, 4, 3, 2, 1, 0),
            };
            bool threw = false;
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception e)
            {
                this.testOutput($"threw {e}");
                threw = true;
            }

            Assert.True(threw, "bad ordering should throw!");

            IList<DeadlockEvent> events = [];
            for (var attempt = 0; attempt < 50 && !events.Any(@event => @event.Deadlocked); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
                events = (await collector.GetEvents())
                    .Where(@event => @event.StartTime >= analysisStartedAfter)
                    .ToArray();
            }

            var deadlockEvents = events.Where(@event => @event.Deadlocked).ToArray();
            Assert.NotEmpty(deadlockEvents);
            Assert.All(deadlockEvents, deadlockEvent =>
            {
                Assert.NotNull(deadlockEvent.Locks);
                Assert.NotEmpty(deadlockEvent.Locks);
                Assert.True(deadlockEvent.StartTime >= analysisStartedAfter);
                Assert.True(deadlockEvent.Duration >= TimeSpan.Zero);
                Assert.Contains(deadlockEvent.Locks, lockInfo => lockInfo.IsWait);
                Assert.Contains(deadlockEvent.Locks, lockInfo => !lockInfo.IsWait);
                Assert.All(deadlockEvent.Locks, lockInfo =>
                {
                    Assert.NotEqual(Guid.Empty, lockInfo.TxId);
                    Assert.False(string.IsNullOrWhiteSpace(lockInfo.Resource.Name));
                });
            });
            var dns =new DeadlockNames();
            foreach (var e in events)
            {
                if (e.Deadlocked)
                {
                    this.testOutput(
                        $"At {e.StartTime.TimeOfDay:G} ({e.RequestCount}): {dns.Format(e.Locks!)}");
                }
                else
                {
                    this.testOutput($"not deadlocked? {e.RequestCount}");
                }
            }
        }

        private class DeadlockNames
        {
            private readonly Dictionary<Guid, string> txNames = new Dictionary<Guid, string>();
            private readonly Dictionary<ParticipantId, string> rsNames = new Dictionary<ParticipantId, string>();

            public string Format(LockInfo lockInfo)
            {
                if (lockInfo.IsWait)
                {
                    return new StringBuilder(Name(this.txNames, lockInfo.TxId, "T")).Append("~>")
                        .Append(Name(this.rsNames, lockInfo.Resource, "R")).ToString();
                }
                else
                {
                    return new StringBuilder(Name(this.rsNames, lockInfo.Resource, "R")).Append("=>")
                        .Append(Name(this.txNames, lockInfo.TxId, "T")).ToString();
                }
            }

            public string Format(IEnumerable<LockInfo> cycle) =>
                new StringBuilder("[").Append(string.Join(",", cycle.Select(this.Format)))
                    .Append("]").ToString();

            private static string Name<TKey>(Dictionary<TKey, string> dict, TKey k, string prefix)
                where TKey : struct
            {
                if (k.Equals(default(TKey)))
                {
                    return "BAD";
                }

                if (dict.TryGetValue(k, out var name))
                {
                    return name;
                }

                return dict[k] = prefix + dict.Count;
            }
        }

        [Fact]
        public async Task CauseDeadlock()
        {
            var tasks = new Task[]
            {
                this.Coordinator.RunOrdered(0, 1, 2, 3),
                this.Coordinator.RunOrdered(3, 2, 1, 0),
            };
            bool threw = false;
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception e)
            {
                this.testOutput($"threw {e}");
                threw = true;
            }

            Assert.True(threw, "bad ordering should throw!");

            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            this.testOutput($"Start at {DateTime.UtcNow}");
            await Task.WhenAll(
                this.Coordinator.RunOrdered(0, 1),
                this.Coordinator.RunOrdered(0, 1)
            );
            this.testOutput($"Done at {DateTime.UtcNow}");
        }

        private IDeadlockCoordinator Coordinator => this.grainFactory.GetGrain<IDeadlockCoordinator>(0);

        [Fact]
        public async Task OrderedAccessWorks()
        {
            var tasks = new[] {
                this.Coordinator.RunOrdered(0, 1),
                this.Coordinator.RunOrdered(0, 1)
            };
            await Task.WhenAll(tasks);
        }


        private int[] Shuffle(int[] input)
        {
            var random = new Random();
            var output = new int[input.Length];
            Array.Copy(input, output, input.Length);
            for (int i = 0; i < output.Length; i++)
            {
                int j = random.Next(output.Length);
                int tmp = output[j];
                output[j] = output[i];
                output[i] = tmp;
            }

            return output;
        }

    }
}