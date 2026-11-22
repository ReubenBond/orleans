using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// Static shims for <see cref="Mutex"/>. The returned unnamed <see cref="Mutex"/> is only an identity
/// object; ownership, recursion, and waiting are modelled by <see cref="ControlledWaitHandle"/> and never
/// use the kernel mutex. A logical strand which exits while owning a mutex does not simulate OS abandonment:
/// it leaves the mutex owned, so a subsequent waiter receives the normal controlled-deadlock diagnostic.
/// </summary>
public static class ControlledMutex
{
    private const string CtorApi = "System.Threading.Mutex..ctor";
    private const string NamedCtorApi = "System.Threading.Mutex..ctor(name)";
    private const string ReleaseApi = "System.Threading.Mutex.ReleaseMutex";
    private const string OpenExistingApi = "System.Threading.Mutex.OpenExisting";
    private const string TryOpenExistingApi = "System.Threading.Mutex.TryOpenExisting";

    /// <summary>Controlled <c>new Mutex()</c>.</summary>
    public static Mutex Create()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CtorApi);
        return CreateCore(initiallyOwned: false);
    }

    /// <summary>Controlled <c>new Mutex(bool)</c>.</summary>
    public static Mutex Create(bool initiallyOwned)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CtorApi);
        return CreateCore(initiallyOwned);
    }

    /// <summary>Controlled <c>new Mutex(string, NamedWaitHandleOptions)</c>. Non-null names are not supported.</summary>
    public static Mutex CreateNamed(string? name, NamedWaitHandleOptions options)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(NamedCtorApi);
        RejectIfNamed(name);
        return CreateCore(initiallyOwned: false);
    }

    /// <summary>Controlled <c>new Mutex(bool, string)</c>. Non-null names are not supported.</summary>
    public static Mutex CreateNamed(bool initiallyOwned, string? name)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(NamedCtorApi);
        RejectIfNamed(name);
        return CreateCore(initiallyOwned);
    }

    /// <summary>Controlled <c>new Mutex(bool, string, out bool)</c>. Non-null names are not supported.</summary>
    public static Mutex CreateNamed(bool initiallyOwned, string? name, out bool createdNew)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(NamedCtorApi);
        RejectIfNamed(name);
        createdNew = true;
        return CreateCore(initiallyOwned);
    }

    /// <summary>Controlled <c>new Mutex(bool, string, NamedWaitHandleOptions)</c>. Non-null names are not supported.</summary>
    public static Mutex CreateNamed(bool initiallyOwned, string? name, NamedWaitHandleOptions options)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(NamedCtorApi);
        RejectIfNamed(name);
        return CreateCore(initiallyOwned);
    }

    /// <summary>Controlled <c>new Mutex(bool, string, NamedWaitHandleOptions, out bool)</c>. Non-null names are not supported.</summary>
    public static Mutex CreateNamed(
        bool initiallyOwned,
        string? name,
        NamedWaitHandleOptions options,
        out bool createdNew)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(NamedCtorApi);
        RejectIfNamed(name);
        createdNew = true;
        return CreateCore(initiallyOwned);
    }

    /// <summary>Controlled <see cref="Mutex.ReleaseMutex"/>.</summary>
    public static void ReleaseMutex(Mutex instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReleaseApi);
        ArgumentNullException.ThrowIfNull(instance);
        ControlledWaitHandle.MutexStateForOperation(instance, ReleaseApi)
            .Release(SimulationSynchronizationFlow.CurrentId);
    }

    /// <summary>Rejected controlled <see cref="Mutex.OpenExisting(string)"/>.</summary>
    public static Mutex OpenExisting(string name)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(OpenExistingApi);
        throw NamedRejected(OpenExistingApi);
    }

    /// <summary>Rejected controlled <see cref="Mutex.OpenExisting(string, NamedWaitHandleOptions)"/>.</summary>
    public static Mutex OpenExisting(string name, NamedWaitHandleOptions options)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(OpenExistingApi);
        throw NamedRejected(OpenExistingApi);
    }

    /// <summary>Rejected controlled <see cref="Mutex.TryOpenExisting(string, out Mutex)"/>.</summary>
    public static bool TryOpenExisting(string name, out Mutex result)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryOpenExistingApi);
        throw NamedRejected(TryOpenExistingApi);
    }

    /// <summary>Rejected controlled <see cref="Mutex.TryOpenExisting(string, NamedWaitHandleOptions, out Mutex)"/>.</summary>
    public static bool TryOpenExisting(string name, NamedWaitHandleOptions options, out Mutex result)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryOpenExistingApi);
        throw NamedRejected(TryOpenExistingApi);
    }

    private static Mutex CreateCore(bool initiallyOwned)
    {
        // Construct only an unnamed, unowned identity object. Calling the BCL initial-ownership overload
        // would acquire a physical kernel mutex and escape the deterministic model.
        var instance = new Mutex(initiallyOwned: false);
        var state = new ControlledWaitHandle.MutexState();
        ControlledWaitHandle.Register(instance, state);
        if (initiallyOwned)
        {
            _ = state.TryAcquire(SimulationSynchronizationFlow.CurrentId);
        }

        return instance;
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
            "named / cross-process mutexes address a kernel object shared across processes, which a single " +
            "simulated process cannot model; only unnamed (in-process) mutexes are controlled.");
}
