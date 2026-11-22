using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the multi-task surfaces, with the BCL as the oracle: every <c>Task.WhenEach</c> overload,
/// and every <c>TaskFactory</c> and <c>TaskFactory&lt;TResult&gt;</c> <c>ContinueWhenAll</c>/<c>ContinueWhenAny</c>
/// overload. Each probe runs rewritten under a simulation and uninstrumented against the real BCL, and the two must
/// describe the same argument validation, yield order, enumeration, cancellation, continuation options, state, and task
/// state. The probes complete antecedents one scheduling step at a time, so the order in which the BCL observes
/// completions is deterministic too. <c>TaskFactory.FromAsync</c> is rejected. Both Release and Debug codegen are
/// staged.
/// </summary>
public sealed class MultiTaskConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Conf { public static class MultiTaskProbe {
            public static async Task<string> Validation()
            {
                var lines = new List<string>();
                Task done = Task.CompletedTask;
                Task<int> one = Task.FromResult(1);
                TaskFactory factory = Task.Factory;
                TaskFactory<int> typed = Task<int>.Factory;
                Action<Task[]> all = _ => { };
                Action<Task> any = _ => { };
                CancellationToken none = CancellationToken.None;
                Attempt(lines, "WhenEach(Task[] null)", () => Task.WhenEach((Task[])null!));
                Attempt(lines, "WhenEach(IEnumerable null)", () => Task.WhenEach((IEnumerable<Task>)null!));
                Attempt(lines, "WhenEach(array with null)", () => Task.WhenEach(new Task[] { done, null! }));
                Attempt(lines, "WhenEach(span with null)", () => Task.WhenEach(done, null!));
                Attempt(lines, "WhenEach(sequence with null)", () => Task.WhenEach(Sequence(done, null!)));
                Attempt(lines, "WhenEach<T>(T[] null)", () => Task.WhenEach((Task<int>[])null!));
                Attempt(lines, "WhenEach<T>(IEnumerable null)", () => Task.WhenEach((IEnumerable<Task<int>>)null!));
                Attempt(lines, "WhenEach<T>(span with null)", () => Task.WhenEach(one, null!));
                Attempt(lines, "WhenEach<T>(sequence with null)", () => Task.WhenEach(Sequence(one, null!)));
                lines.Add("WhenEach(empty array) yields " + await Count(Task.WhenEach(Array.Empty<Task>())));
                lines.Add("WhenEach(empty sequence) yields " + await Count(Task.WhenEach(Sequence<Task>())));
                lines.Add("WhenEach<T>(empty span) yields " + await Count(Task.WhenEach(ReadOnlySpan<Task<int>>.Empty)));

                Attempt(lines, "ContinueWhenAll(null tasks)", () => factory.ContinueWhenAll(null!, all));
                Attempt(lines, "ContinueWhenAll(empty)", () => factory.ContinueWhenAll(Array.Empty<Task>(), all));
                Attempt(lines, "ContinueWhenAll(null element)", () => factory.ContinueWhenAll(new[] { done, null! }, all));
                Attempt(lines, "ContinueWhenAll(null action)", () => factory.ContinueWhenAll(new[] { done }, (Action<Task[]>)null!));
                Attempt(lines, "ContinueWhenAll(null action and tasks)", () => factory.ContinueWhenAll(null!, (Action<Task[]>)null!));
                Attempt(lines, "ContinueWhenAll(null function)", () => factory.ContinueWhenAll(new[] { done }, (Func<Task[], int>)null!));
                Attempt(lines, "ContinueWhenAll(null scheduler)", () => factory.ContinueWhenAll(new[] { done }, all, none, TaskContinuationOptions.None, null!));
                Attempt(lines, "ContinueWhenAll(null scheduler, empty)", () => factory.ContinueWhenAll(Array.Empty<Task>(), all, none, TaskContinuationOptions.None, null!));
                Attempt(lines, "ContinueWhenAll(NotOnFaulted)", () => factory.ContinueWhenAll(new[] { done }, all, TaskContinuationOptions.NotOnFaulted));
                Attempt(lines, "ContinueWhenAll(OnlyOnRanToCompletion, null tasks)", () => factory.ContinueWhenAll(null!, all, TaskContinuationOptions.OnlyOnRanToCompletion));
                Attempt(lines, "ContinueWhenAll(LongRunning|ExecuteSynchronously)", () => factory.ContinueWhenAll(new[] { done }, all, TaskContinuationOptions.LongRunning | TaskContinuationOptions.ExecuteSynchronously));
                Attempt(lines, "ContinueWhenAll(RunContinuationsAsynchronously)", () => factory.ContinueWhenAll(new[] { done }, all, TaskContinuationOptions.RunContinuationsAsynchronously));
                Attempt(lines, "ContinueWhenAll(128)", () => factory.ContinueWhenAll(new[] { done }, all, (TaskContinuationOptions)128));
                Attempt(lines, "ContinueWhenAll<T>(null element)", () => factory.ContinueWhenAll(new[] { one, null! }, (Task<int>[] _) => { }));
                Attempt(lines, "ContinueWhenAll null factory", () => ((TaskFactory)null!).ContinueWhenAll(null!, (Action<Task[]>)null!));
                Attempt(lines, "typed ContinueWhenAll(empty)", () => typed.ContinueWhenAll(Array.Empty<Task>(), _ => 1));
                Attempt(lines, "typed ContinueWhenAll<T>(null function)", () => typed.ContinueWhenAll(new[] { one }, (Func<Task<int>[], int>)null!));
                Attempt(lines, "typed ContinueWhenAll null factory", () => ((TaskFactory<int>)null!).ContinueWhenAll(null!, (Func<Task[], int>)null!));

                Attempt(lines, "ContinueWhenAny(null tasks)", () => factory.ContinueWhenAny(null!, any));
                Attempt(lines, "ContinueWhenAny(empty)", () => factory.ContinueWhenAny(Array.Empty<Task>(), any));
                Attempt(lines, "ContinueWhenAny(null element)", () => factory.ContinueWhenAny(new[] { done, null! }, any));
                Attempt(lines, "ContinueWhenAny(null element first)", () => factory.ContinueWhenAny(new[] { null!, done }, any));
                Attempt(lines, "ContinueWhenAny(null action)", () => factory.ContinueWhenAny(new[] { done }, (Action<Task>)null!));
                Attempt(lines, "ContinueWhenAny(null scheduler)", () => factory.ContinueWhenAny(new[] { done }, any, none, TaskContinuationOptions.None, null!));
                Attempt(lines, "ContinueWhenAny(null scheduler, empty)", () => factory.ContinueWhenAny(Array.Empty<Task>(), any, none, TaskContinuationOptions.None, null!));
                Attempt(lines, "ContinueWhenAny(NotOnCanceled)", () => factory.ContinueWhenAny(new[] { done }, any, TaskContinuationOptions.NotOnCanceled));
                Attempt(lines, "ContinueWhenAny<T>(null element)", () => factory.ContinueWhenAny(new[] { one, null! }, (Task<int> _) => 1));
                Attempt(lines, "typed ContinueWhenAny(null tasks)", () => typed.ContinueWhenAny(null!, _ => 1));
                Attempt(lines, "typed ContinueWhenAny null factory", () => ((TaskFactory<int>)null!).ContinueWhenAny(null!, (Func<Task, int>)null!));
                return string.Join(Environment.NewLine, lines);
            }

