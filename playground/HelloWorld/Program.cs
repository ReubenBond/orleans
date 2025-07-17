using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(o => o.UseLocalhostClustering());
var app = builder.Build();
await app.StartAsync();

// on the client
var myGrain = app.Services.GetRequiredService<IGrainFactory>().GetGrain<IMyGrain>(Guid.NewGuid());
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
try
{
    await foreach (var item in myGrain.Generate(cts.Token))
    {
        Console.WriteLine(item);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Operation was cancelled.");
}

Console.WriteLine("Done.");
await app.WaitForShutdownAsync();

public interface IMyGrain : IGrainWithGuidKey
{
    IAsyncEnumerable<int> Generate(CancellationToken cancellationToken);
}

public class MyGrain : Grain, IMyGrain
{
    public IAsyncEnumerable<int> Generate(CancellationToken cancellationToken)
    {
        var cw = Channel.CreateUnbounded<int>();
        _ = GeneratorCore(cw.Writer, cancellationToken);
        return cw.Reader.ReadAllAsync(cancellationToken);
    }

    private async Task GeneratorCore(ChannelWriter<int> writer, CancellationToken cancellationToken)
    {
        try
        {
            writer.TryWrite(1);
            await Task.Delay(10_000, cancellationToken);
            Console.WriteLine("Captured token was NOT canceled");
            writer.TryWrite(2);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Captured token cancellation observed");
        }
    }
}

