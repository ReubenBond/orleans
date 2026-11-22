using System;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// <para>
/// Static shims for the <see cref="EventWaitHandle"/> / <see cref="AutoResetEvent"/> /
/// <see cref="ManualResetEvent"/> surface. These are (or derive from) <see cref="EventWaitHandle"/>; the
/// inherited <see cref="WaitHandle.WaitOne()"/>, <c>Dispose</c>, and OS-handle members are handled by
/// <see cref="ControlledWaitHandle"/>. Here we control the constructors (redirected to <c>Create*</c>
/// factories) and the event-specific <see cref="EventWaitHandle.Set"/> / <see cref="EventWaitHandle.Reset"/>.
/// </para>
/// <para>
/// The controlled object <em>is</em> a real event used purely as an identity handle; its modelled
/// signalled state, reset mode, and waiter queue live in the shared weak-keyed registry owned by
/// <see cref="ControlledWaitHandle"/>. Inside a simulation <see cref="EventWaitHandle.Set"/> signals the
/// event and releases waiters per its reset mode - a manual-reset event releases <em>every</em> waiter and
/// stays set; an auto-reset event releases exactly <em>one</em> eligible waiter and is consumed by it -
/// and <see cref="EventWaitHandle.Reset"/> clears the signal. No signal ever touches a kernel object or
/// runs on a physical thread.
/// </para>
/// <para>
/// Named / cross-process events (a non-null <c>name</c>, the <see cref="EventWaitHandle.OpenExisting(string)"/>
/// / <see cref="EventWaitHandle.TryOpenExisting(string, out EventWaitHandle)"/> APIs, and the
/// <see cref="NamedWaitHandleOptions"/> overloads) address a kernel object shared across processes, which a
/// single simulated process cannot model; they are rejected precisely
/// (<see cref="SimulationApiException"/>). A null name is the degenerate unnamed case and
/// is controlled. Adapted from Microsoft Coyote (MIT).
/// </para>
/// </summary>
public static class ControlledEventWaitHandle
{
    private const string SetApi = "System.Threading.EventWaitHandle.Set";
    private const string ResetApi = "System.Threading.EventWaitHandle.Reset";
    private const string CtorApi = "System.Threading.EventWaitHandle..ctor(name)";
    private const string OpenExistingApi = "System.Threading.EventWaitHandle.OpenExisting";
    private const string TryOpenExistingApi = "System.Threading.EventWaitHandle.TryOpenExisting";

    // ---- constructors ----