            public static async Task<string> WhenEach()
            {
                var lines = new List<string>();

                // Completions while the consumer waits, after a task already complete at the call.
                var a = new TaskCompletionSource();
                var b = new TaskCompletionSource();
                var c = new TaskCompletionSource();
                var d = new TaskCompletionSource();
                c.SetResult();
                Task[] tasks = [a.Task, b.Task, c.Task, d.Task];
                IAsyncEnumerable<Task> each = Task.WhenEach(tasks);
                var order = new List<string>();
                Task consumer = Consume(each, tasks, order);
                await Task.Yield();
                b.SetException(new FormatException("b"));
                await Task.Yield();
                d.SetCanceled();
                await Task.Yield();
                a.SetResult();
                await consumer;
                lines.Add("waiting consumer: " + string.Join(",", order));
                lines.Add("enumerated again yields " + await Count(each));

                // Completions before anything enumerates keep their completion order.
                var p = new TaskCompletionSource<int>();
                var q = new TaskCompletionSource<int>();
                var r = new TaskCompletionSource<int>();
                IAsyncEnumerable<Task<int>> late = Task.WhenEach(new List<Task<int>> { p.Task, q.Task, r.Task });
                await Task.Yield();
                r.SetResult(3);
                await Task.Yield();
                p.SetResult(1);
                await Task.Yield();
                q.SetResult(2);
                await Task.Yield();
                var results = new List<int>();
                await foreach (Task<int> completed in late)
                {
                    results.Add(await completed);
                }

                lines.Add("enumerated after completion: " + string.Join(",", results));

                // Tasks already complete at the call come in argument order, and the first enumerator to start takes them all.
                IAsyncEnumerable<Task<int>> typed = Task.WhenEach(Task.FromResult(1), Task.FromResult(2));
                IAsyncEnumerator<Task<int>> first = typed.GetAsyncEnumerator();
                IAsyncEnumerator<Task<int>> second = typed.GetAsyncEnumerator();
                lines.Add("current before start is null: " + (second.Current is null));
                bool moved = await second.MoveNextAsync();
                lines.Add("second: " + moved + " " + second.Current.Result);
                lines.Add("first: " + await first.MoveNextAsync() + " current null=" + (first.Current is null));
                moved = await second.MoveNextAsync();
                lines.Add("second: " + moved + " " + second.Current.Result);
                lines.Add("second ends: " + await second.MoveNextAsync() + " current null=" + (second.Current is null) + " again " + await second.MoveNextAsync());
                await first.DisposeAsync();
                await second.DisposeAsync();

                IAsyncEnumerable<Task> unstarted = Task.WhenEach(Task.CompletedTask);
                IAsyncEnumerator<Task> disposed = unstarted.GetAsyncEnumerator();
                await disposed.DisposeAsync();
                lines.Add("disposed before start: " + await disposed.MoveNextAsync() + ", then another yields " + await Count(unstarted));

                IAsyncEnumerable<Task> partial = Task.WhenEach(Task.CompletedTask, Task.FromResult(1));
                await foreach (Task _ in partial)
                {
                    break;
                }

                lines.Add("after break, another yields " + await Count(partial));

                // Cancellation: an available task fails with OperationCanceledException, a wait with TaskCanceledException.
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                lines.Add("canceled, available: " + await Fails(Task.WhenEach(Task.CompletedTask), canceled.Token));
                var never = new TaskCompletionSource();
                lines.Add("canceled, waiting: " + await Fails(Task.WhenEach(never.Task), canceled.Token));
                lines.Add("canceled, empty: " + await Fails(Task.WhenEach(Array.Empty<Task>()), canceled.Token));

                using var later = new CancellationTokenSource();
                var pending = new TaskCompletionSource();
                IAsyncEnumerator<Task> waiting = Task.WhenEach(pending.Task).GetAsyncEnumerator(later.Token);
                ValueTask<bool> move = waiting.MoveNextAsync();
                lines.Add("pending: " + move.IsCompleted);
                await Task.Yield();
                later.Cancel();
                lines.Add("canceled while waiting: " + await Awaited(move, later.Token) + " current null=" + (waiting.Current is null) +
                    " then " + await waiting.MoveNextAsync());
                await waiting.DisposeAsync();
                never.SetResult();
                pending.SetResult();
                return string.Join(Environment.NewLine, lines);
            }

