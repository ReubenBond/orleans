using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled combinators (<c>Task.WhenAll</c>, <c>Task.WhenAny</c>,
/// <c>TaskExtensions.Unwrap</c>) and every <c>ContinueWith</c> overload, with the BCL as the oracle. Each probe runs
/// rewritten under a simulation and uninstrumented against the real BCL, and the two must describe the same argument
/// validation, identities, results, fault aggregation, cancellation, continuation options, and task state. The probes
/// complete antecedents one at a time, so the order in which the BCL observes completions is deterministic too. Both
/// Release and Debug codegen are staged.
/// </summary>
public sealed class TaskCombinatorConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Conf { public static class CombinatorProbe {
            public static async Task<string> Combinators()
            {
                var lines = new List<string>();
                Validation(lines);
                Identities(lines);
                await WhenAllOutcomes(lines);
                await WhenAnyOutcomes(lines);
                await UnwrapOutcomes(lines);
                return string.Join(Environment.NewLine, lines);
            }

            private static void Validation(List<string> lines)
            {
                Task done = Task.CompletedTask;
                Task<int> one = Task.FromResult(1);
                Attempt(lines, "WhenAll(Task[] null)", () => Task.WhenAll((Task[])null!));
                Attempt(lines, "WhenAll(IEnumerable null)", () => Task.WhenAll((IEnumerable<Task>)null!));
                Attempt(lines, "WhenAll(single null)", () => Task.WhenAll(new Task[] { null! }));
                Attempt(lines, "WhenAll(span with null)", () => Task.WhenAll(done, null!));
                Attempt(lines, "WhenAll(list with null)", () => Task.WhenAll(new List<Task> { done, null! }));
                Attempt(lines, "WhenAll(sequence with null)", () => Task.WhenAll(Sequence(done, null!, done)));
                Attempt(lines, "WhenAll<T>(T[] null)", () => Task.WhenAll((Task<int>[])null!));
                Attempt(lines, "WhenAll<T>(IEnumerable null)", () => Task.WhenAll((IEnumerable<Task<int>>)null!));
                Attempt(lines, "WhenAll<T>(span with null)", () => Task.WhenAll(one, null!));
                Attempt(lines, "WhenAll<T>(list with null)", () => Task.WhenAll(new List<Task<int>> { one, null! }));
                Attempt(lines, "WhenAll<T>(sequence with null)", () => Task.WhenAll(Sequence(one, null!, one)));
                Attempt(lines, "WhenAny(Task[] null)", () => Task.WhenAny((Task[])null!));
                Attempt(lines, "WhenAny(IEnumerable null)", () => Task.WhenAny((IEnumerable<Task>)null!));
                Attempt(lines, "WhenAny(empty array)", () => Task.WhenAny(Array.Empty<Task>()));
                Attempt(lines, "WhenAny(empty list)", () => Task.WhenAny(new List<Task>()));
                Attempt(lines, "WhenAny(empty sequence)", () => Task.WhenAny(Sequence<Task>()));
                Attempt(lines, "WhenAny(first null)", () => Task.WhenAny(null!, done));
                Attempt(lines, "WhenAny(second null)", () => Task.WhenAny(done, null!));
                Attempt(lines, "WhenAny(two-element array with null)", () => Task.WhenAny(new Task[] { done, null! }));
                Attempt(lines, "WhenAny(three with null)", () => Task.WhenAny(done, done, null!));
                Attempt(lines, "WhenAny(list with null)", () => Task.WhenAny(new List<Task> { done, done, null! }));
                Attempt(lines, "WhenAny(sequence with null)", () => Task.WhenAny(Sequence(done, null!, done)));
                Attempt(lines, "WhenAny<T>(T[] null)", () => Task.WhenAny((Task<int>[])null!));
                Attempt(lines, "WhenAny<T>(empty)", () => Task.WhenAny(Array.Empty<Task<int>>()));
                Attempt(lines, "WhenAny<T>(second null)", () => Task.WhenAny(one, null!));
                Attempt(lines, "WhenAny<T>(three with null)", () => Task.WhenAny(one, one, null!));
                Attempt(lines, "WhenAny<T>(sequence with null)", () => Task.WhenAny(Sequence(one, null!)));
                Attempt(lines, "Unwrap(null)", () => ((Task<Task>)null!).Unwrap());
                Attempt(lines, "Unwrap<T>(null)", () => ((Task<Task<int>>)null!).Unwrap());
            }

            private static void Identities(List<string> lines)
            {
                var pending = new TaskCompletionSource();
                var other = new TaskCompletionSource();
                lines.Add("WhenAll() is CompletedTask: " + ReferenceEquals(Task.WhenAll(), Task.CompletedTask));
                lines.Add("WhenAll(empty list) is CompletedTask: " + ReferenceEquals(Task.WhenAll(new List<Task>()), Task.CompletedTask));
                lines.Add("WhenAll(single) is the task: " + ReferenceEquals(Task.WhenAll(new[] { pending.Task }), pending.Task));
                lines.Add("WhenAll(sequence of one) is the task: " + ReferenceEquals(Task.WhenAll(Sequence(pending.Task)), pending.Task));
                Task<int[]> empty = Task.WhenAll(Array.Empty<Task<int>>());
                lines.Add("WhenAll<T>(empty): " + empty.Status + " " + empty.CreationOptions + " length=" + empty.Result.Length);
                lines.Add("WhenAll pending: " + Task.WhenAll(pending.Task, other.Task).Status);

                var inner = new TaskCompletionSource<int>();
                lines.Add("Unwrap(completed outer) is the inner task: " + ReferenceEquals(Task.FromResult(inner.Task).Unwrap(), inner.Task));
                lines.Add("Unwrap(completed outer, null inner): " + Describe(Task.FromResult<Task>(null!).Unwrap()));
                lines.Add("Unwrap<T>(completed outer, null inner): " + Describe(Task.FromResult<Task<int>>(null!).Unwrap()));
                lines.Add("Unwrap(faulted outer): " + Describe(Task.FromException<Task>(new FormatException("outer")).Unwrap()));

                Task completed = Task.CompletedTask;
                Task<Task> any = Task.WhenAny(pending.Task, completed, other.Task);
                lines.Add("WhenAny(completed at call): " + any.Status + " winner=" + Index(any.Result, pending.Task, completed, other.Task));
                Task faulted = Task.FromException(new FormatException("first"));
                Task<Task> two = Task.WhenAny(pending.Task, faulted, completed);
                lines.Add("WhenAny(two completed at call): winner=" + Index(two.Result, pending.Task, faulted, completed));
                Task<Task> pair = Task.WhenAny(completed, faulted);
                lines.Add("WhenAny(pair completed at call): winner=" + Index(pair.Result, completed, faulted));
                lines.Add("WhenAny pending: " + Task.WhenAny(pending.Task, other.Task).Status);
                pending.SetResult();
                other.SetResult();
            }

            private static async Task WhenAllOutcomes(List<string> lines)
            {
                var a = new TaskCompletionSource();
                var b = new TaskCompletionSource();
                var c = new TaskCompletionSource();
                Task faults = Task.WhenAll(a.Task, b.Task, c.Task);
                await Task.Yield();
                c.SetException(new FormatException("c"));
                await Task.Yield();
                b.SetResult();
                await Task.Yield();
                a.SetException(new InvalidOperationException[] { new("a1"), new("a2") });
                lines.Add("WhenAll faults in completion order: " + await Settle(faults));

                var ca = new TaskCompletionSource();
                var cb = new TaskCompletionSource();
                var cc = new TaskCompletionSource();
                using var sa = new CancellationTokenSource();
                using var sb = new CancellationTokenSource();
                sa.Cancel();
                sb.Cancel();
                Task canceled = Task.WhenAll(ca.Task, cb.Task, cc.Task);
                await Task.Yield();
                cb.SetCanceled(sb.Token);
                await Task.Yield();
                ca.SetCanceled(sa.Token);
                await Task.Yield();
                cc.SetResult();
                lines.Add("WhenAll first canceled in completion order: " + await Settle(canceled, sa.Token, sb.Token));

                var fa = new TaskCompletionSource();
                var fb = new TaskCompletionSource();
                Task mixed = Task.WhenAll(fa.Task, fb.Task);
                await Task.Yield();
                fa.SetCanceled(sa.Token);
                await Task.Yield();
                fb.SetException(new FormatException("fb"));
                lines.Add("WhenAll fault wins over cancellation: " + await Settle(mixed));

                var ra = new TaskCompletionSource<int>();
                var rb = new TaskCompletionSource<int>();
                var rc = new TaskCompletionSource<int>();
                Task<int[]> results = Task.WhenAll(ra.Task, rb.Task, rc.Task);
                await Task.Yield();
                rc.SetResult(3);
                await Task.Yield();
                ra.SetResult(1);
                await Task.Yield();
                rb.SetResult(2);
                lines.Add("WhenAll<T> results in argument order: " + string.Join(",", await results) + " " + results.Status);

                var ga = new TaskCompletionSource<int>();
                var gb = new TaskCompletionSource<int>();
                var gc = new TaskCompletionSource<int>();
                Task<int[]> generic = Task.WhenAll(new List<Task<int>> { ga.Task, gb.Task, gc.Task });
                await Task.Yield();
                gc.SetException(new FormatException("gc"));
                await Task.Yield();
                gb.SetCanceled(sb.Token);
                await Task.Yield();
                ga.SetException(new InvalidOperationException("ga"));
                lines.Add("WhenAll<T> faults in argument order: " + await Settle(generic));

                var ha = new TaskCompletionSource<int>();
                var hb = new TaskCompletionSource<int>();
                Task<int[]> genericCanceled = Task.WhenAll(Sequence(ha.Task, hb.Task));
                await Task.Yield();
                hb.SetCanceled(sb.Token);
                await Task.Yield();
                ha.SetCanceled(sa.Token);
                lines.Add("WhenAll<T> first canceled in argument order: " + await Settle(genericCanceled, sa.Token, sb.Token));
            }

            private static async Task WhenAnyOutcomes(List<string> lines)
            {
                var a = new TaskCompletionSource<int>();
                var b = new TaskCompletionSource<int>();
                var c = new TaskCompletionSource<int>();
                Task<Task<int>> any = Task.WhenAny(a.Task, b.Task, c.Task);
                await Task.Yield();
                b.SetException(new FormatException("b"));
                Task<int> winner = await any;
                lines.Add("WhenAny<T> winner: " + Index(winner, a.Task, b.Task, c.Task) + " " + winner.Status + " " + any.Status);
                a.SetResult(1);
                c.SetResult(3);

                var pa = new TaskCompletionSource();
                var pb = new TaskCompletionSource();
                Task<Task> pair = Task.WhenAny(pa.Task, pb.Task);
                await Task.Yield();
                pb.SetCanceled();
                lines.Add("WhenAny pair winner: " + Index(await pair, pa.Task, pb.Task));
                pa.SetResult();

                var la = new TaskCompletionSource();
                var lb = new TaskCompletionSource();
                var lc = new TaskCompletionSource();
                Task<Task> listed = Task.WhenAny(new List<Task> { la.Task, lb.Task, lc.Task });
                await Task.Yield();
                lc.SetResult();
                lines.Add("WhenAny list winner: " + Index(await listed, la.Task, lb.Task, lc.Task));
                la.SetResult();
                lb.SetResult();
            }

            private static async Task UnwrapOutcomes(List<string> lines)
            {
                var outer = new TaskCompletionSource<Task<int>>();
                var inner = new TaskCompletionSource<int>();
                Task<int> unwrapped = outer.Task.Unwrap();
                lines.Add("Unwrap<T> pending: " + unwrapped.Status + " " + unwrapped.CreationOptions);
                await Task.Yield();
                outer.SetResult(inner.Task);
                await Task.Yield();
                inner.SetResult(42);
                lines.Add("Unwrap<T> result: " + await unwrapped + " " + unwrapped.Status);

                var faultedOuter = new TaskCompletionSource<Task>();
                Task fromFaultedOuter = faultedOuter.Task.Unwrap();
                await Task.Yield();
                faultedOuter.SetException(new FormatException("outer"));
                lines.Add("Unwrap faulted outer: " + await Settle(fromFaultedOuter));

                using var source = new CancellationTokenSource();
                source.Cancel();
                Task<Task<int>> canceledOuter = CancelWith(source.Token);
                Task<int> fromCanceledOuter = canceledOuter.Unwrap();
                lines.Add("Unwrap<T> canceled outer: " + await Settle(fromCanceledOuter, source.Token));

                var nullInnerOuter = new TaskCompletionSource<Task>();
                Task fromNullInner = nullInnerOuter.Task.Unwrap();
                await Task.Yield();
                nullInnerOuter.SetResult(null!);
                lines.Add("Unwrap pending outer, null inner: " + await Settle(fromNullInner));

                var faultingOuter = new TaskCompletionSource<Task<int>>();
                var faultingInner = new TaskCompletionSource<int>();
                Task<int> fromFaultingInner = faultingOuter.Task.Unwrap();
                faultingOuter.SetResult(faultingInner.Task);
                await Task.Yield();
                faultingInner.SetException(new InvalidOperationException[] { new("i1"), new("i2") });
                lines.Add("Unwrap<T> faulted inner: " + await Settle(fromFaultingInner));
            }

            private static async Task<Task<int>> CancelWith(CancellationToken token)
            {
                await Task.Yield();
                throw new OperationCanceledException("custom cancellation", token);
            }

            private static async Task<string> Settle(Task task, params CancellationToken[] tokens)
            {
                string awaited;
                try
                {
                    await task;
                    awaited = "ok";
                }
                catch (OperationCanceledException exception)
                {
                    int token = Array.IndexOf(tokens, exception.CancellationToken);
                    awaited = exception.GetType().Name + "(" + exception.Message + ") token=" + token +
                        " canBeCanceled=" + exception.CancellationToken.CanBeCanceled;
                }
                catch (Exception exception)
                {
                    awaited = exception.GetType().Name + "(" + exception.Message + ")";
                }

                return Describe(task) + " awaited=" + awaited;
            }

            private static string Describe(Task task)
            {
                string description = task.Status + " options=" + task.CreationOptions;
                if (task.Exception is AggregateException aggregate)
                {
                    foreach (Exception inner in aggregate.InnerExceptions)
                    {
                        description += " fault=" + inner.GetType().Name + "(" + inner.Message + ")";
                    }
                }

                if (task.IsCanceled)
                {
                    try
                    {
                        task.GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException exception)
                    {
                        description += " canceledBy=" + exception.CancellationToken.IsCancellationRequested;
                    }
                }

                return description;
            }

            private static int Index(Task winner, params Task[] tasks) => Array.IndexOf(tasks, winner);

            private static void Attempt(List<string> lines, string label, Func<object> action)
            {
                try
                {
                    object result = action();
                    lines.Add(label + ": " + (result is Task task ? task.Status.ToString() : result));
                }
                catch (Exception exception)
                {
                    lines.Add(label + ": " + exception.GetType().Name + " param=" + (exception as ArgumentException)?.ParamName +
                        " message=" + exception.Message);
                }
            }

            private static IEnumerable<T> Sequence<T>(params T[] items)
            {
                foreach (T item in items)
                {
                    yield return item;
                }
            }

            private static readonly TaskContinuationOptions[] Options =
            [
                TaskContinuationOptions.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskContinuationOptions.OnlyOnCanceled,
                TaskContinuationOptions.NotOnRanToCompletion,
                TaskContinuationOptions.NotOnFaulted,
                TaskContinuationOptions.NotOnCanceled,
                TaskContinuationOptions.LazyCancellation,
                TaskContinuationOptions.LazyCancellation | TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskContinuationOptions.DenyChildAttach | TaskContinuationOptions.HideScheduler |
                    TaskContinuationOptions.PreferFairness | TaskContinuationOptions.RunContinuationsAsynchronously,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.LazyCancellation | TaskContinuationOptions.NotOnFaulted,
                TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.NotOnFaulted | TaskContinuationOptions.NotOnCanceled,
                TaskContinuationOptions.LongRunning | TaskContinuationOptions.ExecuteSynchronously,
                (TaskContinuationOptions)128,
            ];

            // Every continuation option, antecedent outcome, and token state, through the full overload of one shape.
            // Token modes: 0 none, 1 canceled before ContinueWith, 2 canceled while the antecedent is pending, 3 canceled
            // by the continuation, which then throws the token's OperationCanceledException.
            public static async Task<string> ContinueWithMatrix(int shape)
            {
                var lines = new List<string>();
                foreach (TaskContinuationOptions options in Options)
                {
                    for (int outcome = 0; outcome < 3; outcome++)
                    {
                        for (int tokenMode = 0; tokenMode < 4; tokenMode++)
                        {
                            lines.Add(options + "/" + outcome + "/" + tokenMode + ": " + await ContinueWithCase(shape, options, outcome, tokenMode));
                        }
                    }
                }

                return string.Join(Environment.NewLine, lines);
            }

            private static async Task<string> ContinueWithCase(int shape, TaskContinuationOptions options, int outcome, int tokenMode)
            {
                var antecedent = new TaskCompletionSource<int>();
                using var source = new CancellationTokenSource();
                if (tokenMode == 1)
                {
                    source.Cancel();
                }

                var ran = new List<string>();
                Task continuation;
                try
                {
                    continuation = Continue(shape, antecedent.Task, source, tokenMode, options, ran);
                }
                catch (Exception exception)
                {
                    antecedent.SetResult(0);
                    return "throws " + exception.GetType().Name + " param=" + (exception as ArgumentException)?.ParamName +
                        " message=" + exception.Message;
                }

                string created = continuation.Status + " options=" + continuation.CreationOptions + " state=" + (continuation.AsyncState ?? "<null>");
                if (tokenMode == 2)
                {
                    source.Cancel();
                }

                await Task.Yield();
                switch (outcome)
                {
                    case 0:
                        antecedent.SetResult(5);
                        break;
                    case 1:
                        antecedent.SetException(new FormatException("boom"));
                        break;
                    default:
                        antecedent.SetCanceled();
                        break;
                }

                string settled = await SettleContinuation(continuation, source.Token);
                return created + " -> " + settled + " ran=" + string.Join(",", ran);
            }

            private static Task Continue(
                int shape,
                Task<int> antecedent,
                CancellationTokenSource source,
                int tokenMode,
                TaskContinuationOptions options,
                List<string> ran)
            {
                CancellationToken token = source.Token;
                TaskScheduler scheduler = TaskScheduler.Default;
                Task untyped = antecedent;
                return shape switch
                {
                    0 => untyped.ContinueWith(t => { Body(t, null, source, tokenMode, ran); }, token, options, scheduler),
                    1 => untyped.ContinueWith((t, state) => { Body(t, state, source, tokenMode, ran); }, "state", token, options, scheduler),
                    2 => untyped.ContinueWith(t => Body(t, null, source, tokenMode, ran) + 1, token, options, scheduler),
                    3 => untyped.ContinueWith((t, state) => Body(t, state, source, tokenMode, ran) + 2, "state", token, options, scheduler),
                    4 => antecedent.ContinueWith(t => { Body(t, null, source, tokenMode, ran); }, token, options, scheduler),
                    5 => antecedent.ContinueWith((t, state) => { Body(t, state, source, tokenMode, ran); }, "state", token, options, scheduler),
                    6 => antecedent.ContinueWith(t => Body(t, null, source, tokenMode, ran) + 3, token, options, scheduler),
                    _ => antecedent.ContinueWith((t, state) => Body(t, state, source, tokenMode, ran) + 4, "state", token, options, scheduler),
                };
            }

            private static int Body(Task antecedent, object? state, CancellationTokenSource source, int tokenMode, List<string> ran)
            {
                ran.Add(antecedent.Status + "/" + (state ?? "<null>") + "/current=" + (TaskScheduler.Current == TaskScheduler.Default));
                if (tokenMode == 3)
                {
                    source.Cancel();
                    source.Token.ThrowIfCancellationRequested();
                }

                return 10;
            }

            private static async Task<string> SettleContinuation(Task continuation, CancellationToken token)
            {
                try
                {
                    await continuation;
                    return "RanToCompletion" + (continuation is Task<int> result ? " result=" + result.Result : string.Empty);
                }
                catch (OperationCanceledException exception)
                {
                    return continuation.Status + " " + exception.GetType().Name + " token=" +
                        (exception.CancellationToken == token ? "own" : exception.CancellationToken.CanBeCanceled ? "other" : "none");
                }
                catch (Exception exception)
                {
                    return continuation.Status + " " + exception.GetType().Name + "(" + exception.Message + ")";
                }
            }

            // Every overload once, on a completed antecedent, with its default or explicit arguments.
            public static async Task<string> Overloads()
            {
                var lines = new List<string>();
                Task done = Task.CompletedTask;
                Task<int> five = Task.FromResult(5);
                CancellationToken none = CancellationToken.None;
                TaskScheduler scheduler = TaskScheduler.Default;
                TaskContinuationOptions options = TaskContinuationOptions.DenyChildAttach;
                Task[] continuations =
                [
                    done.ContinueWith(_ => { }),
                    done.ContinueWith(_ => { }, none),
                    done.ContinueWith(_ => { }, scheduler),
                    done.ContinueWith(_ => { }, options),
                    done.ContinueWith(_ => { }, none, options, scheduler),
                    done.ContinueWith((_, s) => { }, "s1"),
                    done.ContinueWith((_, s) => { }, "s2", none),
                    done.ContinueWith((_, s) => { }, "s3", scheduler),
                    done.ContinueWith((_, s) => { }, "s4", options),
                    done.ContinueWith((_, s) => { }, "s5", none, options, scheduler),
                    done.ContinueWith(_ => 1),
                    done.ContinueWith(_ => 2, none),
                    done.ContinueWith(_ => 3, scheduler),
                    done.ContinueWith(_ => 4, options),
                    done.ContinueWith(_ => 5, none, options, scheduler),
                    done.ContinueWith((_, s) => (string)s! + "!", "s6"),
                    done.ContinueWith((_, s) => (string)s! + "!", "s7", none),
                    done.ContinueWith((_, s) => (string)s! + "!", "s8", scheduler),
                    done.ContinueWith((_, s) => (string)s! + "!", "s9", options),
                    done.ContinueWith((_, s) => (string)s! + "!", "s10", none, options, scheduler),
                    five.ContinueWith(t => { }),
                    five.ContinueWith(t => { }, none),
                    five.ContinueWith(t => { }, scheduler),
                    five.ContinueWith(t => { }, options),
                    five.ContinueWith(t => { }, none, options, scheduler),
                    five.ContinueWith((t, s) => { }, "g1"),
                    five.ContinueWith((t, s) => { }, "g2", none),
                    five.ContinueWith((t, s) => { }, "g3", scheduler),
                    five.ContinueWith((t, s) => { }, "g4", options),
                    five.ContinueWith((t, s) => { }, "g5", none, options, scheduler),
                    five.ContinueWith(t => t.Result + 10),
                    five.ContinueWith(t => t.Result + 20, none),
                    five.ContinueWith(t => t.Result + 30, scheduler),
                    five.ContinueWith(t => t.Result + 40, options),
                    five.ContinueWith(t => t.Result + 50, none, options, scheduler),
                    five.ContinueWith((t, s) => (string)s! + t.Result, "g6"),
                    five.ContinueWith((t, s) => (string)s! + t.Result, "g7", none),
                    five.ContinueWith((t, s) => (string)s! + t.Result, "g8", scheduler),
                    five.ContinueWith((t, s) => (string)s! + t.Result, "g9", options),
                    five.ContinueWith((t, s) => (string)s! + t.Result, "g10", none, options, scheduler),
                ];

                foreach (Task continuation in continuations)
                {
                    await continuation;
                    string result = continuation switch
                    {
                        Task<int> number => number.Result.ToString(),
                        Task<string> text => text.Result,
                        _ => "-",
                    };
                    lines.Add(continuation.Status + " " + continuation.CreationOptions + " state=" + (continuation.AsyncState ?? "<null>") + " result=" + result);
                }

                Attempt(lines, "null action", () => done.ContinueWith((Action<Task>)null!));
                Attempt(lines, "null function", () => five.ContinueWith((Func<Task<int>, int>)null!, none));
                Attempt(lines, "null scheduler", () => done.ContinueWith(_ => { }, (TaskScheduler)null!));
                Attempt(lines, "null action and scheduler", () => done.ContinueWith((Action<Task, object?>)null!, null, (TaskScheduler)null!));
                Attempt(lines, "null antecedent", () => ((Task)null!).ContinueWith(_ => { }, (TaskScheduler)null!));
                Attempt(lines, "null generic antecedent", () => ((Task<int>)null!).ContinueWith(t => 1, (TaskContinuationOptions)128));
                return string.Join(Environment.NewLine, lines);
            }

            public static string Unsupported()
            {
                var lines = new List<string>();
                Task done = Task.CompletedTask;
                TaskScheduler exclusive = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
                Attempt(lines, "LongRunning", () => done.ContinueWith(_ => { }, TaskContinuationOptions.LongRunning));
                Attempt(lines, "AttachedToParent", () => done.ContinueWith(_ => 1, TaskContinuationOptions.AttachedToParent));
                Attempt(lines, "custom scheduler", () => done.ContinueWith(_ => { }, exclusive));
                Attempt(lines, "generic custom scheduler", () => Task.FromResult(1).ContinueWith((t, s) => 1, null, exclusive));
                return string.Join(Environment.NewLine, lines);
            }

            // Passes TaskScheduler.Current explicitly to each shape before the first await: the simulation's own task
            // scheduler when the probe is the root task of a cluster's RunToCompletion, TaskScheduler.Default otherwise.
            public static async Task<string> CurrentScheduler(Action<TaskScheduler> observe)
            {
                TaskScheduler current = TaskScheduler.Current;
                observe(current);
                Task done = Task.CompletedTask;
                Task<int> five = Task.FromResult(5);
                CancellationToken none = CancellationToken.None;
                TaskContinuationOptions options = TaskContinuationOptions.DenyChildAttach;
                Task[] continuations =
                [
                    done.ContinueWith(_ => { }, current),
                    done.ContinueWith((_, s) => { }, "s1", current),
                    done.ContinueWith(_ => 1, current),
                    done.ContinueWith((_, s) => (string)s! + "!", "s2", current),
                    done.ContinueWith(_ => { }, none, options, current),
                    five.ContinueWith(t => { }, current),
                    five.ContinueWith((t, s) => { }, "g1", current),
                    five.ContinueWith(t => t.Result + 1, current),
                    five.ContinueWith((t, s) => (string)s! + t.Result, "g2", current),
                    five.ContinueWith(t => t.Result + 2, none, options, current),
                ];

                var lines = new List<string>();
                foreach (Task continuation in continuations)
                {
                    await continuation;
                    string result = continuation switch
                    {
                        Task<int> number => number.Result.ToString(),
                        Task<string> text => text.Result,
                        _ => "-",
                    };
                    lines.Add(continuation.Status + " " + continuation.CreationOptions + " state=" + (continuation.AsyncState ?? "<null>") + " result=" + result);
                }

                return string.Join(Environment.NewLine, lines);
            }
        } }
        """;

    private const string ProbeType = "Conf.CombinatorProbe";

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;
    private readonly Lazy<UninstrumentedProbe> _bclRelease;
    private readonly Lazy<UninstrumentedProbe> _bclDebug;

    public TaskCombinatorConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.CombinatorRel", ProbeType, Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.CombinatorDbg", ProbeType, Source, optimize: false));
        _bclRelease = new Lazy<UninstrumentedProbe>(() =>
            _fixture.CompileUninstrumented("Conf.CombinatorBclRel", ProbeType, Source, optimize: true));
        _bclDebug = new Lazy<UninstrumentedProbe>(() =>
            _fixture.CompileUninstrumented("Conf.CombinatorBclDbg", ProbeType, Source, optimize: false));
    }

    public static TheoryData<bool> Optimizations => new() { true, false };

    public static TheoryData<bool, int> Shapes
    {
        get
        {
            var data = new TheoryData<bool, int>();
            foreach (bool optimize in new[] { true, false })
            {
                for (int shape = 0; shape < 8; shape++)
                {
                    data.Add(optimize, shape);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public async Task CombinatorsBehaveAsTheBcl(bool optimize)
    {
        string expected = await Bcl("Combinators", optimize);

        string actual = Simulated("Combinators", optimize);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task ContinueWithOptionsBehaveAsTheBcl(bool optimize, int shape)
    {
        string expected = await Bcl("ContinueWithMatrix", optimize, shape);

        string actual = Simulated("ContinueWithMatrix", optimize, shape);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public async Task EveryContinueWithOverloadBehavesAsTheBcl(bool optimize)
    {
        string expected = await Bcl("Overloads", optimize);

        string actual = Simulated("Overloads", optimize);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void ContinuationsWhoseSchedulingCannotBePreservedAreRejected(bool optimize)
    {
        using var host = new SimulationHost(Start);

        string actual = (string)host.Invoke(Method("Unsupported", optimize))!;

        string[] lines = actual.Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.All(lines, line => Assert.Contains(": SimulationApiException param= message=The task API 'System.Threading.Tasks.Task", line, StringComparison.Ordinal));
        Assert.Contains("LongRunning requests a dedicated thread", lines[0], StringComparison.Ordinal);
        Assert.Contains("AttachedToParent attaches the continuation", lines[1], StringComparison.Ordinal);
        Assert.Contains("custom TaskScheduler", lines[2], StringComparison.Ordinal);
        Assert.Contains("Task`1.ContinueWith", lines[3], StringComparison.Ordinal);
    }

    // The root task of SimulationCluster.RunToCompletion runs on the cluster's SimulationTaskScheduler, so
    // TaskScheduler.Current is that scheduler until the probe's first await: an overload without a scheduler treats
    // it as TaskScheduler.Default, as TaskFactory.StartNew does.
    [Theory]
    [MemberData(nameof(Optimizations))]
    public async Task EveryContinueWithOverloadBehavesAsTheBclUnderRunToCompletion(bool optimize)
    {
        string expected = await Bcl("Overloads", optimize);

        string actual = SimulatedRoot("Overloads", optimize);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task ContinueWithOptionsBehaveAsTheBclUnderRunToCompletion(bool optimize, int shape)
    {
        string expected = await Bcl("ContinueWithMatrix", optimize, shape);

        string actual = SimulatedRoot("ContinueWithMatrix", optimize, shape);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public async Task TheSimulationsOwnTaskSchedulerIsAcceptedExplicitly(bool optimize)
    {
        string expected = await Bcl("CurrentScheduler", optimize, new Action<TaskScheduler>(_ => { }));
        var observed = new List<TaskScheduler>();
        using var host = new SimulationHost(Start);

        string actual = host.RunToCompletion<string>(
            Method("CurrentScheduler", optimize),
            new Action<TaskScheduler>(observed.Add));

        Assert.IsType<SimulationTaskScheduler>(Assert.Single(observed));
        Assert.Equal(expected, actual);
    }

    private async Task<string> Bcl(string name, bool optimize, params object[] arguments)
    {
        MethodInfo method = (optimize ? _bclRelease : _bclDebug).Value.Method(name);
        return await (Task<string>)method.Invoke(null, arguments)!;
    }

    private string Simulated(string name, bool optimize, params object[] arguments)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<string>)host.Invoke(Method(name, optimize), arguments)!;
        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        return task.GetAwaiter().GetResult();
    }

    private string SimulatedRoot(string name, bool optimize, params object[] arguments)
    {
        using var host = new SimulationHost(Start);
        return host.RunToCompletion<string>(Method(name, optimize), arguments);
    }

    private MethodInfo Method(string name, bool optimize) =>
        (optimize ? _release : _debug).Value.Method(name);

    public void Dispose() => _fixture.Dispose();
}
