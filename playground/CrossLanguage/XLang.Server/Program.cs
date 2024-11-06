using Grpc.Core;
using Foo.Bar;

var builder = WebApplication.CreateBuilder(args);
builder.UseOrleans(o =>
{
    o.UseLocalhostClustering();
    o.AddGrpcGrains();
});

var services = builder.Services;

//services.AddCoreRpc();
//services.AddStandaloneCoreRpc();
//services.AddHostedService<ServerService>();

using var app = builder.Build();
await app.StartAsync();
var grainFactory = app.Services.GetRequiredService<IGrainFactory>();
var greeterGrainOrleans = grainFactory.GetGrain<IGreeterGrain>("falcon1");
var response = await greeterGrainOrleans.SayHello2(new HelloRequest { Name = "Orleans" });
Console.WriteLine("Orleans: " + response.Message);

await app.WaitForShutdownAsync();

public interface IGreeterGrain : IGrainWithStringKey
{
    Task<HelloReply> SayHello2(HelloRequest request);
}

[GrainType("greeter")]
public sealed class GreeterGrain : Greeter.GreeterBase, IGreeterGrain
{
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
    {
        return Task.FromResult(new HelloReply{ Message = "Hello " + request.Name });
    }

    public Task<HelloReply> SayHello2(HelloRequest request) => 
        Task.FromResult(new HelloReply{ Message = "Hello 2 " + request.Name });
}