            private static async Task Consume(IAsyncEnumerable<Task> each, Task[] tasks, List<string> order)
            {
                await foreach (Task task in each)
                {
                    order.Add(Array.IndexOf(tasks, task) + ":" + task.Status);
                }
            }

            private static async Task<int> Count<T>(IAsyncEnumerable<T> each)
            {
                int count = 0;
                await foreach (T _ in each)
                {
                    count++;
                }

                return count;
            }

            private static async Task<string> Fails(IAsyncEnumerable<Task> each, CancellationToken token)
            {
                IAsyncEnumerator<Task> enumerator = each.GetAsyncEnumerator(token);
                string outcome = await Outcome(enumerator.MoveNextAsync(), token);
                outcome += " current null=" + (enumerator.Current is null) + " then " + await enumerator.MoveNextAsync();
                await enumerator.DisposeAsync();
                return outcome;
            }

            private static async Task<string> Outcome(ValueTask<bool> move, CancellationToken token) =>
                "completed=" + move.IsCompleted + " canceled=" + move.IsCanceled + " " + await Awaited(move, token);

            private static async Task<string> Awaited(ValueTask<bool> move, CancellationToken token)
            {
                try
                {
                    return "moved " + await move;
                }
                catch (OperationCanceledException exception)
                {
                    return exception.GetType().Name + "(" + exception.Message + ") own=" + (exception.CancellationToken == token);
                }
            }

