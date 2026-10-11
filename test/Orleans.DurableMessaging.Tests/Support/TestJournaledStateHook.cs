using Orleans.Journaling;

namespace Orleans.DurableMessaging.Tests.Support;

internal sealed class TestJournaledStateHook : IJournaledStateHook
{
    public Action<JournaledStateOperation, CancellationToken>? BeforeOperation { get; init; }
    public Func<JournaledStateOperation, CancellationToken, ValueTask>? BeforeOperationAsync { get; init; }
    public Action<JournaledStateOperation, CancellationToken>? AfterOperation { get; init; }
    public Func<JournaledStateOperation, CancellationToken, ValueTask>? AfterOperationAsync { get; init; }

    ValueTask IJournaledStateHook.BeforeOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        BeforeOperation?.Invoke(operation, cancellationToken);
        return BeforeOperationAsync?.Invoke(operation, cancellationToken) ?? default;
    }

    ValueTask IJournaledStateHook.AfterOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        AfterOperation?.Invoke(operation, cancellationToken);
        return AfterOperationAsync?.Invoke(operation, cancellationToken) ?? default;
    }
}
