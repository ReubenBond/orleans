using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.DurableTasks;
using Orleans.Serialization;

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<JobScheduler>();
        services.AddSingleton<IJobStorage, LiteDbJobStorage>();
        services.AddSerializer();
    })
    .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace))
    .UseConsoleLifetime().Build();
await host.StartAsync();

var jobScheduler = host.Services.GetRequiredService<JobScheduler>();

// Cleanup completed jobs which completed at least a second ago.
await jobScheduler.PruneCompletedTasksAsync(TimeSpan.FromMinutes(5));

jobScheduler.AddHandler("stringJoin", args => new(string.Join(", ", args))); 

// During program config. This could be ASP.NET route mapping
jobScheduler.AddHandler("SayHello", async args =>
{
    string result;
    if (args is { Length: > 1 })
    {
        result = await jobScheduler.GetOrCreateJob("stringJoin", args).AsStep("join");
    }
    else
    {
        result = args[0];
    }

    return $"hello, {result}";
});

await jobScheduler.StartAsync();

// Later, or somewhere else:
var job1 = await jobScheduler.GetOrCreateJob("SayHello", "Xiao").ScheduleAsync("job-1");
var job2 = await jobScheduler.GetOrCreateJob("SayHello", "Julian", "Benjamin", "Phil").ScheduleAsync("job-2");
var job3 = await jobScheduler.GetOrCreateJob("SayHello", "Sergey", "Gabriel", "Jason").ScheduleAsync("job-3");
var result3 = await job3;

// Some time later, maybe an app crash happens in between.
var result1 = await job1;
Console.WriteLine($"Result of {job1.TaskId}: {result1}");

var result2 = await job2;
Console.WriteLine($"Result of {job2.TaskId}: {result2}");
Console.WriteLine($"Result of {job3.TaskId}: {result3}");

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

while (!lifetime.ApplicationStopping.IsCancellationRequested)
{
    Console.WriteLine("What would you like to do? list, create, pending, approve <TaskId>, cancel <TaskId>, exit");
    var cmd = Console.ReadLine();

    if (cmd == "exit")
    {
        lifetime.StopApplication();
        break;
    }

    if (cmd == "prune")
    {
        await jobScheduler.PruneCompletedTasksAsync(TimeSpan.Zero);
    }

    if (cmd == "list")
    {
        await foreach (var job in jobScheduler.GetJobsAsync())
        {
            Console.WriteLine(job);
        }
    }

    if (cmd == "pending")
    {
        await foreach (var job in jobScheduler.GetJobsAsync())
        {
            Console.WriteLine(job);
        }
    }

    if (cmd == "create")
    {
        var names = new[] { "Bob", "Mary", "Ted", "Alice", "Jehoshaphat", "Brian" };
        var jobType = "SayHello";
        var jobArgs = Enumerable.Range(0, Random.Shared.Next(3)).Select(_ => names[Random.Shared.Next(names.Length)]).ToArray();
        var jobId = $"jeb-{Random.Shared.Next(0, int.MaxValue):X}";
        await jobScheduler.GetOrCreateJob(jobType, jobArgs).ScheduleAsync(jobId);
    }
}

await host.WaitForShutdownAsync();