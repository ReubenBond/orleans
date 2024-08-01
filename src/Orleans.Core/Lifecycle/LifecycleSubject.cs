#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans;

/// <summary>
/// Provides functionality for observing a lifecycle.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Single use, does not support multiple start/stop cycles.</description></item>
/// <item><description>Once started, no other observers can be subscribed.</description></item>
/// <item><description>OnStart starts stages in order until first failure or cancellation.</description></item>
/// <item><description>OnStop stops states in reverse order starting from highest started stage.</description></item>
/// <item><description>OnStop stops all stages regardless of errors even if canceled canceled.</description></item>
/// </list>
/// </remarks>
public abstract class LifecycleSubject : ILifecycleSubject
{
    protected readonly ILogger Logger;
    private SortedList<int, OrderedObserver>? _subscribers;
    private int? _highStage = null;

    protected LifecycleSubject(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Logger = logger;
    }

    /// <summary>
    /// Gets the name of the specified numeric stage.
    /// </summary>
    /// <param name="stage">The stage number.</param>
    /// <returns>The name of the stage.</returns>
    protected virtual string GetStageName(int stage) => stage.ToString();

    /// <summary>
    /// Gets the collection of all stage numbers and their corresponding names.
    /// </summary>
    /// <seealso cref="ServiceLifecycleStage"/>
    /// <param name="type">The lifecycle stage class.</param>
    /// <returns>The collection of all stage numbers and their corresponding names.</returns>
    protected static ImmutableDictionary<int, string> GetStageNames(Type type)
    {
        try
        {
            var result = ImmutableDictionary.CreateBuilder<int, string>();
            var fields = type.GetFields(
                System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                if (typeof(int).IsAssignableFrom(field.FieldType))
                {
                    try
                    {
                        if (field.GetValue(null) is int value)
                        {
                            result[value] = $"{field.Name} ({value})";
                        }
                    }
                    catch
                    {
                        // Ignore.
                    }
                }
            }

            return result.ToImmutable();
        }
        catch
        {
            return ImmutableDictionary<int, string>.Empty;
        }
    }

