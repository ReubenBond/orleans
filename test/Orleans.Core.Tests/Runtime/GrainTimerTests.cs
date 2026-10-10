using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Timers;
using Xunit;

namespace NonSilo.Tests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GrainTimerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateOneShot_QueuesActivationMessageWithoutPhysicalTimer(bool interleave)
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        IGrainTimer timer;
        using var reentrancy = RequestContext.AllowCallChainReentrancy();
        Assert.NotEqual(Guid.Empty, RequestContext.ReentrancyId);
        RequestContext.Set("timer-parent", "request-data");
        try
        {
            timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
            {
                Assert.Equal(Guid.Empty, RequestContext.ReentrancyId);
                Assert.Null(RequestContext.Get("timer-parent"));
                calls++;
                return Task.CompletedTask;
            }, 0, new(TimeSpan.Zero, Timeout.InfiniteTimeSpan) { Interleave = interleave, KeepAlive = true });
        }
        finally
        {
            RequestContext.Remove("timer-parent");
        }
        using var lifetime = timer;
        var message = Assert.Single(fixture.Messages);
        Assert.Equal(0, calls);
        Assert.Equal(0, fixture.Time.TimerCreations);
        Assert.Equal(interleave, message.IsAlwaysInterleave);
        Assert.True(message.IsKeepAlive);
        Assert.True(message.IsLocalOnly);
        Assert.Null(message.RequestContextData);
        Assert.Equal(Guid.Empty, message.GetReentrancyId());
        await fixture.InvokeAsync(message);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, calls);
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.Time.TimerCreations);
    }

    [Fact]
    public async Task LegacyImmediateTimer_ReusesActivationTurnAndInvalidatesQueuedTickOnDisposal()
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Registry.RegisterTimer(fixture.Grain, _ =>
        {
            calls++;
            return Task.CompletedTask;
        }, null, TimeSpan.Zero, TimeSpan.Zero);
        var message = Assert.Single(fixture.Messages);
        Assert.Equal(0, calls);
        Assert.True(message.IsAlwaysInterleave);
        Assert.False(message.IsKeepAlive);
        Assert.True(message.IsLocalOnly);
        await fixture.InvokeAsync(message);
        Assert.Equal(1, calls);
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Same(message.BodyObject, fixture.Messages[1].BodyObject);
        timer.Dispose();
        await fixture.InvokeAsync(fixture.Messages[1]);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, calls);
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Equal(0, fixture.Time.TimerCreations);
    }

    [Fact]
    public async Task ImmediateTimer_ChangedInsideCallbackCreatesDelayedTimerAndRespectsPeriod()
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        IGrainTimer? timer = null;
        timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                timer!.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
            }
            return Task.CompletedTask;
        }, 0, new(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        using (timer)
        {
            await fixture.InvokeAsync(Assert.Single(fixture.Messages));
            Assert.Equal(1, fixture.Time.TimerCreations);
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.Single(fixture.Messages);
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(2, fixture.Messages.Count);
            await fixture.InvokeAsync(fixture.Messages[1]);
            fixture.Time.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal(3, fixture.Messages.Count);
            await fixture.InvokeAsync(fixture.Messages[2]);
            Assert.Equal(3, calls);
            Assert.Equal(1, fixture.Time.TimerCreations);
        }
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(3, fixture.Messages.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisposedTimer_QueuedTickDrainsWithoutCallback(bool delayed, bool interleave)
    {
        using var fixture = new TimerFixture();
        using var diagnostics = new TimerDiagnostics(fixture.Grain);
        var calls = 0;
        var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, new(delayed ? TimeSpan.FromSeconds(1) : TimeSpan.Zero, TimeSpan.FromSeconds(1)) { Interleave = interleave });
        if (delayed)
        {
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
        }
        timer.Dispose();
        timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, calls);
        Assert.Single(fixture.Messages);
        Assert.Equal(delayed ? 1 : 0, fixture.Time.TimerCreations);
        Assert.Collection(diagnostics.Events,
            evt => Assert.IsType<GrainTimerEvents.Created>(evt),
            evt => Assert.IsType<GrainTimerEvents.Disposed>(evt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeWhileCancellationBlocked_QueuedTickDrainsBeforeDisposalCompletes(bool interleave)
    {
        using var fixture = new TimerFixture();
        using var diagnostics = new TimerDiagnostics(fixture.Grain);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken callbackToken = default;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, token) =>
        {
            calls++;
            callbackToken = token;
            return Task.CompletedTask;
        }, 0, new(TimeSpan.Zero, Timeout.InfiniteTimeSpan) { Interleave = interleave });
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        using var registration = callbackToken.Register(() =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        });
        timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        var disposal = Task.Run(timer.Dispose, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(callbackToken.IsCancellationRequested);
            Assert.False(disposal.IsCompleted);
            // Invoke on another thread so a lock-order regression is bounded by the phase timeout.
            await Task.Run(() => fixture.InvokeAsync(fixture.Messages[1]), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(1, calls);
            Assert.Single(diagnostics.Events.OfType<GrainTimerEvents.TickStart>());
            Assert.Single(diagnostics.Events.OfType<GrainTimerEvents.TickStop>());
        }
        finally
        {
            release.SetResult();
            await disposal;
        }
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Equal(0, fixture.Time.TimerCreations);
        Assert.Single(diagnostics.Events.OfType<GrainTimerEvents.Disposed>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RepeatedImmediateRearm_ReusesInvokerAndCancellationScopeWithoutPhysicalTimer(bool interleave, bool keepAlive)
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        var state = new object();
        CancellationToken? firstToken = null;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (value, token) =>
        {
            Assert.Same(state, value);
            firstToken ??= token;
            Assert.Equal(firstToken.Value, token);
            calls++;
            return Task.CompletedTask;
        }, state, new(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan) { Interleave = interleave, KeepAlive = keepAlive });

        for (var turn = 0; turn < 128; turn++)
        {
            RequestContext.Set("timer-parent", "request-data");
            try
            {
                timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
            finally
            {
                RequestContext.Remove("timer-parent");
            }

            Assert.Equal(turn + 1, fixture.Messages.Count);
            Assert.Equal(turn, calls);
            var message = fixture.Messages[turn];
            Assert.Same(fixture.Messages[0].BodyObject, message.BodyObject);
            Assert.Equal(Message.Directions.OneWay, message.Direction);
            Assert.Equal(interleave, message.IsAlwaysInterleave);
            Assert.Equal(keepAlive, message.IsKeepAlive);
            Assert.True(message.IsLocalOnly);
            Assert.Null(message.TimeToLive);
            Assert.Equal(fixture.Grain.GrainId, message.TargetGrain);
            Assert.Equal(message.SendingGrain, message.TargetGrain);
            Assert.Equal(message.SendingSilo, message.TargetSilo);
            Assert.Null(message.RequestContextData);
            await fixture.InvokeAsync(message);
            fixture.Time.Advance(TimeSpan.FromDays(1));
            Assert.Equal(turn + 1, calls);
            Assert.Equal(turn + 1, fixture.Messages.Count);
            Assert.Equal(0, fixture.Time.TimerCreations);
        }
    }

    [Fact]
    public async Task CoalescedImmediateRearm_AllocatesZeroBytes()
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, new(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        for (var i = 0; i < 32; i++)
        {
            timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1024; i++)
        {
            timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, fixture.Time.TimerCreations);
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        Assert.Equal(1, calls);
        Assert.Single(fixture.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateRearmDuringAsyncCallback_DefersAndCoalescesUntilCompletion(bool interleave)
    {
        using var fixture = new TimerFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, async (_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                started.SetResult();
                await release.Task;
            }
        }, 0, new(TimeSpan.Zero, Timeout.InfiniteTimeSpan) { Interleave = interleave });
        var invocation = fixture.InvokeAsync(Assert.Single(fixture.Messages));
        try
        {
            await started.Task;
            for (var i = 0; i < 128; i++)
            {
                timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
            fixture.Time.Advance(TimeSpan.FromDays(1));
            Assert.Single(fixture.Messages);
            Assert.Equal(1, calls);
            Assert.Equal(0, fixture.Time.TimerCreations);
        }
        finally
        {
            release.SetResult();
            await invocation;
        }
        Assert.Equal(2, fixture.Messages.Count);
        await fixture.InvokeAsync(fixture.Messages[1]);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, calls);
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Equal(0, fixture.Time.TimerCreations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedImmediateTick_ChangedToDelayedOrInfinite_DrainsWithoutCallback(bool infinite)
    {
        using var fixture = new TimerFixture();
        using var diagnostics = new TimerDiagnostics(fixture.Grain);
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, new(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        timer.Change(infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        Assert.Equal(0, calls);
        Assert.Empty(diagnostics.Events.OfType<GrainTimerEvents.TickStart>());
        Assert.Empty(diagnostics.Events.OfType<GrainTimerEvents.TickStop>());
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        if (!infinite)
        {
            Assert.Equal(2, fixture.Messages.Count);
            await fixture.InvokeAsync(fixture.Messages[1]);
            Assert.Equal(1, calls);
        }
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(infinite ? 1 : 2, fixture.Messages.Count);
        Assert.Equal(infinite ? 0 : 1, fixture.Time.TimerCreations);
    }

    [Fact]
    public async Task QueuedTick_DelayedReplacementExpiresBeforeDrain_ReusesQueuedTurn()
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, new(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        var message = Assert.Single(fixture.Messages);
        timer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        fixture.Time.FireStaleCallback();
        Assert.Single(fixture.Messages);
        Assert.Equal(0, calls);
        await fixture.InvokeAsync(message);
        Assert.Equal(1, calls);
        fixture.Time.Advance(TimeSpan.FromSeconds(3) - TimeSpan.FromTicks(1));
        Assert.Single(fixture.Messages);
        fixture.Time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Same(message.BodyObject, fixture.Messages[1].BodyObject);
        await fixture.InvokeAsync(fixture.Messages[1]);
        Assert.Equal(2, calls);
        Assert.Equal(1, fixture.Time.TimerCreations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningCallback_StalePhysicalTickPreservesPeriodFromCompletion(bool interleave)
    {
        using var fixture = new TimerFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, async (_, _) =>
        {
            if (++calls == 1)
            {
                started.SetResult();
                await release.Task;
            }
        }, 0, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)) { Interleave = interleave });
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var invocation = fixture.InvokeAsync(Assert.Single(fixture.Messages));
        try
        {
            await started.Task;
            fixture.Time.Advance(TimeSpan.FromSeconds(10));
            fixture.Time.FireStaleCallback();
            Assert.Single(fixture.Messages);
            Assert.Equal(1, calls);
        }
        finally
        {
            release.SetResult();
            await invocation;
        }
        fixture.Time.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
        Assert.Single(fixture.Messages);
        fixture.Time.Advance(TimeSpan.FromTicks(1));
        await fixture.InvokeAsync(fixture.Messages[1]);
        Assert.Equal(2, calls);
        Assert.Equal(1, fixture.Time.TimerCreations);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(100.5)]
    public async Task DelayedTick_UsesProviderTimingAndResolution(double milliseconds)
    {
        using var fixture = new TimerFixture();
        using var diagnostics = new TimerDiagnostics(fixture.Grain);
        var calls = 0;
        var dueTime = TimeSpan.FromMilliseconds(milliseconds);
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, new(dueTime, Timeout.InfiniteTimeSpan));

        fixture.Time.Advance(dueTime - TimeSpan.FromTicks(1));
        Assert.Empty(fixture.Messages);
        fixture.Time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(0, calls);
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        Assert.Equal(1, calls);
        Assert.Equal(1, fixture.Time.TimerCreations);
        Assert.Single(diagnostics.Events.OfType<GrainTimerEvents.TickStart>());
        Assert.Single(diagnostics.Events.OfType<GrainTimerEvents.TickStop>());
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Single(fixture.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchedPhysicalTick_RacingDelayedChange_IsAdmittedAndResumesPeriod(bool interleave)
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, new(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan) { Interleave = interleave });
        timer.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        // Deliver a previously dispatched callback after the replacement schedule was armed.
        fixture.Time.FireStaleCallback();
        Assert.Equal(0, calls);
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        Assert.Equal(1, calls);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Single(fixture.Messages);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, fixture.Messages.Count);
        await fixture.InvokeAsync(fixture.Messages[1]);
        Assert.Equal(2, calls);
        Assert.Equal(1, fixture.Time.TimerCreations);
    }

    [Fact]
    public async Task DelayedToImmediateRearm_RejectsStalePhysicalCallbacksAndReusesTimer()
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, new(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan));
        timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        fixture.Time.FireStaleCallback();
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        fixture.Time.FireStaleCallback();
        Assert.Single(fixture.Messages);
        timer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(fixture.Messages);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, fixture.Messages.Count);
        fixture.Time.FireStaleCallback();
        await fixture.InvokeAsync(fixture.Messages[1]);
        Assert.Equal(2, calls);
        Assert.Equal(1, fixture.Time.TimerCreations);
        timer.Dispose();
        fixture.Time.FireStaleCallback();
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, fixture.Messages.Count);
    }

    [Fact]
    public async Task ExpiredDelayedTick_ImmediateRearmBeforeAdmission_Coalesces()
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, new(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan));
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        fixture.Time.FireStaleCallback();
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, calls);
        Assert.Single(fixture.Messages);
        Assert.Equal(1, fixture.Time.TimerCreations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncCallback_LastChangeToDelayedOrInfiniteWins(bool infinite)
    {
        using var fixture = new TimerFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, async (_, _) =>
        {
            if (++calls == 1)
            {
                started.SetResult();
                await release.Task;
            }
        }, 0, new(TimeSpan.Zero, TimeSpan.Zero));
        var invocation = fixture.InvokeAsync(Assert.Single(fixture.Messages));
        try
        {
            await started.Task;
            timer.Change(TimeSpan.Zero, TimeSpan.Zero);
            timer.Change(infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            Assert.Equal(0, fixture.Time.TimerCreations);
        }
        finally
        {
            release.SetResult();
            await invocation;
        }
        Assert.Single(fixture.Messages);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        if (!infinite)
        {
            await fixture.InvokeAsync(fixture.Messages[1]);
        }
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(infinite ? 1 : 2, calls);
        Assert.Equal(infinite ? 1 : 2, fixture.Messages.Count);
        Assert.Equal(infinite ? 0 : 1, fixture.Time.TimerCreations);
    }

    [Fact]
    public async Task DisposeDuringCallback_ReentrantCancellationCannotRearmOrDisposeTwice()
    {
        using var fixture = new TimerFixture();
        using var diagnostics = new TimerDiagnostics(fixture.Grain);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        IGrainTimer? timer = null;
        timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, async (_, token) =>
        {
            calls++;
            using var registration = token.Register(() =>
            {
                timer!.Change(TimeSpan.Zero, TimeSpan.Zero);
                timer.Dispose();
                canceled.SetResult();
            });
            started.SetResult();
            await canceled.Task;
            Assert.True(token.IsCancellationRequested);
            await release.Task;
        }, 0, new(TimeSpan.Zero, TimeSpan.Zero));
        var invocation = fixture.InvokeAsync(Assert.Single(fixture.Messages));
        try
        {
            await started.Task;
            timer.Change(TimeSpan.Zero, TimeSpan.Zero);
            timer.Dispose();
        }
        finally
        {
            timer.Dispose();
            release.SetResult();
            await invocation;
        }
        timer.Dispose();
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, calls);
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.Time.TimerCreations);
        Assert.Collection(diagnostics.Events,
            evt => Assert.IsType<GrainTimerEvents.Created>(evt),
            evt => Assert.IsType<GrainTimerEvents.TickStart>(evt),
            evt => Assert.IsType<GrainTimerEvents.Disposed>(evt),
            evt => Assert.IsType<GrainTimerEvents.TickStop>(evt));
    }

    [Fact]
    public async Task ZeroPeriod_QueuesNextTickOnlyAfterCurrentCallbackCompletes()
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            Assert.Equal(++calls, fixture.Messages.Count);
            return Task.CompletedTask;
        }, 0, new(TimeSpan.Zero, TimeSpan.Zero));
        for (var i = 0; i < 16; i++)
        {
            await fixture.InvokeAsync(fixture.Messages[i]);
            Assert.Equal(i + 2, fixture.Messages.Count);
        }
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        await fixture.InvokeAsync(fixture.Messages[16]);
        Assert.Equal(16, calls);
        Assert.Equal(17, fixture.Messages.Count);
        Assert.Equal(0, fixture.Time.TimerCreations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingAdmission_ConcurrentChangeAndDisposeDoNotHoldTimerLock(bool invalidate)
    {
        using var fixture = new TimerFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, new(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        fixture.BeforeReceive = () =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var admission = Task.Run(() => timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var mutation = Task.Run(() =>
            {
                timer.Change(invalidate ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                timer.Dispose();
            }, TestContext.Current.CancellationToken);
            await mutation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            release.SetResult();
            await admission;
        }
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, calls);
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.Time.TimerCreations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackFailure_PreservesPeriodAndPairsDiagnostics(bool asynchronous)
    {
        using var fixture = new TimerFixture();
        using var diagnostics = new TimerDiagnostics(fixture.Grain);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exception = new InvalidOperationException("Timer callback failure");
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            if (++calls > 1)
            {
                return Task.CompletedTask;
            }

            if (asynchronous)
            {
                return FailAsync();
            }

            throw exception;
        }, 0, new(TimeSpan.Zero, TimeSpan.FromSeconds(2)));

        var invocation = fixture.InvokeAsync(Assert.Single(fixture.Messages), exception);
        try
        {
            if (asynchronous)
            {
                await started.Task;
                fixture.Time.Advance(TimeSpan.FromDays(1));
                Assert.Single(fixture.Messages);
            }
        }
        finally
        {
            release.SetResult();
            await invocation;
        }
        fixture.Time.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
        Assert.Single(fixture.Messages);
        fixture.Time.Advance(TimeSpan.FromTicks(1));
        await fixture.InvokeAsync(fixture.Messages[1]);
        Assert.Equal(2, calls);
        Assert.Collection(diagnostics.Events,
            evt => Assert.IsType<GrainTimerEvents.Created>(evt),
            evt => Assert.IsType<GrainTimerEvents.TickStart>(evt),
            evt => Assert.Same(exception, Assert.IsType<GrainTimerEvents.TickStop>(evt).Exception),
            evt => Assert.IsType<GrainTimerEvents.TickStart>(evt),
            evt => Assert.Null(Assert.IsType<GrainTimerEvents.TickStop>(evt).Exception));

        async Task FailAsync()
        {
            started.SetResult();
            await release.Task;
            throw exception;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAdmission_LogsErrorAndPreservesReplacementSchedule(bool replaceWithDelay)
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Error).Returns(true);
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        using var fixture = new TimerFixture(loggerFactory);
        using var diagnostics = new TimerDiagnostics(fixture.Grain);
        var exception = new InvalidOperationException("Activation admission failure");
        var calls = 0;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, new(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        fixture.BeforeReceive = () =>
        {
            if (replaceWithDelay)
            {
                timer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            }
            throw exception;
        };
        timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, calls);
        var error = Assert.Single(logger.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(ILogger.Log)
            && Equals(call.GetArguments()[0], LogLevel.Error));
        Assert.Same(exception, error.GetArguments()[3]);
        fixture.BeforeReceive = null;
        if (replaceWithDelay)
        {
            fixture.Time.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
            Assert.Empty(fixture.Messages);
            fixture.Time.Advance(TimeSpan.FromTicks(1));
        }
        else
        {
            timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        Assert.Equal(1, calls);
        Assert.Single(diagnostics.Events.OfType<GrainTimerEvents.TickStart>());
        Assert.Single(diagnostics.Events.OfType<GrainTimerEvents.TickStop>());
        Assert.Equal(replaceWithDelay ? 1 : 0, fixture.Time.TimerCreations);
    }

    [Fact]
    public async Task CancellationRegistrationFailure_LogsErrorAndCompletesDisposalOnce()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Error).Returns(true);
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        using var fixture = new TimerFixture(loggerFactory);
        using var diagnostics = new TimerDiagnostics(fixture.Grain);
        var timerRegistry = Substitute.For<IGrainTimerRegistry>();
        fixture.Grain.GetComponent(typeof(IGrainTimerRegistry)).Returns(timerRegistry);
        var exception = new InvalidOperationException("Cancellation registration failure");
        CancellationToken callbackToken = default;
        using var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, token) =>
        {
            callbackToken = token;
            return Task.CompletedTask;
        }, 0, new(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        using var registration = callbackToken.Register(() => throw exception);
        timer.Dispose();
        timer.Dispose();
        Assert.True(callbackToken.IsCancellationRequested);
        var error = Assert.Single(logger.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(ILogger.Log)
            && Equals(call.GetArguments()[0], LogLevel.Error));
        var cancellationError = Assert.IsType<AggregateException>(error.GetArguments()[3]);
        Assert.Same(exception, Assert.Single(cancellationError.InnerExceptions));
        Assert.Single(diagnostics.Events.OfType<GrainTimerEvents.Disposed>());
        timerRegistry.Received(1).OnTimerDisposed(timer);
        timer.Change(TimeSpan.Zero, TimeSpan.Zero);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.Time.TimerCreations);
    }

    [Fact]
    public async Task CallbackRearmsImmediately_TickDiagnosticsRemainPaired()
    {
        using var fixture = new TimerFixture();
        using var diagnostics = new TimerDiagnostics(fixture.Grain);
        var calls = 0;
        IGrainTimer? timer = null;
        timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            if (++calls < 32)
            {
                timer!.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
            return Task.CompletedTask;
        }, 0, new(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        using (timer)
        {
            for (var i = 0; i < 32; i++)
            {
                await fixture.InvokeAsync(fixture.Messages[i]);
            }
            fixture.Time.Advance(TimeSpan.FromDays(1));
            Assert.Equal(32, calls);
            Assert.Equal(32, fixture.Messages.Count);
            Assert.Equal(0, fixture.Time.TimerCreations);
            Assert.Single(diagnostics.Events.OfType<GrainTimerEvents.Created>());
            for (var i = 0; i < 32; i++)
            {
                Assert.IsType<GrainTimerEvents.TickStart>(diagnostics.Events[1 + i * 2]);
                var stop = Assert.IsType<GrainTimerEvents.TickStop>(diagnostics.Events[2 + i * 2]);
                Assert.Null(stop.Exception);
            }
        }
        Assert.IsType<GrainTimerEvents.Disposed>(diagnostics.Events[^1]);
        Assert.Equal(66, diagnostics.Events.Count);
    }

    private sealed class TimerDiagnostics : IObserver<GrainTimerEvents.TimerEvent>, IDisposable
    {
        private readonly IGrainContext _grain;
        private readonly IDisposable _subscription;
        public List<GrainTimerEvents.TimerEvent> Events { get; } = [];
        public TimerDiagnostics(IGrainContext grain)
        {
            _grain = grain;
            _subscription = GrainTimerEvents.AllEvents.Subscribe(this);
        }
        public void OnNext(GrainTimerEvents.TimerEvent value)
        {
            if (ReferenceEquals(value.GrainContext, _grain))
            {
                Events.Add(value);
            }
        }
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
        public void Dispose() => _subscription.Dispose();
    }

    private sealed class TimerFixture : IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        public TrackingTimeProvider Time { get; } = new();
        public IGrainContext Grain { get; } = Substitute.For<IGrainContext>();
        public List<Message> Messages { get; } = [];
        public Action? BeforeReceive { get; set; }
        public TimerRegistry Registry { get; }

        public TimerFixture(ILoggerFactory? loggerFactory = null)
        {
            Grain.GrainId.Returns(GrainId.Create("timer-test", "one"));
            Grain.When(context => context.ReceiveMessage(Arg.Any<object>())).Do(call =>
            {
                BeforeReceive?.Invoke();
                Messages.Add((Message)call[0]);
            });
            var details = Substitute.For<ILocalSiloDetails>();
            details.SiloAddress.Returns(SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1));
            var factory = new MessageFactory(_services.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, null!);
            Registry = new(loggerFactory ?? NullLoggerFactory.Instance, Time, factory, details);
        }

        public async Task InvokeAsync(Message message, Exception? expectedException = null)
        {
            RequestContextExtensions.Import(message.RequestContextData);
            var invokable = Assert.IsAssignableFrom<IInvokable>(message.BodyObject);
            invokable.SetTarget(Grain);
            using var response = await invokable.Invoke();
            Assert.Same(expectedException, response.Exception);
        }

        public void Dispose() => _services.Dispose();
    }

    private sealed class TrackingTimeProvider : FakeTimeProvider
    {
        public int TimerCreations { get; private set; }
        private TimerCallback? _callback;
        private object? _state;
        public void FireStaleCallback() => _callback!(_state);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerCreations++;
            _callback = callback;
            _state = state;
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