            private static readonly TaskContinuationOptions[] Options =
            [
                TaskContinuationOptions.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskContinuationOptions.LazyCancellation,
                TaskContinuationOptions.DenyChildAttach | TaskContinuationOptions.HideScheduler | TaskContinuationOptions.PreferFairness,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.LazyCancellation | TaskContinuationOptions.DenyChildAttach,
            ];

            // Every valid option set, antecedent outcome, and token state, through the full overload of one shape. Token
            // modes: 0 none, 1 canceled before the call, 2 canceled while the antecedents are pending, 3 canceled by the
            // continuation, which then throws the token's OperationCanceledException. The second antecedent completes
            // first, with the outcome; the first then succeeds.
            public static async Task<string> ContinueWhenMatrix(int shape)
            {
                var lines = new List<string>();
                foreach (TaskContinuationOptions options in Options)
                {
                    for (int outcome = 0; outcome < 3; outcome++)
                    {
                        for (int tokenMode = 0; tokenMode < 4; tokenMode++)
                        {
                            lines.Add(options + "/" + outcome + "/" + tokenMode + ": " + await ContinueWhenCase(shape, options, outcome, tokenMode));
                        }
                    }
                }

                return string.Join(Environment.NewLine, lines);
            }

            private static async Task<string> ContinueWhenCase(int shape, TaskContinuationOptions options, int outcome, int tokenMode)
            {
                var a = new TaskCompletionSource<int>();
                var b = new TaskCompletionSource<int>();
                Task<int>[] tasks = [a.Task, b.Task];
                using var source = new CancellationTokenSource();
                if (tokenMode == 1)
                {
                    source.Cancel();
                }

                var ran = new List<string>();
                (Task continuation, Delegate body) = ContinueWhen(shape, tasks, source, tokenMode, options, ran);
                string created = continuation.Status + " options=" + continuation.CreationOptions + " state=" + State(continuation.AsyncState, body);
                if (tokenMode == 2)
                {
                    source.Cancel();
                }

                await Task.Yield();
                switch (outcome)
                {
                    case 0:
                        b.SetResult(2);
                        break;
                    case 1:
                        b.SetException(new FormatException("b"));
                        break;
                    default:
                        b.SetCanceled();
                        break;
                }

                await Task.Yield();
                a.SetResult(1);
                string settled = await Settle(continuation, source.Token);
                lock (ran)
                {
                    return created + " -> " + settled + " ran=" + string.Join(",", ran);
                }
            }

