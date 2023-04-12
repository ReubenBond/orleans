// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
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
    .UseOrleans(siloBuilder => siloBuilder.UseLocalhostClustering())
    .Build();
#pragma warning restore IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.

PinnedTypes.GoBePinned();
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

#endif
