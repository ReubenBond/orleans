// See https://aka.ms/new-console-template for more information
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

#if false 
/*
#pragma warning disable IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.
var services = new ServiceCollection()
//    .AddSingleton(typeof(IDam<>), typeof(Dam<>))
    .AddSingleton(typeof(IList<>), typeof(List<>))
    .AddTransient(typeof(DayOfWeek))
    .AddSingleton<NeedsValueGeneric>()
    .BuildServiceProvider();

//services.GetRequiredService<NeedsValueGeneric>();
services.GetRequiredService<IList<DayOfWeek>>();

public class NeedsValueGeneric
{
    public NeedsValueGeneric(IList<DayOfWeek> days)
    {
    }
}

public interface IDam<[DynamicallyAccessedMembers(PublicParameterlessConstructor)] T>
{
}

public class Dam<[DynamicallyAccessedMembers(All)] T> : IDam<T>
{
}
#pragma warning restore IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.
*/
#else
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
#endif
