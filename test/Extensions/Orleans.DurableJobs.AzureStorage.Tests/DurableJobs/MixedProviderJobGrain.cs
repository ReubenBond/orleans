using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Placement;
using Orleans.Runtime;

namespace Tester.AzureUtils.DurableJobs;

/// <summary>One execution attempt of a durable job, as its target grain recorded it in its own journal.</summary>
[GenerateSerializer]
public sealed record MixedProviderJobRun(
    [property: Id(0)] string JobName,
    [property: Id(1)] int DequeueCount,
    [property: Id(2)] string Silo,
    [property: Id(3)] bool Succeeded);

/// <summary>
/// An application grain whose journal uses the default journal provider and which schedules and handles durable jobs.
/// </summary>
public interface IMixedProviderJobGrain : IGrainWithStringKey
{
    /// <summary>Schedules a job targeting this grain whose first <paramref name="failedAttempts"/> recorded attempts fail.</summary>
    Task<DurableJob> ScheduleAsync(string jobName, DateTimeOffset dueTime, int failedAttempts);

    /// <summary>Requests cancellation of <paramref name="job"/>.</summary>
    Task<bool> CancelAsync(DurableJob job);

    /// <summary>Returns the attempts this grain's journal recorded, in order.</summary>
    Task<List<MixedProviderJobRun>> GetRunsAsync();

    /// <summary>Deactivates the activation once idle, so the next call recovers the grain's journal.</summary>
    Task DeactivateAsync();
}

/// <summary>
/// Records each job attempt durably before completing it, so the attempts survive the loss of the silo which ran them.
/// </summary>
[PreferLocalPlacement]
public sealed class MixedProviderJobGrain(
    [FromKeyedServices("runs")] IDurableList<MixedProviderJobRun> runs,
    IDurableStateManager state,
    ILocalDurableJobManager jobs,
    ILocalSiloDetails silo) : Grain, IMixedProviderJobGrain, IDurableJobHandler
{
    private const string FailedAttemptsKey = "FailedAttempts";

    public Task<DurableJob> ScheduleAsync(string jobName, DateTimeOffset dueTime, int failedAttempts) =>
        jobs.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = this.GetGrainId(),
                JobName = jobName,
                DueTime = dueTime,
                Metadata = new Dictionary<string, string> { [FailedAttemptsKey] = failedAttempts.ToString(CultureInfo.InvariantCulture) },
            },
            CancellationToken.None);

    public Task<bool> CancelAsync(DurableJob job) => jobs.CancelAsync(job, CancellationToken.None);

    public Task<List<MixedProviderJobRun>> GetRunsAsync() => Task.FromResult(runs.ToList());

    public Task DeactivateAsync()
    {
        this.DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        var failedAttempts = int.Parse(context.Job.Metadata![FailedAttemptsKey], CultureInfo.InvariantCulture);
        var succeeded = runs.Count(run => run.JobName == context.Job.Name) >= failedAttempts;
        runs.Add(new(context.Job.Name, context.DequeueCount, silo.SiloAddress.ToParsableString(), succeeded));
        await state.WriteStateAsync(attemptCancellationToken);
        if (!succeeded)
        {
            throw new InvalidOperationException($"Attempt {context.DequeueCount} of job '{context.Job.Name}' fails as scheduled.");
        }
    }
}

/// <summary>The JSON journal format's metadata for <see cref="MixedProviderJobGrain"/>'s state and its manager's own.</summary>
[JsonSerializable(typeof(MixedProviderJobRun))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(Guid))]
internal sealed partial class MixedProviderJobsJsonContext : JsonSerializerContext;
