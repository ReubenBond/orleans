using System.Collections.Concurrent;
using Orleans.Testing.Simulation.Runtime;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the framework entry points which the concurrency-escape audit found starting work outside
/// the simulation, and which are rejected at the call site: <c>BackgroundWorker.RunWorkerAsync</c> and
/// <c>BackgroundService.StartAsync</c> (<c>BackgroundWork</c> family), every TPL Dataflow block constructor and the
/// <c>DataflowBlock</c> helpers which schedule work or wait (<c>Dataflow</c> family), and the <c>WebSocket</c> factories
/// over a stream (<c>WebSockets</c> family). Each throws a <see cref="SimulationApiException"/> naming the API before the
/// call runs, so none of the application's work starts. Rewritten without the rules, a Dataflow block and a
/// <c>BackgroundService</c> run the application's work on threads outside the simulation.
/// </summary>
public sealed class RejectedFrameworkWorkConformanceTests : IDisposable
{
    private const string ProbeType = "Conf.RejectedFrameworkWorkProbe";

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // The helpers are rejected before they run, so their null arguments are never read.
    private const string Source = """
        using System;
        using System.ComponentModel;
        using System.IO;
        using System.Net.WebSockets;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Threading.Tasks.Dataflow;
        using Microsoft.Extensions.Hosting;

        namespace Conf
        {
            public sealed class ReportingService : BackgroundService
            {
                private readonly Action _report;

                public ReportingService(Action report) => _report = report;

                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _report();
                    return Task.CompletedTask;
                }
            }

            public static class RejectedFrameworkWorkProbe
            {
                public static void Begin(string entryPoint, Action report)
                {
                    switch (entryPoint)
                    {
                        case "RunWorkerAsync":
                        {
                            var worker = new BackgroundWorker();
                            worker.DoWork += (_, _) => report();
                            worker.RunWorkerAsync();
                            break;
                        }
                        case "RunWorkerAsyncArgument":
                        {
                            var worker = new BackgroundWorker();
                            worker.DoWork += (_, _) => report();
                            worker.RunWorkerAsync(1);
                            break;
                        }
                        case "StartAsync":
                            _ = new ReportingService(report).StartAsync(CancellationToken.None);
                            break;
                        case "ActionBlock":
                            new ActionBlock<int>(_ => report()).Post(1);
                            break;
                        case "ActionBlockAsync":
                            new ActionBlock<int>(_ => { report(); return Task.CompletedTask; }, new ExecutionDataflowBlockOptions()).Post(1);
                            break;
                        case "TransformBlock":
                            new TransformBlock<int, int>(item => { report(); return item; }).Post(1);
                            break;
                        case "TransformManyBlock":
                            new TransformManyBlock<int, int>(item => { report(); return new[] { item }; }).Post(1);
                            break;
                        case "BroadcastBlock":
                            new BroadcastBlock<int>(item => { report(); return item; }).Post(1);
                            break;
                        case "WriteOnceBlock":
                            new WriteOnceBlock<int>(item => { report(); return item; }).Post(1);
                            break;
                        case "BufferBlock":
                            new BufferBlock<int>().Post(1);
                            break;
                        case "BatchBlock":
                            new BatchBlock<int>(1).Post(1);
                            break;
                        case "JoinBlock":
                            new JoinBlock<int, int>().Target1.Post(1);
                            break;
                        case "JoinBlock3":
                            new JoinBlock<int, int, int>().Target1.Post(1);
                            break;
                        case "BatchedJoinBlock":
                            new BatchedJoinBlock<int, int>(1).Target1.Post(1);
                            break;
                        case "BatchedJoinBlock3":
                            new BatchedJoinBlock<int, int, int>(1).Target1.Post(1);
                            break;
                        case "Choose":
                            _ = DataflowBlock.Choose<int, int>(null!, _ => report(), null!, _ => report());
                            break;
                        case "Receive":
                            _ = DataflowBlock.Receive<int>(null!);
                            break;
                        case "ReceiveAsync":
                            _ = DataflowBlock.ReceiveAsync<int>(null!, TimeSpan.FromSeconds(1));
                            break;
                        case "ReceiveAllAsync":
                            _ = DataflowBlock.ReceiveAllAsync<int>(null!);
                            break;
                        case "SendAsync":
                            _ = DataflowBlock.SendAsync<int>(null!, 1);
                            break;
                        case "OutputAvailableAsync":
                            _ = DataflowBlock.OutputAvailableAsync<int>(null!);
                            break;
                        case "CreateFromStream":
                            _ = WebSocket.CreateFromStream(Stream.Null, new WebSocketCreationOptions());
                            break;
                        case "CreateFromStreamKeepAlive":
                            _ = WebSocket.CreateFromStream(Stream.Null, isServer: false, subProtocol: null, TimeSpan.FromMilliseconds(1));
                            break;
                        case "CreateClientWebSocket":
                            _ = WebSocket.CreateClientWebSocket(Stream.Null, null, 1024, 1024, TimeSpan.Zero, false, WebSocket.CreateClientBuffer(1024, 1024));
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(entryPoint));
                    }
                }
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public RejectedFrameworkWorkConformanceTests() =>
        _probe = new(() => _fixture.StageControlledTasks("Conf.RejectedFrameworkWork", ProbeType, Source, optimize: true));

    public static TheoryData<string, string> EntryPoints => new()
    {
        { "RunWorkerAsync", "System.ComponentModel.BackgroundWorker.RunWorkerAsync" },
        { "RunWorkerAsyncArgument", "System.ComponentModel.BackgroundWorker.RunWorkerAsync" },
        { "StartAsync", "Microsoft.Extensions.Hosting.BackgroundService.StartAsync" },
        { "ActionBlock", "System.Threading.Tasks.Dataflow.ActionBlock`1..ctor" },
        { "ActionBlockAsync", "System.Threading.Tasks.Dataflow.ActionBlock`1..ctor" },
        { "TransformBlock", "System.Threading.Tasks.Dataflow.TransformBlock`2..ctor" },
        { "TransformManyBlock", "System.Threading.Tasks.Dataflow.TransformManyBlock`2..ctor" },
        { "BroadcastBlock", "System.Threading.Tasks.Dataflow.BroadcastBlock`1..ctor" },
        { "WriteOnceBlock", "System.Threading.Tasks.Dataflow.WriteOnceBlock`1..ctor" },
        { "BufferBlock", "System.Threading.Tasks.Dataflow.BufferBlock`1..ctor" },
        { "BatchBlock", "System.Threading.Tasks.Dataflow.BatchBlock`1..ctor" },
        { "JoinBlock", "System.Threading.Tasks.Dataflow.JoinBlock`2..ctor" },
        { "JoinBlock3", "System.Threading.Tasks.Dataflow.JoinBlock`3..ctor" },
        { "BatchedJoinBlock", "System.Threading.Tasks.Dataflow.BatchedJoinBlock`2..ctor" },
        { "BatchedJoinBlock3", "System.Threading.Tasks.Dataflow.BatchedJoinBlock`3..ctor" },
        { "Choose", "System.Threading.Tasks.Dataflow.DataflowBlock.Choose" },
        { "Receive", "System.Threading.Tasks.Dataflow.DataflowBlock.Receive" },
        { "ReceiveAsync", "System.Threading.Tasks.Dataflow.DataflowBlock.ReceiveAsync" },
        { "ReceiveAllAsync", "System.Threading.Tasks.Dataflow.DataflowBlock.ReceiveAllAsync" },
        { "SendAsync", "System.Threading.Tasks.Dataflow.DataflowBlock.SendAsync" },
        { "OutputAvailableAsync", "System.Threading.Tasks.Dataflow.DataflowBlock.OutputAvailableAsync" },
        { "CreateFromStream", "System.Net.WebSockets.WebSocket.CreateFromStream" },
        { "CreateFromStreamKeepAlive", "System.Net.WebSockets.WebSocket.CreateFromStream" },
        { "CreateClientWebSocket", "System.Net.WebSockets.WebSocket.CreateClientWebSocket" },
    };

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void EveryEntryPointIsRejectedBeforeTheWorkStarts(string entryPoint, string apiName)
    {
        using var host = new SimulationHost(Start);
        int reports = 0;

        var exception = Assert.Throws<SimulationApiException>(
            () => host.Invoke(_probe.Value.Method("Begin"), entryPoint, (Action)(() => Interlocked.Increment(ref reports))));

        Assert.Equal(apiName, exception.ApiName);
        Assert.Equal(0, reports);
    }

    [Theory]
    [InlineData("ActionBlock", "clockwork.dataflow.")]
    [InlineData("StartAsync", "clockwork.backgroundservice.")]
    public void WithoutTheRulesTheApplicationsWorkRunsOutsideTheSimulation(string entryPoint, string rulePrefix)
    {
        StagedProbe escaping = _fixture.StageControlledTasksWithout(
            "Conf.RejectedFrameworkWorkEscape." + entryPoint,
            ProbeType,
            Source,
            rule => rule.Id.StartsWith(rulePrefix, StringComparison.Ordinal));
        // The guard is off: the escape this test pins runs the work on the thread pool.
        using var host = new SimulationHost(Start, allowThreadPoolEscapes: true);
        var observed = new ConcurrentQueue<bool>();

        host.Invoke(escaping.Method("Begin"), entryPoint, (Action)(() => observed.Enqueue(host.Scheduler.IsSimulationThread)));

        // The escaped work runs on the thread pool after the probe returns, so the test waits for its report.
        Assert.True(SpinWait.SpinUntil(() => !observed.IsEmpty, TimeSpan.FromSeconds(30)));
        Assert.False(Assert.Single(observed));
    }

    public void Dispose() => _fixture.Dispose();
}
