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

await Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        services.AddStandaloneCoreRpc();
        services.AddHostedService<ServerService>();
        services.AddHostedService<ClientService>();
    })
    .RunConsoleAsync();

public interface IHelloService : IRpcService
{
    ValueTask<string> SayHello(string input, string anotherThing);
}

public class HelloService : IHelloService
{
    private readonly ILogger<HelloService> _logger;
    public HelloService(ILogger<HelloService> logger) => _logger = logger;
    public ValueTask<string> SayHello(string input, string anotherThing)
    {
        _logger.LogInformation("Received request {Input}, {AnotherThing}", input, anotherThing);
        return new($"Hello, {input}. Oh, and another thing: {anotherThing}");
    }
}

public class ServerService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ServerService> _logger;
    private readonly Server _server;

    public ServerService(IServiceProvider serviceProvider, ILogger<ServerService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
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
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await _client.SayHello("Joe", count++.ToString());
            _logger.LogInformation("Result: {Result}", result);
            await Task.Delay(1000);
        }
    }
}