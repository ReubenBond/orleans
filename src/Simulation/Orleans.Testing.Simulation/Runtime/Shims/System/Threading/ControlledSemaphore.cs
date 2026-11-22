using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// Static shims for the kernel <see cref="Semaphore"/> surface. The returned unnamed semaphore is an
/// identity handle only: its count, maximum count, and FIFO waiter queue are modelled by
/// <see cref="ControlledWaitHandle"/> and never use the kernel semaphore's wait or release operations.
/// </summary>
public static class ControlledSemaphore
{
    private const string CtorApi = "System.Threading.Semaphore..ctor";
    private const string NamedCtorApi = "System.Threading.Semaphore..ctor(name)";
    private const string ReleaseApi = "System.Threading.Semaphore.Release";
    private const string OpenExistingApi = "System.Threading.Semaphore.OpenExisting";
    private const string TryOpenExistingApi = "System.Threading.Semaphore.TryOpenExisting";

    /// <summary>Controlled <c>new Semaphore(int, int)</c>.</summary>
    public static Semaphore Create(int initialCount, int maximumCount)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CtorApi);
        return CreateCore(initialCount, maximumCount);
    }

    /// <summary>Controlled <c>new Semaphore(int, int, string)</c>. Non-null names are not supported.</summary>
    public static Semaphore CreateNamed(int initialCount, int maximumCount, string? name)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(NamedCtorApi);
        ValidateCounts(initialCount, maximumCount);
        RejectIfNamed(name);
        return CreateCore(initialCount, maximumCount);
    }

    /// <summary>Controlled <c>new Semaphore(int, int, string, out bool)</c>. Non-null names are not supported.</summary>
    public static Semaphore CreateNamed(int initialCount, int maximumCount, string? name, out bool createdNew)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(NamedCtorApi);
        ValidateCounts(initialCount, maximumCount);
        RejectIfNamed(name);
        createdNew = true;
        return CreateCore(initialCount, maximumCount);
    }

    /// <summary>Controlled <c>new Semaphore(int, int, string, NamedWaitHandleOptions)</c>. Non-null names are not supported.</summary>
    public static Semaphore CreateNamed(int initialCount, int maximumCount, string? name, NamedWaitHandleOptions options)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(NamedCtorApi);
        ValidateCounts(initialCount, maximumCount);
        RejectIfNamed(name);
        return CreateCore(initialCount, maximumCount);
    }

    /// <summary>Controlled <c>new Semaphore(int, int, string, NamedWaitHandleOptions, out bool)</c>. Non-null names are not supported.</summary>
    public static Semaphore CreateNamed(
        int initialCount,
        int maximumCount,
        string? name,
        NamedWaitHandleOptions options,
        out bool createdNew)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(NamedCtorApi);
        ValidateCounts(initialCount, maximumCount);
        RejectIfNamed(name);
        createdNew = true;
        return CreateCore(initialCount, maximumCount);
    }

    /// <summary>Controlled <see cref="Semaphore.Release()"/>.</summary>
    public static int Release(Semaphore instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReleaseApi);
        return ReleaseCore(instance, 1);
    }

    /// <summary>Controlled <see cref="Semaphore.Release(int)"/>.</summary>
    public static int Release(Semaphore instance, int releaseCount)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReleaseApi);
        return ReleaseCore(instance, releaseCount);
    }

    /// <summary>Rejected controlled <see cref="Semaphore.OpenExisting(string)"/>.</summary>
    public static Semaphore OpenExisting(string name)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(OpenExistingApi);
        throw NamedRejected(OpenExistingApi);
    }

    /// <summary>Rejected controlled <see cref="Semaphore.OpenExisting(string, NamedWaitHandleOptions)"/>.</summary>
    public static Semaphore OpenExisting(string name, NamedWaitHandleOptions options)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(OpenExistingApi);
        throw NamedRejected(OpenExistingApi);
    }

    /// <summary>Rejected controlled <see cref="Semaphore.TryOpenExisting(string, out Semaphore)"/>.</summary>
    public static bool TryOpenExisting(string name, out Semaphore result)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryOpenExistingApi);
        throw NamedRejected(TryOpenExistingApi);
    }

    /// <summary>Rejected controlled <see cref="Semaphore.TryOpenExisting(string, NamedWaitHandleOptions, out Semaphore)"/>.</summary>
    public static bool TryOpenExisting(string name, NamedWaitHandleOptions options, out Semaphore result)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryOpenExistingApi);
        throw NamedRejected(TryOpenExistingApi);
    }

    private static Semaphore CreateCore(int initialCount, int maximumCount)
    {
        ValidateCounts(initialCount, maximumCount);

        // This is an unnamed identity object. No controlled operation observes or mutates its kernel count.
        var instance = new Semaphore(initialCount, maximumCount);
        ControlledWaitHandle.Register(instance, new ControlledWaitHandle.SemaphoreState(initialCount, maximumCount));
        return instance;
    }

    private static int ReleaseCore(Semaphore instance, int releaseCount)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (releaseCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(releaseCount), releaseCount, "The release count must be greater than zero.");
        }

        return ControlledWaitHandle.SemaphoreStateForOperation(instance, ReleaseApi).Release(releaseCount);
    }

    private static void ValidateCounts(int initialCount, int maximumCount)
    {
        if (initialCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCount), initialCount, "The initial count must be non-negative.");
        }

        if (maximumCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount), maximumCount, "The maximum count must be greater than zero.");
        }

        if (initialCount > maximumCount)
        {
            throw new ArgumentException(
                "The initial count for the semaphore must be greater than or equal to zero and less than the maximum count.");
        }
    }

    private static void RejectIfNamed(string? name)
    {
        if (name is not null)
        {
            throw NamedRejected(NamedCtorApi);
        }
    }

    private static SimulationApiException NamedRejected(string api) =>
        new(
            SimulationApiCategory.WaitHandle,
            api,
            "named / cross-process semaphores address a kernel object shared across processes, which a single " +
            "simulated process cannot model; only unnamed (in-process) semaphores are controlled.");
}
