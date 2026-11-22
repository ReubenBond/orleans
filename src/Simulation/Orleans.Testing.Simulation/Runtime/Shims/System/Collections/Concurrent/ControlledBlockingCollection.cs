using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Collections.Concurrent;

/// <summary>
/// <para>
/// Controlled <see cref="BlockingCollection{T}"/> members. The BCL blocks a taker on an empty collection, and an adder on
/// a full bounded one, on <see cref="SemaphoreSlim"/> waits and wait handles inside the framework, which would block the
/// simulation's logical thread. The rewriter redirects every member which adds, takes, or completes adding here (the
/// instance members receiver first), and the shims keep the real collection as the only state.
/// </para>
/// <para>
/// Each shim first makes the BCL's own non-blocking attempt (the same member with a zero timeout), which validates the
/// arguments, checks disposal and the token, and adds or takes exactly as the BCL does. When that attempt would have had
/// to wait, the caller parks on the coordinator until the collection can satisfy it, adding is completed, the token is
/// cancelled, or the timeout elapses in virtual time, and then retries; a member whose BCL implementation would then
/// throw is called itself, since it throws without waiting. A wait which nothing can satisfy is reported as a deadlock.
/// A waiting adder which sees <see cref="BlockingCollection{T}.CompleteAdding"/> throws the BCL's
/// <see cref="InvalidOperationException"/>, and a waiting taker returns <see langword="false"/>, as in the BCL.
/// </para>
/// <para>
/// Every add and <see cref="BlockingCollection{T}.CompleteAdding"/> publishes a race-detection release edge on the
/// collection, which every take, and every observation that the collection is completed, acquires.
/// </para>
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "Mirrors BlockingCollection<T>, as every controlled shim mirrors its framework type's name.")]
public static class ControlledBlockingCollection
{
    private const string TypeName = "System.Collections.Concurrent.BlockingCollection`1.";
    private const string AddApi = TypeName + "Add";
    private const string TryAddApi = TypeName + "TryAdd";
    private const string TakeApi = TypeName + "Take";
    private const string TryTakeApi = TypeName + "TryTake";
    private const string GetConsumingEnumerableApi = TypeName + "GetConsumingEnumerable";
    private const string CompleteAddingApi = TypeName + "CompleteAdding";
    private const string AddToAnyApi = TypeName + "AddToAny";
    private const string TryAddToAnyApi = TypeName + "TryAddToAny";
    private const string TakeFromAnyApi = TypeName + "TakeFromAny";
    private const string TryTakeFromAnyApi = TypeName + "TryTakeFromAny";

    // The BCL's message (SR.BlockingCollection_Add_ConcurrentCompleteAdd) for an adder which CompleteAdding wakes.
    private const string ConcurrentCompleteAddMessage = "CompleteAdding may not be used concurrently with additions to the collection.";

