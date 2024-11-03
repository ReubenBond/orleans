using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Hosting;

public static class OrleansGrpcClientExtensions
{
    public static IHostApplicationBuilder AddGrpcGrains(this IHostApplicationBuilder builder)
    {
        return builder;
    }
}

public sealed class GrpcGrainClientFactory
{
    public TClient GetGrpcGrainClient<TClient>(string grainType, string grainKey) where TClient : ClientBase<TClient>
    {
        // create a call invoker which wraps another invoker and adds grain headers
        // create the client type, providing the invoker
        // return the client
    }
}

public class OrleansServerCallContext(DateTime deadline, CancellationToken cancellationToken) : ServerCallContext
{
    protected override string MethodCore { get; } = "Method";
    protected override string HostCore { get; } = "host";
    protected override string PeerCore { get; } = "peer";
    protected override DateTime DeadlineCore { get; } = deadline;
    protected override Metadata RequestHeadersCore { get; } = Metadata.Empty;
    protected override CancellationToken CancellationTokenCore { get; } = cancellationToken;
    protected override Metadata ResponseTrailersCore { get; } = Metadata.Empty;
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore { get; } = new AuthContext(null, []);

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
    {
        throw new NotImplementedException();
    }

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        throw new NotImplementedException();
    }
}