    /// <summary>
    /// Logs the observed performance of an <see cref="OnStart"/> call.
    /// </summary>
    /// <param name="stage">The stage.</param>
    /// <param name="elapsed">The period of time which elapsed before <see cref="OnStart"/> completed once it was initiated.</param>
    protected virtual void PerfMeasureOnStart(int stage, TimeSpan elapsed)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
        {
            Logger.LogTrace(
                (int)ErrorCode.SiloStartPerfMeasure,
                "Starting lifecycle stage {Stage} took {Elapsed} Milliseconds",
                stage,
                elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public virtual async Task OnStart(CancellationToken cancellationToken = default)
    {
        if (_highStage.HasValue)
        {
            throw new InvalidOperationException("Lifecycle has already been started.");
        }

        try
        {
            if (_subscribers is null) return;

            int? currentStage = null;
            List<Task>? stageTasks = null;
            var stopWatch = ValueStopwatch.StartNew();
            for (var i = 0; i < _subscribers.Count; i++)
            {
                var observerStage = _subscribers.GetKeyAtIndex(i);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OrleansLifecycleCanceledException("Lifecycle start canceled by request");
                }

                // Ensure tasks from previous stage have completed first.
                if (currentStage.HasValue && observerStage != currentStage && stageTasks is not null)
                {
                    await Task.WhenAll(stageTasks);
                    LogAndReset(stageTasks, stopWatch, observerStage);
                    stopWatch.Restart();
                }

                currentStage = observerStage;
                _highStage = observerStage;
                stageTasks ??= [];
                stageTasks.Add(CallOnStart(_subscribers.GetValueAtIndex(i), cancellationToken));
            }

            if (currentStage is int stage && stageTasks is not null)
            {
                await Task.WhenAll(stageTasks);
                LogAndReset(stageTasks, stopWatch, stage);
                stopWatch.Restart();
            }
        }
        catch (Exception ex) when (ex is not OrleansLifecycleCanceledException)
        {
            Logger.LogError(
                (int)ErrorCode.LifecycleStartFailure,
                ex,
                "Lifecycle start canceled due to errors at stage {Stage}",
                _highStage);
            throw;
        }

        static Task CallOnStart(OrderedObserver observer, CancellationToken cancellationToken)
        {
            try
            {
                return observer.Observer?.OnStart(cancellationToken) ?? Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        void LogAndReset(List<Task> stageTasks, ValueStopwatch stopWatch, int observerStage)
        {
            PerfMeasureOnStart(observerStage, stopWatch.Elapsed);
            stageTasks.Clear();
            OnStartStageCompleted(observerStage);
        }
    }

    /// <summary>
    /// Signifies that <see cref="OnStart"/> completed.
    /// </summary>
    /// <param name="stage">The stage which completed.</param>
    protected virtual void OnStartStageCompleted(int stage) { }

    /// <summary>
    /// Logs the observed performance of an <see cref="OnStop"/> call.
    /// </summary>
    /// <param name="stage">The stage.</param>
    /// <param name="elapsed">The period of time which elapsed before <see cref="OnStop"/> completed once it was initiated.</param>
    protected virtual void PerfMeasureOnStop(int stage, TimeSpan elapsed)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
        {
            Logger.LogTrace(
                (int)ErrorCode.SiloStartPerfMeasure,
                "Stopping lifecycle stage {Stage} took {Elapsed} Milliseconds",
                stage,
                elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public virtual async Task OnStop(CancellationToken cancellationToken = default)
    {
        // if not started, do nothing
        if (!_highStage.HasValue) return;
        var loggedCancellation = false;
        if (_subscribers is null)
        {
            return;
        }

        // include up to highest started stage
        foreach (IGrouping<int, OrderedObserver> observerGroup in _subscribers
            .Where(orderedObserver => orderedObserver.Stage <= _highStage && orderedObserver.Observer != null)
            .GroupBy(orderedObserver => orderedObserver.Stage)
            .OrderByDescending(group => group.Key))
        {
            if (cancellationToken.IsCancellationRequested && !loggedCancellation)
            {
                Logger.LogWarning("Lifecycle stop operations canceled by request.");
                loggedCancellation = true;
            }

            var stage = observerGroup.Key;
            _highStage = stage;
            try
            {
                var stopwatch = ValueStopwatch.StartNew();
                await Task.WhenAll(observerGroup.Select(orderedObserver => CallOnStop(orderedObserver, cancellationToken)));
                stopwatch.Stop();
                PerfMeasureOnStop(stage, stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    (int)ErrorCode.LifecycleStopFailure,
                    ex,
                    "Stopping lifecycle encountered an error at stage {Stage}. Continuing to stop.",
                    _highStage);
            }

            OnStopStageCompleted(stage);
        }

        static Task CallOnStop(OrderedObserver observer, CancellationToken cancellationToken)
        {
            try
            {
                return observer.Observer?.OnStop(cancellationToken) ?? Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }
    }

    /// <summary>
    /// Signifies that <see cref="OnStop"/> completed.
    /// </summary>
    /// <param name="stage">The stage which completed.</param>
    protected virtual void OnStopStageCompleted(int stage) { }

    public virtual IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));

        if (_highStage.HasValue) throw new InvalidOperationException("Lifecycle has already been started.");

        var orderedObserver = new OrderedObserver(stage, observer);
        _subscribers ??= [];
        _subscribers.Add(orderedObserver);

        return orderedObserver;
    }

    /// <summary>
    /// Represents a <see cref="ILifecycleObservable"/>'s participation in a given lifecycle stage.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="OrderedObserver"/> class.
    /// </remarks>
    /// <param name="stage">The stage which the observer is participating in.</param>
    /// <param name="observer">The participating observer.</param>
    private class OrderedObserver(int stage, ILifecycleObserver observer) : IDisposable
    {
        /// <summary>
        /// Gets the observer.
        /// </summary>
        public ILifecycleObserver? Observer { get; private set; } = observer;

        /// <summary>
        /// Gets the stage which the observer is participating in.
        /// </summary>
        public int Stage { get; } = stage;

        /// <inheritdoc />
        public void Dispose() => Observer = null;
    }
}
