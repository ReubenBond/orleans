using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.CoreRPC;
using System.Threading.Tasks;
using Orleans.Serialization.Grpc;
using System.Threading;
using Grpc.Core;
using System;
using System.Linq;
using Orleans;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Orleans.Runtime;

await Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        services.AddStandaloneCoreRpc();
        services.AddHostedService<ServerService>();
        services.AddHostedService<ClientService>();
    })
    .RunConsoleAsync();

[GenerateSerializer]
public class MyComplexModel
{
    [Id(0)]
    public HashSet<string>? IgnoreCaseStrings { get; set; }

    [Id(1)]
    public HashSet<string>? OrdinalStrings { get; set; }


    [Id(2)]
    public Dictionary<string, DateTimeOffset>? Dates { get; set; }

    [Id(3)]
    public object? SomeReferenceType { get; set; }

    [Id(4)]
    public Exception? Exception { get;set; }

    public string ToDisplayString()
    {
        var result = new StringBuilder();
        if (IgnoreCaseStrings is { })
        {
            result.Append($"\tIgnoreCaseStrings: [{string.Join(", ", IgnoreCaseStrings)}], Comparer: {IgnoreCaseStrings.Comparer?.GetType()})");
        }
        else
        {
            result.Append($"\tIgnoreCaseStrings: null");
        }

        if (OrdinalStrings is { })
        {
            result.Append($"\n\tOrdinalStrings: [{string.Join(", ", OrdinalStrings)}], Comparer: {OrdinalStrings.Comparer?.GetType()})");
        }
        else
        {
            result.Append($"\n\tOrdinalStrings: null");
        }

        result.Append($"\n\tDates: [{(Dates is { } ? string.Join(", ", Dates.Select(d => $"[{d.Key}] = \"{d.Value}\"")) : "null")}]");
        result.Append($"\n\tSomeReferenceType: [{(SomeReferenceType is { } ? $"{RuntimeHelpers.GetHashCode(SomeReferenceType).ToString("X")} (is this? {ReferenceEquals(this, SomeReferenceType)})" : "null")}]");
        result.Append($"\n\tException: {(Exception is { } ? LogFormatter.PrintException(Exception) : "null")}");
        return result.ToString();
    }
}

public interface IHelloService : IRpcService
{
    ValueTask<string> SayHello(string input, string anotherThing);
    ValueTask<T> Echo<T>(T value);
}

public class HelloService : IHelloService
{
    private readonly ILogger<HelloService> _logger;
    public HelloService(ILogger<HelloService> logger) => _logger = logger;

    public ValueTask<T> Echo<T>(T value) => new(value);

    public ValueTask<string> SayHello(string input, string anotherThing)
    {
        _logger.LogInformation("Received request {Input}, {AnotherThing}", input, anotherThing);
        return new($"Hello, {input}! Oh, and another thing: {anotherThing}");
    }
}

public class ServerService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Server _server;

    public ServerService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _server = new Server(new[] { new ChannelOption(ChannelOptions.SoReuseport, 0) })
        {
            Ports = { new ServerPort("localhost", 9001, ServerCredentials.Insecure) },
            Services = {
                ServerServiceDefinition.CreateBuilder()
                    .AddService<HelloService>(_serviceProvider)
                    .Build()
            }
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _server.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _server.ShutdownAsync();
        return Task.CompletedTask;
    }
}

public class ClientService : BackgroundService
{
    private readonly CoreRpcClientFactory _clientFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ClientService> _logger;
    private readonly IHelloService _client;

    public ClientService(CoreRpcClientFactory clientFactory, IServiceProvider serviceProvider, ILogger<ClientService> logger)
    {
        _clientFactory = clientFactory;
        _serviceProvider = serviceProvider;
        _logger = logger;

        var channel = new Channel("localhost", 9001, ChannelCredentials.Insecure); ;
        var callInvoker = new DefaultCallInvoker(channel);
        _client = _clientFactory.CreateClient<IHelloService>(callInvoker);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Continuously call the server
        var count = 0;
        var complexValue = GetComplexValue();
        while (!stoppingToken.IsCancellationRequested)
        {
            {
                // Something simple to start
                var result = await _client.SayHello("friends", count++.ToString());
                _logger.LogInformation("Result: {Result}", result);
            }

            {
                // What about generic methods?
                var result = await _client.Echo<string>("hi");
                _logger.LogInformation("Echo simple value: {Result}", result);
            }

            {
                // How about generic parameter types?
                var result = await _client.Echo<(List<int>, string)>((new[] { 1, 2, 3 }.ToList(), "this works"));
                _logger.LogInformation("Echo generic type: {Result}", result);
            }

            {
                // How about complex types with generic parameters?
                var result = await _client.Echo(complexValue);
               _logger.LogInformation("Echo complex type:\n{Result}", result.ToDisplayString());
            }

            {
                // Referential equality across parameters?
                var result = await _client.Echo((One: complexValue, Two: complexValue));
                _logger.LogInformation("Does referential equality accross parameters work?: {Result}", ReferenceEquals(result.One, result.Two) ? "yes" : "no");
            }

            await Task.Delay(1000);
        }
    }

    private static MyComplexModel GetComplexValue()
    {
        var result = new MyComplexModel();
        result.IgnoreCaseStrings = new(StringComparer.OrdinalIgnoreCase) { "ONE", "one" };
        result.OrdinalStrings = new(StringComparer.Ordinal) { "ONE", "one" };
        result.Dates = new Dictionary<string, DateTimeOffset>
        {
            ["Today"] = DateTimeOffset.Now,
            ["Now, according to the Queen"] = DateTimeOffset.UtcNow,
            ["Yesty"] = DateTimeOffset.Now.AddDays(-1)
        };
        try
        {
            try
            {
                throw new InvalidOperationException("No!");
            }
            catch (Exception inner)
            {
                throw new ArgumentException("I assume this was an argument issue", inner);
            }
        }
        catch (Exception exception)
        {
            result.Exception = exception;
        }

        result.SomeReferenceType = result;
        return result;
    }
}