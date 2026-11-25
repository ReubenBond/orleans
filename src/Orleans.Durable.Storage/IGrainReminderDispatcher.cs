using Orleans.Durable.Jobs;
using Orleans.Runtime;

namespace Orleans.Durable.Storage;

/// <summary>
/// Dispatches an ephemeral primary-side grain reminder to a target grain's durable job receiver extension
/// (orleans.md §5.15).
/// </summary>
internal interface IGrainReminderDispatcher
{
    /// <summary>Invokes the target grain's durable job receiver with <paramref name="context"/>.</summary>
    /// <param name="target">The target grain identifier.</param>
    /// <param name="context">The ephemeral reminder run context (<see cref="IJobRunContext.IsEphemeralReminder"/> is <see langword="true"/>).</param>
    /// <param name="cancellationToken">Cancels the dispatch attempt.</param>
    /// <returns>The grain handler's run result.</returns>
    ValueTask<DurableJobRunResult> DispatchAsync(GrainId target, IJobRunContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IGrainReminderDispatcher"/> backed by Orleans' <see cref="IGrainFactory"/> and
/// <see cref="IDurableJobReceiverExtension"/>.
/// </summary>
internal sealed class GrainFactoryReminderDispatcher(IGrainFactory grainFactory) : IGrainReminderDispatcher
{
    private readonly IGrainFactory _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));

    /// <inheritdoc />
    public ValueTask<DurableJobRunResult> DispatchAsync(GrainId target, IJobRunContext context, CancellationToken cancellationToken)
    {
        var receiver = _grainFactory.GetGrain<IDurableJobReceiverExtension>(target);
        return receiver.HandleDurableJobAsync(context, cancellationToken);
    }
}