            private static (Task Continuation, Delegate Body) ContinueWhen(
                int shape,
                Task<int>[] tasks,
                CancellationTokenSource source,
                int tokenMode,
                TaskContinuationOptions options,
                List<string> ran)
            {
                CancellationToken token = source.Token;
                TaskScheduler scheduler = TaskScheduler.Default;
                Task[] untyped = tasks;
                switch (shape)
                {
                    case 0:
                    {
                        Action<Task[]> body = all => Body(All(all, untyped), source, tokenMode, ran);
                        return (Task.Factory.ContinueWhenAll(untyped, body, token, options, scheduler), body);
                    }

                    case 1:
                    {
                        Action<Task<int>[]> body = all => Body(All(all, untyped), source, tokenMode, ran);
                        return (Task.Factory.ContinueWhenAll(tasks, body, token, options, scheduler), body);
                    }

                    case 2:
                    {
                        Func<Task[], int> body = all => Body(All(all, untyped), source, tokenMode, ran) + 1;
                        return (Task.Factory.ContinueWhenAll(untyped, body, token, options, scheduler), body);
                    }

                    case 3:
                    {
                        Func<Task<int>[], long> body = all => Body(All(all, untyped), source, tokenMode, ran) + all.Sum(t => t.IsCompletedSuccessfully ? t.Result : 0);
                        return (Task.Factory.ContinueWhenAll(tasks, body, token, options, scheduler), body);
                    }

                    case 4:
                    {
                        Action<Task> body = winner => Body(Any(winner, untyped), source, tokenMode, ran);
                        return (Task.Factory.ContinueWhenAny(untyped, body, token, options, scheduler), body);
                    }

                    case 5:
                    {
                        Action<Task<int>> body = winner => Body(Any(winner, untyped), source, tokenMode, ran);
                        return (Task.Factory.ContinueWhenAny(tasks, body, token, options, scheduler), body);
                    }

                    case 6:
                    {
                        Func<Task, int> body = winner => Body(Any(winner, untyped), source, tokenMode, ran) + 2;
                        return (Task.Factory.ContinueWhenAny(untyped, body, token, options, scheduler), body);
                    }

                    case 7:
                    {
                        Func<Task<int>, long> body = winner => Body(Any(winner, untyped), source, tokenMode, ran) + 3L;
                        return (Task.Factory.ContinueWhenAny(tasks, body, token, options, scheduler), body);
                    }

                    case 8:
                    {
                        Func<Task[], string> body = all => Body(All(all, untyped), source, tokenMode, ran) + "!";
                        return (Task<string>.Factory.ContinueWhenAll(untyped, body, token, options, scheduler), body);
                    }

                    case 9:
                    {
                        Func<Task<int>[], string> body = all => Body(All(all, untyped), source, tokenMode, ran) + "?";
                        return (Task<string>.Factory.ContinueWhenAll(tasks, body, token, options, scheduler), body);
                    }

                    case 10:
                    {
                        Func<Task, string> body = winner => Body(Any(winner, untyped), source, tokenMode, ran) + "#";
                        return (Task<string>.Factory.ContinueWhenAny(untyped, body, token, options, scheduler), body);
                    }

                    default:
                    {
                        Func<Task<int>, string> body = winner => Body(Any(winner, untyped), source, tokenMode, ran) + "$";
                        return (Task<string>.Factory.ContinueWhenAny(tasks, body, token, options, scheduler), body);
                    }
                }
            }

            private static string All(Task[] received, Task[] tasks) =>
                "copy=" + !ReferenceEquals(received, tasks) + " " + string.Join("|", received.Select(task => Array.IndexOf(tasks, task) + ":" + task.Status));

            private static string Any(Task winner, Task[] tasks) => "winner=" + Array.IndexOf(tasks, winner) + ":" + winner.Status;

            private static int Body(string received, CancellationTokenSource source, int tokenMode, List<string> ran)
            {
                lock (ran)
                {
                    ran.Add(received + " current=" + (TaskScheduler.Current == TaskScheduler.Default));
                }

                if (tokenMode == 3)
                {
                    source.Cancel();
                    source.Token.ThrowIfCancellationRequested();
                }

                return 10;
            }

            private static string State(object? state, Delegate body) =>
                state is null ? "<null>" : ReferenceEquals(state, body) ? "continuation" : state.ToString()!;

