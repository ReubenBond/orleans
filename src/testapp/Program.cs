// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

#if false

object stringifier = new Stringifier();
GoStringIt.Go(stringifier);

public static class GoStringIt
{
    public static void Go(object s)
    {
        Console.WriteLine(((IStringifier)s).GetName<DayOfWeek>(DayOfWeek.Tuesday));
    }
}

public struct MyStruct
{
    public int i;
    public string j;
}
/*
#pragma warning disable IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.
var services = new ServiceCollection()
//    .AddSingleton(typeof(IDam<>), typeof(Dam<>))
    .AddSingleton(typeof(IList<>), typeof(List<>))
    .AddSingleton<NeedsValueGeneric>()
    .BuildServiceProvider();

services.GetRequiredService<NeedsValueGeneric>();
services.GetRequiredService<IList<DayOfWeek>>();

public class NeedsValueGeneric
{
    public NeedsValueGeneric(IStringifier stringer)
    {
    }
}
#pragma warning restore IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.
*/

public interface IStringifier
{
    string GetName<[DynamicallyAccessedMembers(All)] T>(T value);
}

public class Stringifier : IStringifier
{
    public string GetName<[DynamicallyAccessedMembers(All)] T>(T value)
    {
        if (typeof(T).IsEnum)
        {
            return value!.ToString() ?? "no";
        }

        return typeof(T).Name;
    }
}
#else
/*
while (!Debugger.IsAttached)
{
    Console.WriteLine("Waiting for debugger to attach");
    Thread.Sleep(2000);
}
*/
#pragma warning disable IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.
using var host = Host.CreateDefaultBuilder(args)
    //.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace))
    .UseOrleans((ctx, siloBuilder) =>
    {

        // In order to support multiple hosts forming a cluster, they must listen on different ports.
        // Use the --InstanceId X option to launch subsequent hosts.
        int.TryParse(ctx.Configuration["InstanceId"], out var instanceId);
        siloBuilder.UseLocalhostClustering(
            siloPort: 11111 + instanceId,
            gatewayPort: 30000 + instanceId,
            primarySiloEndpoint: new IPEndPoint(IPAddress.Loopback, 11111));
    })
    .Build();
#pragma warning restore IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.

PinnedTypes.GoBePinned();
await host.StartAsync();
var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var response = await grainFactory.GetGrain<IEchoGrain>(Guid.Empty).Echo("Hello World!");
Console.WriteLine($"Echo response: {response}");
Console.WriteLine("bye?");
Console.ReadLine();
Console.WriteLine("bye");
await host.StopAsync();

public interface IEchoGrain : IGrainWithGuidKey
{
    Task<string> Echo(string message);
}   

public class EchoGrain : Grain, IEchoGrain
{
    public Task<string> Echo(string message) => Task.FromResult($"EchoGrain is responding to \"{message}\" from process {Environment.ProcessId}");
}

#endif