    /// <summary>Controlled <see cref="BlockingCollection{T}.Add(T)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="item">The item to add.</param>
    public static void Add<T>(BlockingCollection<T> collection, T item)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(AddApi);
        _ = TryAddCore(collection, item, Timeout.Infinite, AddApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.Add(T, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="item">The item to add.</param>
    /// <param name="cancellationToken">The token that cancels the wait for space.</param>
    public static void Add<T>(BlockingCollection<T> collection, T item, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(AddApi);
        _ = TryAddCore(collection, item, Timeout.Infinite, AddApi, cancellationToken);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryAdd(T)"/>, which never waits.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="item">The item to add.</param>
    /// <returns><see langword="true"/> if the item was added.</returns>
    public static bool TryAdd<T>(BlockingCollection<T> collection, T item)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryAddApi);
        return TryAddCore(collection, item, 0, TryAddApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryAdd(T, TimeSpan)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="item">The item to add.</param>
    /// <param name="timeout">The time to wait for space, which elapses in virtual time.</param>
    /// <returns><see langword="true"/> if the item was added within the timeout.</returns>
    public static bool TryAdd<T>(BlockingCollection<T> collection, T item, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryAddApi);
        return IsValid(timeout)
            ? TryAddCore(collection, item, (int)timeout.TotalMilliseconds, TryAddApi, CancellationToken.None)
            : collection.TryAdd(item, timeout);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryAdd(T, int)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="item">The item to add.</param>
    /// <param name="millisecondsTimeout">The time to wait for space, which elapses in virtual time.</param>
    /// <returns><see langword="true"/> if the item was added within the timeout.</returns>
    public static bool TryAdd<T>(BlockingCollection<T> collection, T item, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryAddApi);
        return IsValid(millisecondsTimeout)
            ? TryAddCore(collection, item, millisecondsTimeout, TryAddApi, CancellationToken.None)
            : collection.TryAdd(item, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryAdd(T, int, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="item">The item to add.</param>
    /// <param name="millisecondsTimeout">The time to wait for space, which elapses in virtual time.</param>
    /// <param name="cancellationToken">The token that cancels the wait for space.</param>
    /// <returns><see langword="true"/> if the item was added within the timeout.</returns>
    public static bool TryAdd<T>(BlockingCollection<T> collection, T item, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryAddApi);
        return IsValid(millisecondsTimeout)
            ? TryAddCore(collection, item, millisecondsTimeout, TryAddApi, cancellationToken)
            : collection.TryAdd(item, millisecondsTimeout, cancellationToken);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.Take()"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <returns>The item taken.</returns>
    public static T Take<T>(BlockingCollection<T> collection)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TakeApi);
        return TryTakeCore(collection, out T? item, Timeout.Infinite, TakeApi, CancellationToken.None)
            ? item
            : collection.Take();
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.Take(CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="cancellationToken">The token that cancels the wait for an item.</param>
    /// <returns>The item taken.</returns>
    public static T Take<T>(BlockingCollection<T> collection, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TakeApi);

        // Unsatisfied only once the collection is completed, when the BCL's Take throws without waiting.
        return TryTakeCore(collection, out T? item, Timeout.Infinite, TakeApi, cancellationToken)
            ? item
            : collection.Take(cancellationToken);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryTake(out T)"/>, which never waits.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="item">The item taken.</param>
    /// <returns><see langword="true"/> if an item was taken.</returns>
    public static bool TryTake<T>(BlockingCollection<T> collection, [MaybeNullWhen(false)] out T item)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryTakeApi);
        return TryTakeCore(collection, out item, 0, TryTakeApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryTake(out T, TimeSpan)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="item">The item taken.</param>
    /// <param name="timeout">The time to wait for an item, which elapses in virtual time.</param>
    /// <returns><see langword="true"/> if an item was taken within the timeout.</returns>
    public static bool TryTake<T>(BlockingCollection<T> collection, [MaybeNullWhen(false)] out T item, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryTakeApi);
        return IsValid(timeout)
            ? TryTakeCore(collection, out item, (int)timeout.TotalMilliseconds, TryTakeApi, CancellationToken.None)
            : collection.TryTake(out item, timeout);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryTake(out T, int)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="item">The item taken.</param>
    /// <param name="millisecondsTimeout">The time to wait for an item, which elapses in virtual time.</param>
    /// <returns><see langword="true"/> if an item was taken within the timeout.</returns>
    public static bool TryTake<T>(BlockingCollection<T> collection, [MaybeNullWhen(false)] out T item, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryTakeApi);
        return IsValid(millisecondsTimeout)
            ? TryTakeCore(collection, out item, millisecondsTimeout, TryTakeApi, CancellationToken.None)
            : collection.TryTake(out item, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryTake(out T, int, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="item">The item taken.</param>
    /// <param name="millisecondsTimeout">The time to wait for an item, which elapses in virtual time.</param>
    /// <param name="cancellationToken">The token that cancels the wait for an item.</param>
    /// <returns><see langword="true"/> if an item was taken within the timeout.</returns>
    public static bool TryTake<T>(BlockingCollection<T> collection, [MaybeNullWhen(false)] out T item, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryTakeApi);
        return IsValid(millisecondsTimeout)
            ? TryTakeCore(collection, out item, millisecondsTimeout, TryTakeApi, cancellationToken)
            : collection.TryTake(out item, millisecondsTimeout, cancellationToken);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.GetConsumingEnumerable()"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <returns>A sequence which takes items until the collection is completed.</returns>
    public static IEnumerable<T> GetConsumingEnumerable<T>(BlockingCollection<T> collection)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(GetConsumingEnumerableApi);
        _ = collection.GetType();
        return Consume(collection, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.GetConsumingEnumerable(CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    /// <param name="cancellationToken">The token that cancels the wait for an item.</param>
    /// <returns>A sequence which takes items until the collection is completed.</returns>
    public static IEnumerable<T> GetConsumingEnumerable<T>(BlockingCollection<T> collection, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(GetConsumingEnumerableApi);
        _ = collection.GetType();
        return Consume(collection, cancellationToken);
    }

    /// <summary>
    /// Controlled <see cref="BlockingCollection{T}.CompleteAdding"/>: the BCL member, publishing the race-detection
    /// edge which a taker that observes the completion acquires.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collection">The receiving collection.</param>
    public static void CompleteAdding<T>(BlockingCollection<T> collection)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CompleteAddingApi);
        collection.CompleteAdding();
        RaceSynchronization.Signal(collection);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.AddToAny(BlockingCollection{T}[], T)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item to add.</param>
    /// <returns>The index of the collection the item was added to.</returns>
    public static int AddToAny<T>(BlockingCollection<T>[] collections, T item)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(AddToAnyApi);
        return TryAddToAnyCore(collections, item, Timeout.Infinite, AddToAnyApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.AddToAny(BlockingCollection{T}[], T, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item to add.</param>
    /// <param name="cancellationToken">The token that cancels the wait for space.</param>
    /// <returns>The index of the collection the item was added to.</returns>
    public static int AddToAny<T>(BlockingCollection<T>[] collections, T item, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(AddToAnyApi);
        return TryAddToAnyCore(collections, item, Timeout.Infinite, AddToAnyApi, cancellationToken);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryAddToAny(BlockingCollection{T}[], T)"/>, which never waits.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item to add.</param>
    /// <returns>The index of the collection the item was added to, or -1.</returns>
    public static int TryAddToAny<T>(BlockingCollection<T>[] collections, T item)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryAddToAnyApi);
        return TryAddToAnyCore(collections, item, 0, TryAddToAnyApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryAddToAny(BlockingCollection{T}[], T, TimeSpan)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item to add.</param>
    /// <param name="timeout">The time to wait for space, which elapses in virtual time.</param>
    /// <returns>The index of the collection the item was added to, or -1.</returns>
    public static int TryAddToAny<T>(BlockingCollection<T>[] collections, T item, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryAddToAnyApi);
        return IsValid(timeout)
            ? TryAddToAnyCore(collections, item, (int)timeout.TotalMilliseconds, TryAddToAnyApi, CancellationToken.None)
            : BlockingCollection<T>.TryAddToAny(collections, item, timeout);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryAddToAny(BlockingCollection{T}[], T, int)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item to add.</param>
    /// <param name="millisecondsTimeout">The time to wait for space, which elapses in virtual time.</param>
    /// <returns>The index of the collection the item was added to, or -1.</returns>
    public static int TryAddToAny<T>(BlockingCollection<T>[] collections, T item, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryAddToAnyApi);
        return IsValid(millisecondsTimeout)
            ? TryAddToAnyCore(collections, item, millisecondsTimeout, TryAddToAnyApi, CancellationToken.None)
            : BlockingCollection<T>.TryAddToAny(collections, item, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryAddToAny(BlockingCollection{T}[], T, int, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item to add.</param>
    /// <param name="millisecondsTimeout">The time to wait for space, which elapses in virtual time.</param>
    /// <param name="cancellationToken">The token that cancels the wait for space.</param>
    /// <returns>The index of the collection the item was added to, or -1.</returns>
    public static int TryAddToAny<T>(
        BlockingCollection<T>[] collections,
        T item,
        int millisecondsTimeout,
        CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryAddToAnyApi);
        return IsValid(millisecondsTimeout)
            ? TryAddToAnyCore(collections, item, millisecondsTimeout, TryAddToAnyApi, cancellationToken)
            : BlockingCollection<T>.TryAddToAny(collections, item, millisecondsTimeout, cancellationToken);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TakeFromAny(BlockingCollection{T}[], out T)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item taken.</param>
    /// <returns>The index of the collection the item was taken from.</returns>
    public static int TakeFromAny<T>(BlockingCollection<T>[] collections, out T? item)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TakeFromAnyApi);
        return TryTakeFromAnyCore(collections, out item, Timeout.Infinite, isTake: true, TakeFromAnyApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TakeFromAny(BlockingCollection{T}[], out T, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item taken.</param>
    /// <param name="cancellationToken">The token that cancels the wait for an item.</param>
    /// <returns>The index of the collection the item was taken from.</returns>
    public static int TakeFromAny<T>(BlockingCollection<T>[] collections, out T? item, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TakeFromAnyApi);
        return TryTakeFromAnyCore(collections, out item, Timeout.Infinite, isTake: true, TakeFromAnyApi, cancellationToken);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryTakeFromAny(BlockingCollection{T}[], out T)"/>, which never waits.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item taken.</param>
    /// <returns>The index of the collection the item was taken from, or -1.</returns>
    public static int TryTakeFromAny<T>(BlockingCollection<T>[] collections, out T? item)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryTakeFromAnyApi);
        return TryTakeFromAnyCore(collections, out item, 0, isTake: false, TryTakeFromAnyApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryTakeFromAny(BlockingCollection{T}[], out T, TimeSpan)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item taken.</param>
    /// <param name="timeout">The time to wait for an item, which elapses in virtual time.</param>
    /// <returns>The index of the collection the item was taken from, or -1.</returns>
    public static int TryTakeFromAny<T>(BlockingCollection<T>[] collections, out T? item, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryTakeFromAnyApi);
        return IsValid(timeout)
            ? TryTakeFromAnyCore(collections, out item, (int)timeout.TotalMilliseconds, isTake: false, TryTakeFromAnyApi, CancellationToken.None)
            : BlockingCollection<T>.TryTakeFromAny(collections, out item, timeout);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryTakeFromAny(BlockingCollection{T}[], out T, int)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item taken.</param>
    /// <param name="millisecondsTimeout">The time to wait for an item, which elapses in virtual time.</param>
    /// <returns>The index of the collection the item was taken from, or -1.</returns>
    public static int TryTakeFromAny<T>(BlockingCollection<T>[] collections, out T? item, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryTakeFromAnyApi);
        return IsValid(millisecondsTimeout)
            ? TryTakeFromAnyCore(collections, out item, millisecondsTimeout, isTake: false, TryTakeFromAnyApi, CancellationToken.None)
            : BlockingCollection<T>.TryTakeFromAny(collections, out item, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="BlockingCollection{T}.TryTakeFromAny(BlockingCollection{T}[], out T, int, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="collections">The candidate collections.</param>
    /// <param name="item">The item taken.</param>
    /// <param name="millisecondsTimeout">The time to wait for an item, which elapses in virtual time.</param>
    /// <param name="cancellationToken">The token that cancels the wait for an item.</param>
    /// <returns>The index of the collection the item was taken from, or -1.</returns>
    public static int TryTakeFromAny<T>(
        BlockingCollection<T>[] collections,
        out T? item,
        int millisecondsTimeout,
        CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryTakeFromAnyApi);
        return IsValid(millisecondsTimeout)
            ? TryTakeFromAnyCore(collections, out item, millisecondsTimeout, isTake: false, TryTakeFromAnyApi, cancellationToken)
            : BlockingCollection<T>.TryTakeFromAny(collections, out item, millisecondsTimeout, cancellationToken);
    }

    private static bool TryAddCore<T>(
        BlockingCollection<T> collection,
        T item,
        int millisecondsTimeout,
        string apiName,
        CancellationToken cancellationToken)
    {
        if (TryAddNow(collection, item, cancellationToken))
        {
            return true;
        }

        if (millisecondsTimeout == 0)
        {
            return false;
        }

        return Wait(
            () => CanAdd(collection),
            millisecondsTimeout,
            apiName,
            () =>
            {
                // CompleteAdding wakes a waiting adder, whose BCL wait then fails with this exception.
                if (collection.IsAddingCompleted)
                {
                    throw new InvalidOperationException(ConcurrentCompleteAddMessage);
                }

                return TryAddNow(collection, item, cancellationToken) ? true : null;
            },
            cancellationToken);
    }

    private static bool TryTakeCore<T>(
        BlockingCollection<T> collection,
        [MaybeNullWhen(false)] out T item,
        int millisecondsTimeout,
        string apiName,
        CancellationToken cancellationToken)
    {
        if (TryTakeNow(collection, out item, cancellationToken))
        {
            return true;
        }

        if (millisecondsTimeout == 0 || IsCompleted(collection))
        {
            return false;
        }

        T? taken = default;
        bool result = Wait(
            () => CanTake(collection),
            millisecondsTimeout,
            apiName,
            () => TryTakeNow(collection, out taken, cancellationToken) ? true : IsCompleted(collection) ? false : null,
            cancellationToken);
        item = taken!;
        return result;
    }

    private static IEnumerable<T> Consume<T>(BlockingCollection<T> collection, CancellationToken cancellationToken)
    {
        if (IsCompleted(collection))
        {
            yield break;
        }

        while (TryTakeCore(collection, out T? item, Timeout.Infinite, GetConsumingEnumerableApi, cancellationToken))
        {
            yield return item;
        }
    }

    private static int TryAddToAnyCore<T>(
        BlockingCollection<T>[] collections,
        T item,
        int millisecondsTimeout,
        string apiName,
        CancellationToken cancellationToken)
    {
        int index = TryAddToAnyNow(collections, item, cancellationToken);
        if (index >= 0 || millisecondsTimeout == 0)
        {
            return index;
        }

        int added = -1;
        bool result = Wait(
            () => CanAddToAny(collections),
            millisecondsTimeout,
            apiName,
            () => (added = TryAddToAnyNow(collections, item, cancellationToken)) >= 0 ? true : null,
            cancellationToken);
        return result ? added : -1;
    }

    private static int TryTakeFromAnyCore<T>(
        BlockingCollection<T>[] collections,
        out T? item,
        int millisecondsTimeout,
        bool isTake,
        string apiName,
        CancellationToken cancellationToken)
    {
        int index = TryTakeFromAnyNow(collections, out item, cancellationToken);
        if (index >= 0)
        {
            return index;
        }

        if (AreAllCompleted(collections))
        {
            // The BCL's TakeFromAny throws, without waiting, once every collection is completed.
            return isTake ? BlockingCollection<T>.TakeFromAny(collections, out item, cancellationToken) : -1;
        }

        if (millisecondsTimeout == 0)
        {
            return -1;
        }

        int taken = -1;
        T? takenItem = default;
        bool result = Wait(
            () => CanTakeFromAny(collections),
            millisecondsTimeout,
            apiName,
            () => (taken = TryTakeFromAnyNow(collections, out takenItem, cancellationToken)) >= 0 ? true
                : AreAllCompleted(collections) ? false
                : null,
            cancellationToken);
        item = takenItem;
        if (result)
        {
            return taken;
        }

        return isTake && AreAllCompleted(collections)
            ? BlockingCollection<T>.TakeFromAny(collections, out item, cancellationToken)
            : -1;
    }

    // Parks the caller on the coordinator until `ready` holds (or the token is cancelled, or the timeout elapses in
    // virtual time) and then makes `attempt`, until it returns a result. The token, observed after each wake, throws as
    // the BCL's waits do; an elapsed timeout returns false.
    private static bool Wait(
        Func<bool> ready,
        int millisecondsTimeout,
        string apiName,
        Func<bool?> attempt,
        CancellationToken cancellationToken)
    {
        ISimulationTimer? deadline = millisecondsTimeout == Timeout.Infinite
            ? null
            : SimulationTaskRuntime.RegisterTimeout(TimeSpan.FromMilliseconds(millisecondsTimeout), onElapsed: null, apiName);
        try
        {
            while (true)
            {
                SimulationTaskRuntime.DrainUntil(
                    () => ready() || cancellationToken.IsCancellationRequested || deadline?.IsElapsed == true,
                    apiName,
                    CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt() is { } result)
                {
                    return result;
                }

                if (deadline?.IsElapsed == true)
                {
                    return false;
                }
            }
        }
        finally
        {
            deadline?.Cancel();
        }
    }

    // The BCL member with a zero timeout never waits; it validates, checks disposal and the token, and adds.
    private static bool TryAddNow<T>(BlockingCollection<T> collection, T item, CancellationToken cancellationToken)
    {
        if (!collection.TryAdd(item, 0, cancellationToken))
        {
            return false;
        }

        RaceSynchronization.Signal(collection);
        return true;
    }

    private static bool TryTakeNow<T>(BlockingCollection<T> collection, [MaybeNullWhen(false)] out T item, CancellationToken cancellationToken)
    {
        if (!collection.TryTake(out item, 0, cancellationToken))
        {
            return false;
        }

        RaceSynchronization.Wait(collection);
        return true;
    }

    private static bool IsCompleted<T>(BlockingCollection<T> collection)
    {
        if (!collection.IsCompleted)
        {
            return false;
        }

        RaceSynchronization.Wait(collection);
        return true;
    }

    private static int TryAddToAnyNow<T>(BlockingCollection<T>[] collections, T item, CancellationToken cancellationToken)
    {
        int index = BlockingCollection<T>.TryAddToAny(collections, item, 0, cancellationToken);
        if (index >= 0)
        {
            RaceSynchronization.Signal(collections[index]);
        }

        return index;
    }

    private static int TryTakeFromAnyNow<T>(BlockingCollection<T>[] collections, out T? item, CancellationToken cancellationToken)
    {
        int index = BlockingCollection<T>.TryTakeFromAny(collections, out item, 0, cancellationToken);
        if (index >= 0)
        {
            RaceSynchronization.Wait(collections[index]);
        }

        return index;
    }

    private static bool AreAllCompleted<T>(BlockingCollection<T>[] collections)
    {
        foreach (BlockingCollection<T> collection in collections)
        {
            if (!collection.IsCompleted)
            {
                return false;
            }
        }

        foreach (BlockingCollection<T> collection in collections)
        {
            RaceSynchronization.Wait(collection);
        }

        return true;
    }

    // The readiness predicates read the collection's state; a collection disposed while a caller waits never becomes
    // ready, as the BCL's waiter stays blocked on its disposed semaphore.
    private static bool CanAdd<T>(BlockingCollection<T> collection)
    {
        try
        {
            return collection.IsAddingCompleted || collection.Count < collection.BoundedCapacity;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool CanTake<T>(BlockingCollection<T> collection)
    {
        try
        {
            return collection.Count > 0 || collection.IsAddingCompleted;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool CanAddToAny<T>(BlockingCollection<T>[] collections)
    {
        foreach (BlockingCollection<T> collection in collections)
        {
            if (CanAdd(collection))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanTakeFromAny<T>(BlockingCollection<T>[] collections)
    {
        bool allCompleted = true;
        foreach (BlockingCollection<T> collection in collections)
        {
            try
            {
                if (collection.Count > 0)
                {
                    return true;
                }

                allCompleted &= collection.IsCompleted;
            }
            catch (ObjectDisposedException)
            {
                allCompleted = false;
            }
        }

        return allCompleted;
    }

    // The BCL's ValidateMillisecondsTimeout and ValidateTimeout: an invalid timeout reaches the BCL member, which throws
    // before it waits.
    private static bool IsValid(int millisecondsTimeout) => millisecondsTimeout >= Timeout.Infinite;

    private static bool IsValid(TimeSpan timeout)
    {
        long totalMilliseconds = (long)timeout.TotalMilliseconds;
        return totalMilliseconds is >= 0 and <= int.MaxValue || totalMilliseconds == Timeout.Infinite;
    }
}