            private static async Task<string> Settle(Task continuation, CancellationToken token)
            {
                try
                {
                    await continuation;
                    string result = continuation switch
                    {
                        Task<int> number => " result=" + number.Result,
                        Task<long> number => " result=" + number.Result,
                        Task<string> text => " result=" + text.Result,
                        _ => string.Empty,
                    };
                    return "RanToCompletion" + result;
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

            // Every overload once, on completed antecedents, with the factory's defaults or explicit arguments; and the
            // defaults of factories constructed with a token, options, and a scheduler.
            public static async Task<string> Overloads()
            {
                var lines = new List<string>();
                Task[] t = [Task.CompletedTask, Task.FromResult(2)];
                Task<int>[] g = [Task.FromResult(1), Task.FromResult(2)];
                TaskFactory f = Task.Factory;
                TaskFactory<string> s = Task<string>.Factory;
                CancellationToken none = CancellationToken.None;
                TaskContinuationOptions o = TaskContinuationOptions.DenyChildAttach;
                TaskScheduler d = TaskScheduler.Default;
                Task[] continuations =
                [
                    f.ContinueWhenAll(t, _ => { }),
                    f.ContinueWhenAll(t, _ => { }, none),
                    f.ContinueWhenAll(t, _ => { }, o),
                    f.ContinueWhenAll(t, _ => { }, none, o, d),
                    f.ContinueWhenAll(g, _ => { }),
                    f.ContinueWhenAll(g, _ => { }, none),
                    f.ContinueWhenAll(g, _ => { }, o),
                    f.ContinueWhenAll(g, _ => { }, none, o, d),
                    f.ContinueWhenAll(t, all => all.Length),
                    f.ContinueWhenAll(t, all => all.Length + 10, none),
                    f.ContinueWhenAll(t, all => all.Length + 20, o),
                    f.ContinueWhenAll(t, all => all.Length + 30, none, o, d),
                    f.ContinueWhenAll(g, all => all.Sum(x => (long)x.Result)),
                    f.ContinueWhenAll(g, all => all.Sum(x => (long)x.Result) + 10, none),
                    f.ContinueWhenAll(g, all => all.Sum(x => (long)x.Result) + 20, o),
                    f.ContinueWhenAll(g, all => all.Sum(x => (long)x.Result) + 30, none, o, d),
                    f.ContinueWhenAny(t, _ => { }),
                    f.ContinueWhenAny(t, _ => { }, none),
                    f.ContinueWhenAny(t, _ => { }, o),
                    f.ContinueWhenAny(t, _ => { }, none, o, d),
                    f.ContinueWhenAny(g, _ => { }),
                    f.ContinueWhenAny(g, _ => { }, none),
                    f.ContinueWhenAny(g, _ => { }, o),
                    f.ContinueWhenAny(g, _ => { }, none, o, d),
                    f.ContinueWhenAny(t, winner => Array.IndexOf(t, winner)),
                    f.ContinueWhenAny(t, winner => Array.IndexOf(t, winner) + 10, none),
                    f.ContinueWhenAny(t, winner => Array.IndexOf(t, winner) + 20, o),
                    f.ContinueWhenAny(t, winner => Array.IndexOf(t, winner) + 30, none, o, d),
                    f.ContinueWhenAny(g, winner => (long)winner.Result),
                    f.ContinueWhenAny(g, winner => (long)winner.Result + 10, none),
                    f.ContinueWhenAny(g, winner => (long)winner.Result + 20, o),
                    f.ContinueWhenAny(g, winner => (long)winner.Result + 30, none, o, d),
                    s.ContinueWhenAll(t, all => "a" + all.Length),
                    s.ContinueWhenAll(t, all => "b" + all.Length, none),
                    s.ContinueWhenAll(t, all => "c" + all.Length, o),
                    s.ContinueWhenAll(t, all => "d" + all.Length, none, o, d),
                    s.ContinueWhenAll(g, all => "e" + all.Sum(x => x.Result)),
                    s.ContinueWhenAll(g, all => "f" + all.Sum(x => x.Result), none),
                    s.ContinueWhenAll(g, all => "g" + all.Sum(x => x.Result), o),
                    s.ContinueWhenAll(g, all => "h" + all.Sum(x => x.Result), none, o, d),
                    s.ContinueWhenAny(t, winner => "i" + Array.IndexOf(t, winner)),
                    s.ContinueWhenAny(t, winner => "j" + Array.IndexOf(t, winner), none),
                    s.ContinueWhenAny(t, winner => "k" + Array.IndexOf(t, winner), o),
                    s.ContinueWhenAny(t, winner => "l" + Array.IndexOf(t, winner), none, o, d),
                    s.ContinueWhenAny(g, winner => "m" + winner.Result),
                    s.ContinueWhenAny(g, winner => "n" + winner.Result, none),
                    s.ContinueWhenAny(g, winner => "o" + winner.Result, o),
                    s.ContinueWhenAny(g, winner => "p" + winner.Result, none, o, d),
                ];

                foreach (Task continuation in continuations)
                {
                    lines.Add(await Settle(continuation, none) + " " + continuation.CreationOptions + " state=" +
                        (continuation.AsyncState is Delegate ? "continuation" : continuation.AsyncState ?? "<null>"));
                }

                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                var pending = new TaskCompletionSource();
                Task[] waiting = [pending.Task];
                var eager = new TaskFactory(canceled.Token, TaskCreationOptions.None, TaskContinuationOptions.HideScheduler, null);
                var lazy = new TaskFactory<int>(canceled.Token, TaskCreationOptions.None, TaskContinuationOptions.LazyCancellation | TaskContinuationOptions.PreferFairness, null);
                var explicitDefault = new TaskFactory(TaskScheduler.Default);
                Task eagerAll = eager.ContinueWhenAll(waiting, _ => { });
                Task<int> eagerAny = eager.ContinueWhenAny(waiting, _ => 1);
                Task<int> lazyAll = lazy.ContinueWhenAll(waiting, _ => 2);
                Task<int> lazyAny = lazy.ContinueWhenAny(waiting, _ => 3);
                Task<int> scheduled = explicitDefault.ContinueWhenAll(waiting, _ => 4);
                foreach (Task created in new Task[] { eagerAll, eagerAny, lazyAll, lazyAny, scheduled })
                {
                    lines.Add("created " + created.Status + " " + created.CreationOptions + " state=" + (created.AsyncState is Delegate ? "continuation" : created.AsyncState ?? "<null>"));
                }

                await Task.Yield();
                pending.SetResult();
                foreach (Task created in new Task[] { eagerAll, eagerAny, lazyAll, lazyAny, scheduled })
                {
                    lines.Add("settled " + await Settle(created, canceled.Token));
                }

                return string.Join(Environment.NewLine, lines);
            }

            public static string Unsupported()
            {
                var lines = new List<string>();
                Task[] t = [Task.CompletedTask];
                TaskScheduler exclusive = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
                Attempt(lines, "LongRunning", () => Task.Factory.ContinueWhenAll(t, _ => { }, TaskContinuationOptions.LongRunning));
                Attempt(lines, "AttachedToParent", () => Task.Factory.ContinueWhenAny(t, _ => 1, TaskContinuationOptions.AttachedToParent));
                Attempt(lines, "custom scheduler", () => Task<int>.Factory.ContinueWhenAll(t, _ => 1, CancellationToken.None, TaskContinuationOptions.None, exclusive));
                Attempt(lines, "factory scheduler", () => new TaskFactory(exclusive).ContinueWhenAny(t, _ => { }));
                Attempt(lines, "FromAsync", () => Task.Factory.FromAsync(Task.CompletedTask, _ => { }));
                Attempt(lines, "FromAsync<T>", () => Task<int>.Factory.FromAsync(Task.CompletedTask, _ => 1));
                Attempt(lines, "FromAsync(begin, end, state)", () => Task.Factory.FromAsync<int, string>(
                    (argument, callback, state) => Task.CompletedTask,
                    _ => "x",
                    1,
                    null));
                return string.Join(Environment.NewLine, lines);
            }

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
        } }
        """;

    private const string ProbeType = "Conf.MultiTaskProbe";

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;
    private readonly Lazy<UninstrumentedProbe> _bclRelease;
    private readonly Lazy<UninstrumentedProbe> _bclDebug;

    public MultiTaskConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.MultiTaskRel", ProbeType, Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.MultiTaskDbg", ProbeType, Source, optimize: false));
        _bclRelease = new Lazy<UninstrumentedProbe>(() =>
            _fixture.CompileUninstrumented("Conf.MultiTaskBclRel", ProbeType, Source, optimize: true));
        _bclDebug = new Lazy<UninstrumentedProbe>(() =>
            _fixture.CompileUninstrumented("Conf.MultiTaskBclDbg", ProbeType, Source, optimize: false));
    }

    public static TheoryData<bool> Optimizations => new() { true, false };

    public static TheoryData<bool, int> Shapes
    {
        get
        {
            var data = new TheoryData<bool, int>();
            foreach (bool optimize in new[] { true, false })
            {
                for (int shape = 0; shape < 12; shape++)
                {
                    data.Add(optimize, shape);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public async Task ArgumentValidationIsTheBcls(bool optimize)
    {
        string expected = await Bcl("Validation", optimize);

        string actual = Simulated("Validation", optimize);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public async Task WhenEachBehavesAsTheBcl(bool optimize)
    {
        string expected = await Bcl("WhenEach", optimize);

        string actual = Simulated("WhenEach", optimize);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task ContinueWhenOptionsBehaveAsTheBcl(bool optimize, int shape)
    {
        string expected = await Bcl("ContinueWhenMatrix", optimize, shape);

        string actual = Simulated("ContinueWhenMatrix", optimize, shape);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public async Task EveryContinueWhenOverloadBehavesAsTheBcl(bool optimize)
    {
        string expected = await Bcl("Overloads", optimize);

        string actual = Simulated("Overloads", optimize);

        Assert.Equal(expected, actual);
    }

    // TaskScheduler.Current is the cluster's SimulationTaskScheduler in the RunToCompletion root until the first await.
    [Theory]
    [MemberData(nameof(Optimizations))]
    public async Task EveryContinueWhenOverloadBehavesAsTheBclUnderRunToCompletion(bool optimize)
    {
        string expected = await Bcl("Overloads", optimize);
        using var host = new SimulationHost(Start);

        string actual = host.RunToCompletion<string>(Method("Overloads", optimize));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void UnpreservableContinuationsAndFromAsyncAreRejected(bool optimize)
    {
        using var host = new SimulationHost(Start);

        string actual = (string)host.Invoke(Method("Unsupported", optimize))!;

        string[] lines = actual.Split(Environment.NewLine);
        Assert.Equal(7, lines.Length);
        Assert.All(lines, line => Assert.Contains(": SimulationApiException param= message=The task API 'System.Threading.Tasks.TaskFactory", line, StringComparison.Ordinal));
        Assert.Contains("LongRunning requests a dedicated thread", lines[0], StringComparison.Ordinal);
        Assert.Contains("AttachedToParent attaches the continuation", lines[1], StringComparison.Ordinal);
        Assert.Contains("TaskFactory`1.ContinueWhenAll' is not supported inside a Clockwork simulation: a custom TaskScheduler", lines[2], StringComparison.Ordinal);
        Assert.Contains("TaskFactory.ContinueWhenAny' is not supported inside a Clockwork simulation: a custom TaskScheduler", lines[3], StringComparison.Ordinal);
        Assert.Contains("TaskFactory.FromAsync' is not supported inside a Clockwork simulation: the task it returns completes when an IAsyncResult completes", lines[4], StringComparison.Ordinal);
        Assert.Contains("TaskFactory`1.FromAsync' is not supported", lines[5], StringComparison.Ordinal);
        Assert.Contains("TaskFactory.FromAsync' is not supported", lines[6], StringComparison.Ordinal);
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

    private MethodInfo Method(string name, bool optimize) =>
        (optimize ? _release : _debug).Value.Method(name);

    public void Dispose() => _fixture.Dispose();
}
