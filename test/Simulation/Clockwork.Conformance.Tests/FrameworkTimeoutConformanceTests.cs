using System.Reflection;
using Orleans.Testing.Simulation.Runtime;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the framework members whose BCL implementations time out on a real timer or block the
/// caller on asynchronous work: <c>HttpClient</c>'s sending members and the JSON extensions (<c>HttpClient</c> family),
/// which reject a finite <c>HttpClient.Timeout</c>; <c>WebSocketStream.Create</c> (<c>WebSockets</c> family), which
/// rejects an owning stream with a finite close timeout; and the hosting extensions (<c>Hosting</c> family), which wait
/// with the controlled wait and time out in virtual time. Each controlled outcome the BCL can produce deterministically is
/// compared with an uninstrumented run, and each rule is shown to matter by a probe staged without it.
/// </summary>
public sealed class FrameworkTimeoutConformanceTests : IDisposable
{
    private const string ProbeType = "Conf.FrameworkTimeoutProbe";

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Net;
        using System.Net.Http;
        using System.Net.Http.Json;
        using System.Net.WebSockets;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.Hosting;

        namespace Conf
        {
            // Answers each request after a delay with its method, path, and body, or with JSON for /json.
            public sealed class DelayedHandler : HttpMessageHandler
            {
                private readonly TimeSpan _delay;

                public DelayedHandler(TimeSpan delay) => _delay = delay;

                protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    await Task.Delay(_delay, cancellationToken);
                    return Respond(request, request.Content is null ? "" : " " + await request.Content.ReadAsStringAsync(cancellationToken));
                }

                protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
                    Respond(request, " sync");

