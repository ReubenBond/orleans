// See https://aka.ms/new-console-template for more information
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

#pragma warning disable IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.
using var host = Host.CreateDefaultBuilder(args).UseOrleans(siloBuilder => siloBuilder.UseLocalhostClustering()).Build();
#pragma warning restore IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.

await host.StartAsync();
var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var response = await grainFactory.GetGrain<IEchoGrain>(Guid.NewGuid()).Echo("Hello World!");
Console.WriteLine($"Echo response: {response}");
Console.WriteLine("bye");
await host.StopAsync();

public interface IEchoGrain : IGrainWithGuidKey
{
    Task<string> Echo(string message);
}   

public class EchoGrain : Grain, IEchoGrain
{
    public Task<string> Echo(string message) => Task.FromResult(message);
}
