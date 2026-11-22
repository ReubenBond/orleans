using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Consensus.CasPaxos.Grpc;
using Orleans.Consensus.CasPaxos.Server;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Hosts the production register gRPC adapter over a real, dynamically bound HTTP/2 socket.
/// The caller owns the supplied register service.
/// </summary>
internal sealed class LoopbackRegisterHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ConcurrentQueue<Request> _requests;
    private int _stopped;
    private int _disposed;

    private LoopbackRegisterHost(WebApplication application, Uri address, ConcurrentQueue<Request> requests)
    {
        _application = application;
        Address = address;
        _requests = requests;
    }

    internal Uri Address { get; }

    internal Request[] Requests => [.. _requests];

    internal static async Task<LoopbackRegisterHost> StartAsync(
        IRegisterService registers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registers);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        _ = builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        _ = builder.Services.AddGrpc();
        _ = builder.Services.AddSingleton(registers);
        var application = builder.Build();
        var requests = new ConcurrentQueue<Request>();
        _ = application.Use(async (context, next) =>
        {
            requests.Enqueue(new Request(context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
            await next(context).ConfigureAwait(false);
        });
        _ = application.MapGrpcService<RegisterGrpcService>();
        try
        {
            // StartAsync is the readiness barrier; no port reservation race or sleeping oracle.
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("The register host did not publish its listening address.");
            var address = new Uri(Assert.Single(addresses), UriKind.Absolute);
            return new LoopbackRegisterHost(application, address, requests);
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            await _application.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // This real-I/O watchdog only bounds cleanup; elapsed time is not a correctness oracle.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), TimeProvider.System);
        try
        {
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal sealed record Request(string Path, string Protocol);
}
