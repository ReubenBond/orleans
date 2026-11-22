using System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// Controlled constructor and instance shims for timer-driven <see cref="CancellationTokenSource"/>
/// behavior. The returned object remains the real BCL type; only its timer is virtual.
/// </summary>
public static class ControlledCancellationTokenSource
{
    private const string Api = "System.Threading.CancellationTokenSource";
    private const string CancelApi = Api + ".Cancel";
    private const uint MaxSupportedTimeout = 0xfffffffe;
    private static readonly ConditionalWeakTable<CancellationTokenSource, Registration> Registrations = new();

    /// <summary>Creates a source canceled immediately or after a virtual millisecond delay.</summary>
    public static CancellationTokenSource Create(int millisecondsDelay)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime(".ctor");
        TimeSpan delay = ValidateMilliseconds(millisecondsDelay);
        return CreateCore(snapshot, delay);
    }

    /// <summary>Creates a source canceled immediately or after a virtual delay.</summary>
    public static CancellationTokenSource Create(TimeSpan delay)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime(".ctor");
        return CreateCore(snapshot, ValidateTimeSpan(delay, nameof(delay)));
    }

    /// <summary>Creates a source canceled using a supported controlled provider.</summary>
    public static CancellationTokenSource Create(TimeSpan delay, TimeProvider timeProvider)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime(".ctor");
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeSpan validated = ValidateTimeSpan(delay, nameof(delay));
        ControlledTimeProvider.ValidateProvider(timeProvider, $"{Api}..ctor");
        return CreateCore(snapshot, validated);
    }

    /// <summary>Schedules or disables virtual timer-driven cancellation.</summary>
    public static void CancelAfter(CancellationTokenSource source, int millisecondsDelay)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime("CancelAfter");
        ArgumentNullException.ThrowIfNull(source);
        TimeSpan delay = ValidateMilliseconds(millisecondsDelay);
        CancelAfterCore(snapshot, source, delay);
    }

    /// <summary>Schedules or disables virtual timer-driven cancellation.</summary>
    public static void CancelAfter(CancellationTokenSource source, TimeSpan delay)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime("CancelAfter");
        ArgumentNullException.ThrowIfNull(source);
        CancelAfterCore(snapshot, source, ValidateTimeSpan(delay, nameof(delay)));
    }

    /// <summary>
    /// Cancels synchronously and disables any pending virtual cancellation timer. The callbacks run as
    /// <see cref="SimulationCompletion"/> runs a completion.
    /// </summary>
    public static void Cancel(CancellationTokenSource source)
    {
        RequireRuntime("Cancel");
        ArgumentNullException.ThrowIfNull(source);
        SimulationCompletion.Run(
            source,
            static source =>
            {
                DisableTimer(source);
                source.Cancel();
            },
            CancelApi);
    }

    /// <inheritdoc cref="Cancel(CancellationTokenSource)"/>
    public static void Cancel(CancellationTokenSource source, bool throwOnFirstException)
    {
        RequireRuntime("Cancel");
        ArgumentNullException.ThrowIfNull(source);
        SimulationCompletion.Run(
            (source, throwOnFirstException),
            static completion =>
            {
                DisableTimer(completion.source);
                completion.source.Cancel(completion.throwOnFirstException);
            },
            CancelApi);
    }

    /// <summary>Queues cancellation as controlled work instead of using the physical thread pool.</summary>
    public static Task CancelAsync(CancellationTokenSource source)
    {
        RequireRuntime("CancelAsync");
        ArgumentNullException.ThrowIfNull(source);
        _ = source.Token;
        if (source.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        DisableTimer(source);
        var completion = new TaskCompletionSource();
        SimulationTaskRuntime.QueueWork(
            () => SimulationCompletion.Run(
                (source, promise: completion),
                static completion =>
                {
                    try
                    {
                        completion.source.Cancel();
                        completion.promise.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        completion.promise.TrySetException(exception);
                    }
                },
                $"{Api}.CancelAsync"),
            $"{Api}.CancelAsync",
            flowExecutionContext: false);
        return completion.Task;
    }

    /// <summary>Attempts to reset the source and disables any pending virtual cancellation timer.</summary>
    public static bool TryReset(CancellationTokenSource source)
    {
        RequireRuntime("TryReset");
        ArgumentNullException.ThrowIfNull(source);
        DisableTimer(source);
        return source.TryReset();
    }

    /// <summary>Disposes the source and permanently cancels its virtual timer registration.</summary>
    public static void Dispose(CancellationTokenSource source)
    {
        RequireRuntime("Dispose");
        ArgumentNullException.ThrowIfNull(source);
        if (Registrations.TryGetValue(source, out Registration? registration))
        {
            registration.Dispose();
            Registrations.Remove(source);
        }

        source.Dispose();
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The registration is owned by the conditional weak table until the cancellation source is disposed.")]
    private static CancellationTokenSource CreateCore(
        SimulationExecutionSnapshot snapshot,
        TimeSpan delay)
    {
        var source = new CancellationTokenSource();
        if (delay == TimeSpan.Zero)
        {
            SimulationCompletion.Run(source, static source => source.Cancel(), $"{Api}..ctor");
            return source;
        }

        if (delay != Timeout.InfiniteTimeSpan)
        {
            Registration registration = CreateRegistration(snapshot, source);
            Registrations.Add(source, registration);
            registration.Change(delay);
        }

        return source;
    }

    private static void CancelAfterCore(
        SimulationExecutionSnapshot snapshot,
        CancellationTokenSource source,
        TimeSpan delay)
    {
        _ = source.Token;
        if (source.IsCancellationRequested)
        {
            return;
        }

        Registration registration = Registrations.GetValue(
            source,
            key => CreateRegistration(snapshot, key));
        registration.Change(delay);
    }

    private static Registration CreateRegistration(
        SimulationExecutionSnapshot snapshot,
        CancellationTokenSource source) =>
        new(
            new ShimTimerRegistration(
                snapshot,
                _ => SimulationCompletion.Run(
                    source,
                    static source =>
                    {
                        if (!source.IsCancellationRequested)
                        {
                            source.Cancel();
                        }
                    },
                    $"{Api}.CancelAfter"),
                null,
                null),
            source.Token);

    private static void DisableTimer(CancellationTokenSource source)
    {
        if (Registrations.TryGetValue(source, out Registration? registration))
        {
            registration.Change(Timeout.InfiniteTimeSpan);
        }
    }

    private static SimulationExecutionSnapshot RequireRuntime(string member) =>
        SimulationRuntimeDispatch.RequireActiveSimulation($"{Api}.{member}");

    private static TimeSpan ValidateMilliseconds(int millisecondsDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsDelay, -1);
        return millisecondsDelay == Timeout.Infinite
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMilliseconds(millisecondsDelay);
    }

    private static TimeSpan ValidateTimeSpan(TimeSpan delay, string parameterName)
    {
        long milliseconds = (long)delay.TotalMilliseconds;
        ArgumentOutOfRangeException.ThrowIfLessThan(milliseconds, -1, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, MaxSupportedTimeout, parameterName);
        return milliseconds == Timeout.Infinite
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMilliseconds(milliseconds);
    }

    private sealed class Registration : IDisposable
    {
        private readonly ShimTimerRegistration _timer;
        private readonly CancellationTokenRegistration _sourceCancellation;

        public Registration(ShimTimerRegistration timer, CancellationToken token)
        {
            _timer = timer;
            _sourceCancellation = token.UnsafeRegister(
                static state => ((ShimTimerRegistration)state!).DisableFromCancellation(),
                timer);
        }

        public void Change(TimeSpan delay) => _timer.Change(delay, Timeout.InfiniteTimeSpan);

        public void Dispose()
        {
            _sourceCancellation.Unregister();
            _timer.Dispose();
        }
    }
}
