using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace WorkflowsApp.Service.Samples.HelloWorld;

internal class HelloWorld
{
    public async Task RunAsync(IServiceProvider serviceProvider)
    {
        var grainFactory = serviceProvider.GetRequiredService<IGrainFactory>();
        var orchestrationGrain = grainFactory.GetGrain<IHelloWorkflowGrain>("default");

        var instance = await orchestrationGrain.RunSample().ScheduleAsync();
        Console.WriteLine($"Started orchestration with ID = '{instance.Id}' successfully!");

        // Block until the orchestration completes
        var result = await instance.;
        Console.WriteLine($"Orchestration completed with status: {state.OrchestrationStatus} and output: {state.Output} ");
        await worker.StopAsync();
        dtsExtension.Dispose();

class HelloWorldOrchestration : TaskOrchestration<string[], string>
    {
        public override async Task<string[]> RunTask(OrchestrationContext context, string _)
        {
            // Say hello to different cities around the world in time zone order
            string result1 = await context.ScheduleTask<string>(typeof(HelloActivity), "Tokyo");
            string result2 = await context.ScheduleTask<string>(typeof(HelloActivity), "Hyderabad");
            string result3 = await context.ScheduleTask<string>(typeof(HelloActivity), "London");
            string result4 = await context.ScheduleTask<string>(typeof(HelloActivity), "São Paulo");
            string result5 = await context.ScheduleTask<string>(typeof(HelloActivity), "Seattle");

            // Return greetings as an array
            return [result1, result2, result3, result4, result5];
        }
    }
}

public interface IHelloGrain : IGrainWithStringKey
{
    DurableTask<string> SayHelloAsync(string input);
}

internal class HelloGrain : DurableGrain, IHelloGrain
{
    public DurableTask<string> SayHelloAsync(string name) => DurableTask.FromResult($"Hello, {name}!");
}

public interface IHelloWorkflowGrain : IGrainWithStringKey
{
    DurableTask<string[]> RunSample();
}

internal class HelloWorkflowGrain : DurableGrain, IHelloWorkflowGrain
{
    public async DurableTask<string[]> RunSample()
    {
        var helloGrain = GrainFactory.GetGrain<IHelloGrain>("default");
        var result1 = await helloGrain.SayHelloAsync("Tokyo");
        var result2 = await helloGrain.SayHelloAsync("Hyderabad");
        var result3 = await helloGrain.SayHelloAsync("London");
        var result4 = await helloGrain.SayHelloAsync("São Paulo");
        var result5 = await helloGrain.SayHelloAsync("Seattle");

        // Return greetings as an array
        return [result1, result2, result3, result4, result5];
    }
}