                private static HttpResponseMessage Respond(HttpRequestMessage request, string suffix) =>
                    request.RequestUri.AbsolutePath == "/json"
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("42", Encoding.UTF8, "application/json") }
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.Method + " " + request.RequestUri.AbsolutePath + suffix) };
            }

            // Marks the responses its override returns, which reach HttpClient's implementation through a base call.
            public sealed class MarkingClient : HttpClient
            {
                public MarkingClient(HttpMessageHandler handler) : base(handler)
                {
                }

                public override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
                    response.Headers.Add("X-Marked", "yes");
                    return response;
                }
            }

            // Answers every request itself, without HttpClient's implementation, so its timeout bounds nothing.
            public sealed class CannedClient : HttpClient
            {
                public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                    Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));

                public override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
                    new HttpResponseMessage(HttpStatusCode.Created);
            }

            // Marks the responses its override returns, which reach HttpMessageInvoker's implementation through a base call.
            public sealed class MarkingInvoker : HttpMessageInvoker
            {
                public MarkingInvoker(HttpMessageHandler handler) : base(handler)
                {
                }

                public override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
                    response.Headers.Add("X-Marked", "yes");
                    return response;
                }
            }

            // A WebSocket which records its close, which takes the given time.
            public sealed class RecordingWebSocket : WebSocket
            {
                private readonly TimeSpan _closeDuration;
                private WebSocketState _state = WebSocketState.Open;

                public RecordingWebSocket(TimeSpan closeDuration) => _closeDuration = closeDuration;

                public List<string> Log { get; } = new List<string>();

                public override WebSocketCloseStatus? CloseStatus => null;

                public override string CloseStatusDescription => null;

                public override WebSocketState State => _state;

                public override string SubProtocol => null;

                public override void Abort() => Log.Add("aborted");

                public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
                {
                    try
                    {
                        await Task.Delay(_closeDuration, cancellationToken);
                        Log.Add("closed");
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Add("close cancelled");
                    }

                    _state = WebSocketState.Closed;
                }

                public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) =>
                    Task.CompletedTask;

                public override void Dispose() => Log.Add("disposed");

                public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
                    throw new NotSupportedException();

                public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
                    Task.CompletedTask;
            }

            public sealed class Lifetime : IHostApplicationLifetime, IServiceProvider
            {
                private readonly CancellationTokenSource _started = new CancellationTokenSource();
                private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
                private readonly CancellationTokenSource _stopped = new CancellationTokenSource();

                public CancellationToken ApplicationStarted => _started.Token;

                public CancellationToken ApplicationStopping => _stopping.Token;

                public CancellationToken ApplicationStopped => _stopped.Token;

                public void StopApplication() => _stopping.Cancel();

                public object GetService(Type serviceType) => serviceType == typeof(IHostApplicationLifetime) ? this : null;
            }

            // A host whose start takes 10 ms and whose stop takes the given time, unless its token cancels it first.
            public sealed class RecordingHost : IHost
            {
                private readonly TimeSpan _stopDuration;
                private readonly Lifetime _lifetime = new Lifetime();
                private readonly TaskCompletionSource _started = new TaskCompletionSource();

                public RecordingHost(TimeSpan stopDuration) => _stopDuration = stopDuration;

                public List<string> Log { get; } = new List<string>();

                public IServiceProvider Services => _lifetime;

                public void RequestStop() => _lifetime.StopApplication();

                public async Task StartAsync(CancellationToken cancellationToken = default)
                {
                    await Task.Delay(10, cancellationToken);
                    Log.Add("started");
                    _started.TrySetResult();
                }

                public Task Started => _started.Task;

                public async Task StopAsync(CancellationToken cancellationToken = default)
                {
                    Log.Add("stopping");
                    try
                    {
                        await Task.Delay(_stopDuration, cancellationToken);
                        Log.Add("stopped");
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Add("stop timed out");
                    }
                }

                public void Dispose() => Log.Add("disposed");
            }

            // Builds a RecordingHost.
            public sealed class RecordingHostBuilder : IHostBuilder
            {
                public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

                public IHost Build() => new RecordingHost(TimeSpan.Zero);

                public IHostBuilder ConfigureHostConfiguration(Action<Microsoft.Extensions.Configuration.IConfigurationBuilder> configureDelegate) => this;

                public IHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, Microsoft.Extensions.Configuration.IConfigurationBuilder> configureDelegate) => this;

                public IHostBuilder ConfigureServices(Action<HostBuilderContext, Microsoft.Extensions.DependencyInjection.IServiceCollection> configureDelegate) => this;

                public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(Microsoft.Extensions.DependencyInjection.IServiceProviderFactory<TContainerBuilder> factory) => this;

                public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(Func<HostBuilderContext, Microsoft.Extensions.DependencyInjection.IServiceProviderFactory<TContainerBuilder>> factory) => this;

                public IHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate) => this;
            }

            public static class FrameworkTimeoutProbe
            {
                private static HttpClient InfiniteClient(TimeSpan delay) =>
                    new HttpClient(new DelayedHandler(delay)) { BaseAddress = new Uri("http://sim/"), Timeout = Timeout.InfiniteTimeSpan };

                public static async Task<string> SendsThroughAClientWithAnInfiniteTimeout()
                {
                    var log = new List<string>();
                    using HttpClient client = InfiniteClient(TimeSpan.FromMilliseconds(5));
                    log.Add(await client.GetStringAsync("a"));
                    log.Add(await (await client.PostAsync("b", new StringContent("x"))).Content.ReadAsStringAsync());
                    log.Add(await (await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "c"))).Content.ReadAsStringAsync());
                    using (var reader = new StreamReader(client.Send(new HttpRequestMessage(HttpMethod.Get, "d")).Content.ReadAsStream()))
                    {
                        log.Add(reader.ReadToEnd());
                    }

                    log.Add((await client.GetFromJsonAsync<int>("json")).ToString());
                    log.Add(await (await client.PostAsJsonAsync("e", 7)).Content.ReadAsStringAsync());

                    HttpMessageInvoker invoker = new HttpMessageInvoker(new DelayedHandler(TimeSpan.FromMilliseconds(5)));
                    log.Add(await (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://sim/f"), CancellationToken.None)).Content.ReadAsStringAsync());

                    using var marking = new MarkingClient(new DelayedHandler(TimeSpan.FromMilliseconds(5))) { Timeout = Timeout.InfiniteTimeSpan };
                    HttpResponseMessage marked = await marking.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://sim/g"), CancellationToken.None);
                    log.Add(await marked.Content.ReadAsStringAsync() + " marked:" + marked.Headers.Contains("X-Marked"));

                    using var markingInvoker = new MarkingInvoker(new DelayedHandler(TimeSpan.FromMilliseconds(5)));
                    HttpResponseMessage invoked = await markingInvoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://sim/h"), CancellationToken.None);
                    log.Add(await invoked.Content.ReadAsStringAsync() + " marked:" + invoked.Headers.Contains("X-Marked"));

                    // A client type's override which answers itself starts no timer, whatever the client's timeout.
                    using var canned = new CannedClient();
                    HttpMessageInvoker cannedInvoker = canned;
                    log.Add("canned:" + (await canned.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://sim/i"), CancellationToken.None)).StatusCode
                        + "," + (await cannedInvoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://sim/i"), CancellationToken.None)).StatusCode
                        + "," + cannedInvoker.Send(new HttpRequestMessage(HttpMethod.Get, "http://sim/i"), CancellationToken.None).StatusCode);
                    return string.Join("|", log);
                }

                // The timeout is shorter than the response, which the BCL cancels once it elapses.
                public static Task<string> ATimeoutShorterThanTheResponse() =>
                    ATimeoutShorterThanTheResponse(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

                // The same, at a scale no host is slow enough for a real timer to reach, within the ten minutes a drive advances.
                public static Task<string> ALongTimeoutShorterThanTheResponse() =>
                    ATimeoutShorterThanTheResponse(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8));

                private static async Task<string> ATimeoutShorterThanTheResponse(TimeSpan timeout, TimeSpan response)
                {
                    using var client = new HttpClient(new DelayedHandler(response)) { Timeout = timeout };
                    try
                    {
                        return await client.GetStringAsync("http://sim/slow");
                    }
                    catch (TaskCanceledException exception)
                    {
                        return "timed out:" + (exception.InnerException is TimeoutException);
                    }
                }

                public static Task DefaultClient() => new HttpClient(new DelayedHandler(TimeSpan.Zero)).GetStringAsync("http://sim/a");

                // The override reaches HttpClient's implementation through its base call, which the timeout bounds.
                public static Task MarkingClientWithTheDefaultTimeout() =>
                    new MarkingClient(new DelayedHandler(TimeSpan.Zero)).SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://sim/a"), CancellationToken.None);

                public static Task InvokerOverAClient()
                {
                    HttpMessageInvoker invoker = new HttpClient(new DelayedHandler(TimeSpan.Zero));
                    return invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://sim/a"), CancellationToken.None);
                }

                public static Task JsonOverADefaultClient() =>
                    new HttpClient(new DelayedHandler(TimeSpan.Zero)).GetFromJsonAsync<int>("http://sim/json");

                // The streams which need no timer: one which does not own its socket, and owning ones whose close
                // timeout is zero (a cancelled token) or infinite, with the BCL's validation.
                public static async Task<string> StreamsWhoseCloseNeedsNoTimer()
                {
                    var log = new List<string>();
                    var socket = new RecordingWebSocket(TimeSpan.FromMilliseconds(5));
                    await WebSocketStream.Create(socket, WebSocketMessageType.Binary).DisposeAsync();
                    log.Add("unowned:" + string.Join(",", socket.Log));

                    socket = new RecordingWebSocket(TimeSpan.FromMilliseconds(5));
                    await WebSocketStream.Create(socket, WebSocketMessageType.Binary, ownsWebSocket: false).DisposeAsync();
                    log.Add("not owned:" + string.Join(",", socket.Log));

                    socket = new RecordingWebSocket(TimeSpan.FromMilliseconds(5));
                    await WebSocketStream.Create(socket, WebSocketMessageType.Binary, TimeSpan.Zero).DisposeAsync();
                    log.Add("zero:" + string.Join(",", socket.Log));

                    socket = new RecordingWebSocket(TimeSpan.FromMilliseconds(5));
                    await WebSocketStream.Create(socket, WebSocketMessageType.Text, Timeout.InfiniteTimeSpan).DisposeAsync();
                    log.Add("infinite:" + string.Join(",", socket.Log));

                    log.Add(Describe(() => WebSocketStream.Create(null, WebSocketMessageType.Binary, ownsWebSocket: false)));
                    log.Add(Describe(() => WebSocketStream.Create(socket, WebSocketMessageType.Binary, TimeSpan.FromSeconds(-2))));
                    return string.Join("|", log);
                }

                // The close takes longer than the stream's close timeout, which the BCL cancels once it elapses.
                public static Task<string> ACloseLongerThanTheTimeout() =>
                    ACloseLongerThanTheTimeout(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

                public static Task<string> ALongCloseTimeoutShorterThanTheClose() =>
                    ACloseLongerThanTheTimeout(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8));

                private static async Task<string> ACloseLongerThanTheTimeout(TimeSpan timeout, TimeSpan close)
                {
                    var socket = new RecordingWebSocket(close);
                    await WebSocketStream.Create(socket, WebSocketMessageType.Binary, timeout).DisposeAsync();
                    return string.Join(",", socket.Log);
                }

                public static void OwningStream() =>
                    WebSocketStream.Create(new RecordingWebSocket(TimeSpan.Zero), WebSocketMessageType.Binary, ownsWebSocket: true);

                public static void StreamWithAFiniteCloseTimeout() =>
                    WebSocketStream.Create(new RecordingWebSocket(TimeSpan.Zero), WebSocketMessageType.Binary, TimeSpan.FromSeconds(1));

                // Starts a host, waits for its shutdown, which another operation requests, then runs a second host.
                public static string StartWaitForShutdownAndRun()
                {
                    var first = new RecordingHost(TimeSpan.FromMilliseconds(5));
                    first.Start();
                    first.Log.Add("start returned");
                    Task.Run(async () =>
                    {
                        await Task.Delay(20);
                        first.RequestStop();
                    });
                    first.WaitForShutdown();
                    first.Log.Add("shut down");

                    var second = new RecordingHost(TimeSpan.FromMilliseconds(5));
                    Task.Run(async () =>
                    {
                        await Task.Delay(50);
                        second.RequestStop();
                    });
                    second.Run();
                    second.Log.Add("run returned");
                    return string.Join(",", first.Log) + "|" + string.Join(",", second.Log);
                }

                // A stop shorter than the timeout completes; a longer one is cancelled once the timeout elapses.
                public static Task<string> StopWithATimeout() => StopWithATimeout(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

                public static Task<string> StopWithALongTimeout() => StopWithATimeout(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8));

                private static async Task<string> StopWithATimeout(TimeSpan timeout, TimeSpan slowStop)
                {
                    var quick = new RecordingHost(TimeSpan.FromMilliseconds(10));
                    await quick.StopAsync(timeout);
                    var slow = new RecordingHost(slowStop);
                    await slow.StopAsync(timeout);
                    return string.Join(",", quick.Log) + "|" + string.Join(",", slow.Log);
                }

                public static void StartAHost() => new RecordingHost(TimeSpan.Zero).Start();

                public static string StartABuilder() => string.Join(",", ((RecordingHost)new RecordingHostBuilder().Start()).Log);

                public static async Task<string> StartABuilderAsync() =>
                    string.Join(",", ((RecordingHost)await new RecordingHostBuilder().StartAsync()).Log);

                // Runs a host until a token which a timer cancels triggers its shutdown. The timer starts once the host
                // has started, so a slow start under the BCL cannot be cancelled instead.
                public static async Task<string> RunUntilATokenIsCancelled()
                {
                    var host = new RecordingHost(TimeSpan.FromMilliseconds(5));
                    using var shutdown = new CancellationTokenSource();
                    Task run = host.RunAsync(shutdown.Token);
                    await host.Started;
                    shutdown.CancelAfter(50);
                    await run;
                    return string.Join(",", host.Log);
                }

                private static string Describe(Func<object> create)
                {
                    try
                    {
                        create();
                        return "created";
                    }
                    catch (ArgumentException exception)
                    {
                        return exception.GetType().Name + ":" + exception.ParamName;
                    }
                }
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;
    private readonly Lazy<UninstrumentedProbe> _bcl;

    public FrameworkTimeoutConformanceTests()
    {
        _release = new(() => _fixture.StageControlledTasks("Conf.FrameworkTimeoutRel", ProbeType, Source, optimize: true));
        _debug = new(() => _fixture.StageControlledTasks("Conf.FrameworkTimeoutDbg", ProbeType, Source, optimize: false));
        _bcl = new(() => _fixture.CompileUninstrumented("Conf.FrameworkTimeoutBcl", ProbeType, Source));
    }

    [Theory]
    [InlineData("SendsThroughAClientWithAnInfiniteTimeout")]
    [InlineData("StreamsWhoseCloseNeedsNoTimer")]
    [InlineData("StopWithATimeout")]
    [InlineData("RunUntilATokenIsCancelled")]
    [InlineData("StartABuilderAsync")]
    public async Task ControlledOutcomesMatchTheBcl(string probe)
    {
        string bcl = await ((Task<string>)_bcl.Value.Method(probe).Invoke(null, null)!)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        foreach (bool optimize in new[] { true, false })
        {
            using var host = new SimulationHost(Start);

            Assert.Equal(bcl, Completed<string>(host.Invoke(Method(probe, optimize))));
        }
    }

    [Theory]
    [InlineData("StartWaitForShutdownAndRun", "started,start returned,stopping,stopped,shut down|started,stopping,stopped,disposed,run returned")]
    [InlineData("StartABuilder", "started")]
    public void ControlledOutcomesMatchTheBclForTheBlockingHostExtensions(string probe, string expected)
    {
        string bcl = (string)_bcl.Value.Method(probe).Invoke(null, null)!;
        foreach (bool optimize in new[] { true, false })
        {
            using var host = new SimulationHost(Start);

            Assert.Equal(bcl, (string)host.Invoke(Method(probe, optimize))!);
        }

        Assert.Equal(expected, bcl);
    }

    [Theory]
    [InlineData("DefaultClient", "System.Net.Http.HttpClient.GetStringAsync")]
    [InlineData("InvokerOverAClient", "System.Net.Http.HttpMessageInvoker.SendAsync")]
    [InlineData("JsonOverADefaultClient", "System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync")]
    [InlineData("ATimeoutShorterThanTheResponse", "System.Net.Http.HttpClient.GetStringAsync")]
    [InlineData("OwningStream", "System.Net.WebSockets.WebSocketStream.Create")]
    [InlineData("StreamWithAFiniteCloseTimeout", "System.Net.WebSockets.WebSocketStream.Create")]
    [InlineData("ACloseLongerThanTheTimeout", "System.Net.WebSockets.WebSocketStream.Create")]
    [InlineData("MarkingClientWithTheDefaultTimeout", "System.Net.Http.HttpClient.SendAsync")]
    public void ARealTimerIsRejected(string probe, string apiName)
    {
        using var host = new SimulationHost(Start);
        object? result = null;

        // An async probe's rejection faults the task it returns.
        Exception? thrown = Record.Exception(() => { result = host.Invoke(Method(probe)); });
        Exception? rejection = thrown ?? Assert.IsAssignableFrom<Task>(result).Exception?.InnerException;

        Assert.Equal(apiName, Assert.IsType<SimulationApiException>(rejection).ApiName);
    }

    // Under the BCL the real timer cancels the work once it elapses. Without the rules the real timer never fires before
    // the work, which takes longer in virtual time but completes at once in real time, so the escaped run completes the
    // work the BCL cancels. The escaped run's timer is minutes long, so its outcome does not depend on the host's speed.
    [Theory]
    [InlineData("ATimeoutShorterThanTheResponse", "ALongTimeoutShorterThanTheResponse", "clockwork.http.", "timed out:True", "GET /slow")]
    [InlineData("ACloseLongerThanTheTimeout", "ALongCloseTimeoutShorterThanTheClose", "clockwork.websocketstream.", "close cancelled,disposed", "closed,disposed")]
    [InlineData("StopWithATimeout", "StopWithALongTimeout", "clockwork.hosting.", "stopping,stopped|stopping,stop timed out", "stopping,stopped|stopping,stopped")]
    public async Task WithoutTheRulesARealTimerRacesVirtualTime(string probe, string escapingProbe, string rulePrefix, string bclOutcome, string escaped)
    {
        string bcl = await ((Task<string>)_bcl.Value.Method(probe).Invoke(null, null)!)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        StagedProbe staged = _fixture.StageControlledTasksWithout(
            "Conf.FrameworkTimeoutEscape." + escapingProbe,
            ProbeType,
            Source,
            rule => rule.Id.StartsWith(rulePrefix, StringComparison.Ordinal));
        using var host = new SimulationHost(Start);

        Assert.Equal(bclOutcome, bcl);
        Assert.Equal(escaped, Completed<string>(host.Invoke(staged.Method(escapingProbe))));
    }

    [Theory]
    [InlineData("StartAHost")]
    [InlineData("StartABuilder")]
    public void WithoutTheHostingRulesStartBlocksTheLogicalThread(string probe)
    {
        StagedProbe staged = _fixture.StageControlledTasksWithout(
            "Conf.FrameworkTimeoutEscape." + probe,
            ProbeType,
            Source,
            rule => rule.Id.StartsWith("clockwork.hosting.", StringComparison.Ordinal));
        using var host = new SimulationHost(Start, hangBound: TimeSpan.FromSeconds(5));

        // The BCL's Start blocks on the host's start, whose virtual delay only the blocked logical thread could run.
        Assert.Throws<TimeoutException>(() => host.Invoke(staged.Method(probe)));
    }

    private static T Completed<T>(object? task)
    {
        var typed = Assert.IsAssignableFrom<Task<T>>(task);
        Assert.Equal(TaskStatus.RanToCompletion, typed.Status);
        return typed.GetAwaiter().GetResult();
    }

    private MethodInfo Method(string name, bool optimize = true) => (optimize ? _release : _debug).Value.Method(name);

    public void Dispose() => _fixture.Dispose();
}
