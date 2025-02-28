using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;

namespace WorkflowsApp.Service.Samples.Parallelism;

internal static class HumanInTheLoop
{
    public static async Task RunAsync(IServiceProvider serviceProvider)
    {
        var grainFactory = serviceProvider.GetRequiredService<IGrainFactory>();
        var orchestrationGrain = grainFactory.GetGrain<IGreeterGrain>("default");

        var instance = await orchestrationGrain.GetGreetingAsync().ScheduleAsync();
        Console.WriteLine($"Started greeter workflow '{instance.Id}'.");
        string? input;
        do
        {
            Console.WriteLine($"Enter a greeting or 'cancel' to cancel the workflow:");
            input = Console.ReadLine();
        } while (input is null);

        if (input == "cancel")
        {
            await orchestrationGrain.CancelAsync();
        }
        else
        {
            await orchestrationGrain.SetGreetingAsync(input);
        }

        try
        {
            var result = await instance.WaitAsync();
            Console.WriteLine($"Workflow completed with result: {result}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Workflow was canceled.");
        }
    }

    public interface IGreeterGrain : IGrainWithStringKey
    {
        ValueTask SetGreetingAsync(string greeting);
        ValueTask CancelAsync();
        DurableTask<string> GetGreetingAsync();
    }

    internal class GreeterGrain([FromKeyedServices("state")] IDurableTaskCompletionSource<string> state) : DurableGrain, IGreeterGrain
    {
        public DurableTask<string> GetGreetingAsync() => DurableTask.Run(async cancellationToken =>
        {
            return await state.Task;
        });

        public async ValueTask SetGreetingAsync(string greeting)
        {
            if (state.TrySetResult(greeting))
            {
                await WriteStateAsync();
            }
        }

        public async ValueTask CancelAsync()
        {
            if (state.TrySetCanceled())
            {
                await WriteStateAsync();
            }
        }
    }
}