    /// <summary>Controlled <c>new AutoResetEvent(bool)</c>.</summary>
    /// <param name="initialState"><see langword="true"/> to create the event signalled.</param>
    /// <returns>A real auto-reset event used as the controlled identity handle.</returns>
    public static AutoResetEvent CreateAutoResetEvent(bool initialState)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.AutoResetEvent..ctor");
        var instance = new AutoResetEvent(initialState);
        ControlledWaitHandle.Register(instance, new ControlledWaitHandle.EventState(EventResetMode.AutoReset, initialState));
        return instance;
    }

    /// <summary>Controlled <c>new ManualResetEvent(bool)</c>.</summary>
    /// <param name="initialState"><see langword="true"/> to create the event signalled.</param>
    /// <returns>A real manual-reset event used as the controlled identity handle.</returns>
    public static ManualResetEvent CreateManualResetEvent(bool initialState)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.ManualResetEvent..ctor");
        var instance = new ManualResetEvent(initialState);
        ControlledWaitHandle.Register(instance, new ControlledWaitHandle.EventState(EventResetMode.ManualReset, initialState));
        return instance;
    }

    /// <summary>Controlled <c>new EventWaitHandle(bool, EventResetMode)</c>.</summary>
    /// <param name="initialState"><see langword="true"/> to create the event signalled.</param>
    /// <param name="mode">The reset mode.</param>
    /// <returns>A real event used as the controlled identity handle.</returns>
    public static EventWaitHandle CreateEvent(bool initialState, EventResetMode mode)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.EventWaitHandle..ctor");
        var instance = new EventWaitHandle(initialState, mode);
        ControlledWaitHandle.Register(instance, new ControlledWaitHandle.EventState(mode, initialState));
        return instance;
    }

    /// <summary>Controlled <c>new EventWaitHandle(bool, EventResetMode, string)</c>. A non-null name is rejected.</summary>
    /// <param name="initialState"><see langword="true"/> to create the event signalled.</param>
    /// <param name="mode">The reset mode.</param>
    /// <param name="name">The system-wide name, or <see langword="null"/> for an unnamed event.</param>
    /// <returns>A real event used as the controlled identity handle.</returns>
    public static EventWaitHandle CreateNamedEvent(bool initialState, EventResetMode mode, string? name)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CtorApi);
        RejectIfNamed(name);
        return CreateEvent(initialState, mode);
    }

    /// <summary>Controlled <c>new EventWaitHandle(bool, EventResetMode, string, out bool)</c>. A non-null name is rejected.</summary>
    /// <param name="initialState"><see langword="true"/> to create the event signalled.</param>
    /// <param name="mode">The reset mode.</param>
    /// <param name="name">The system-wide name, or <see langword="null"/> for an unnamed event.</param>
    /// <param name="createdNew"><see langword="true"/> when a new event was created (always true for an unnamed event).</param>
    /// <returns>A real event used as the controlled identity handle.</returns>
    public static EventWaitHandle CreateNamedEvent(bool initialState, EventResetMode mode, string? name, out bool createdNew)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CtorApi);
        RejectIfNamed(name);
        createdNew = true;
        return CreateEvent(initialState, mode);
    }

    /// <summary>Controlled <c>new EventWaitHandle(bool, EventResetMode, string, NamedWaitHandleOptions)</c>. A non-null name is rejected.</summary>
    /// <param name="initialState"><see langword="true"/> to create the event signalled.</param>
    /// <param name="mode">The reset mode.</param>
    /// <param name="name">The system-wide name, or <see langword="null"/> for an unnamed event.</param>
    /// <param name="options">The named-handle options.</param>
    /// <returns>A real event used as the controlled identity handle.</returns>
    public static EventWaitHandle CreateNamedEvent(bool initialState, EventResetMode mode, string? name, NamedWaitHandleOptions options)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CtorApi);
        RejectIfNamed(name);
        return CreateEvent(initialState, mode);
    }

    /// <summary>Controlled <c>new EventWaitHandle(bool, EventResetMode, string, NamedWaitHandleOptions, out bool)</c>. A non-null name is rejected.</summary>
    /// <param name="initialState"><see langword="true"/> to create the event signalled.</param>
    /// <param name="mode">The reset mode.</param>
    /// <param name="name">The system-wide name, or <see langword="null"/> for an unnamed event.</param>
    /// <param name="options">The named-handle options.</param>
    /// <param name="createdNew"><see langword="true"/> when a new event was created (always true for an unnamed event).</param>
    /// <returns>A real event used as the controlled identity handle.</returns>
    public static EventWaitHandle CreateNamedEvent(bool initialState, EventResetMode mode, string? name, NamedWaitHandleOptions options, out bool createdNew)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CtorApi);
        RejectIfNamed(name);
        createdNew = true;
        return CreateEvent(initialState, mode);
    }

    // ---- Set / Reset ----

    /// <summary>Controlled <see cref="EventWaitHandle.Set"/>.</summary>
    /// <param name="instance">The receiving event.</param>
    /// <returns>Always <see langword="true"/>, as for the real event.</returns>
    public static bool Set(EventWaitHandle instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SetApi);
        ArgumentNullException.ThrowIfNull(instance);
        ControlledWaitHandle.EventState state = ControlledWaitHandle.StateForOperation(instance, SetApi);
        state.Signaled = true;
        ControlledWaitHandle.ReleaseWaiters(state);
        return true;
    }

    /// <summary>Controlled <see cref="EventWaitHandle.Reset"/>.</summary>
    /// <param name="instance">The receiving event.</param>
    /// <returns>Always <see langword="true"/>, as for the real event.</returns>
    public static bool Reset(EventWaitHandle instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ResetApi);
        ArgumentNullException.ThrowIfNull(instance);
        ControlledWaitHandle.EventState state = ControlledWaitHandle.StateForOperation(instance, ResetApi);
        state.Signaled = false;
        return true;
    }

    // ---- named / cross-process APIs: rejected precisely ----

    /// <summary>Rejected controlled <see cref="EventWaitHandle.OpenExisting(string)"/>.</summary>
    /// <param name="name">The system-wide event name.</param>
    /// <returns>Never returns inside a simulation; throws instead.</returns>
    public static EventWaitHandle OpenExisting(string name)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(OpenExistingApi);
        throw NamedRejected(OpenExistingApi);
    }

    /// <summary>Rejected controlled <see cref="EventWaitHandle.OpenExisting(string, NamedWaitHandleOptions)"/>.</summary>
    /// <param name="name">The system-wide event name.</param>
    /// <param name="options">The named-handle options.</param>
    /// <returns>Never returns inside a simulation; throws instead.</returns>
    public static EventWaitHandle OpenExisting(string name, NamedWaitHandleOptions options)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(OpenExistingApi);
        throw NamedRejected(OpenExistingApi);
    }

    /// <summary>Rejected controlled <see cref="EventWaitHandle.TryOpenExisting(string, out EventWaitHandle)"/>.</summary>
    /// <param name="name">The system-wide event name.</param>
    /// <param name="result">The opened event, on success.</param>
    /// <returns>Never returns inside a simulation; throws instead.</returns>
    public static bool TryOpenExisting(string name, out EventWaitHandle result)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryOpenExistingApi);
        throw NamedRejected(TryOpenExistingApi);
    }

    /// <summary>Rejected controlled <see cref="EventWaitHandle.TryOpenExisting(string, NamedWaitHandleOptions, out EventWaitHandle)"/>.</summary>
    /// <param name="name">The system-wide event name.</param>
    /// <param name="options">The named-handle options.</param>
    /// <param name="result">The opened event, on success.</param>
    /// <returns>Never returns inside a simulation; throws instead.</returns>
    public static bool TryOpenExisting(string name, NamedWaitHandleOptions options, out EventWaitHandle result)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryOpenExistingApi);
        throw NamedRejected(TryOpenExistingApi);
    }

    private static void RejectIfNamed(string? name)
    {
        if (name is not null)
        {
            throw NamedRejected(CtorApi);
        }
    }

    private static SimulationApiException NamedRejected(string api) =>
        new(
            SimulationApiCategory.WaitHandle,
            api,
            "named / cross-process events address a kernel object shared across processes, which a single " +
            "simulated process cannot model; only unnamed (in-process) events are controlled.");
}
