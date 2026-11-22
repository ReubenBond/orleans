using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// <para>
/// The controlled stand-in for <see cref="global::System.Threading.Lock"/> (the .NET 9+ dedicated lock type).
/// The rewriter substitutes the type at every use site - <c>new Lock()</c> becomes
/// <c>new ControlledLock()</c>, and the C# <c>lock (lockObj)</c> statement (which the compiler lowers to
/// <c>Lock.Scope scope = lockObj.EnterScope(); try { ... } finally { scope.Dispose(); }</c>) is
/// redirected to <see cref="EnterScope"/> and <see cref="Scope.Dispose"/> on this type. The public
/// surface therefore mirrors <see cref="global::System.Threading.Lock"/> exactly, including the nested
/// <see cref="Scope"/> ref struct.
/// </para>
/// <para>
/// Inside a simulation the lock is modelled on the controlled monitor kernel
/// (<see cref="ControlledMonitor"/>) against a private key object, so acquisition, mutual exclusion,
/// reentrancy and deadlock detection all behave exactly as a controlled <c>Monitor</c>.
/// </para>
/// <para>
/// <see cref="global::System.Threading.Lock"/> exposes no OS-only members, so the whole surface is controlled;
/// there is nothing to reject.
/// </para>
/// </summary>
public sealed class ControlledLock
{
    // The controlled-monitor key used inside a simulation. A dedicated private object keeps the lock's
    // identity distinct from the ControlledLock instance itself.
    private readonly object _key = new();

    /// <summary>Initializes a new controlled lock, mirroring <c>new System.Threading.Lock()</c>.</summary>
    public ControlledLock()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Lock..ctor");
    }

    /// <summary>Gets a value indicating whether the current strand holds the lock.</summary>
    public bool IsHeldByCurrentThread
    {
        get
        {
            SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Lock.get_IsHeldByCurrentThread");
            return ControlledMonitor.IsEntered(_key);
        }
    }

    /// <summary>Controlled <see cref="global::System.Threading.Lock.Enter()"/>.</summary>
    public void Enter()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Lock.Enter");
        ControlledMonitor.Enter(_key);
    }

    /// <summary>Controlled <see cref="global::System.Threading.Lock.Exit()"/>.</summary>
    public void Exit()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Lock.Exit");
        ControlledMonitor.Exit(_key);
    }

    /// <summary>
    /// Controlled <see cref="global::System.Threading.Lock.EnterScope()"/>: acquires the lock and returns a
    /// <see cref="Scope"/> whose disposal releases it (the target of the C# <c>lock</c> lowering).
    /// </summary>
    /// <returns>A scope that releases the lock when disposed.</returns>
    public Scope EnterScope()
    {
        Enter();
        return new Scope(this);
    }

    /// <summary>Controlled <see cref="global::System.Threading.Lock.TryEnter()"/>: a non-blocking acquire attempt.</summary>
    /// <returns><see langword="true"/> if the lock was acquired.</returns>
    public bool TryEnter()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Lock.TryEnter");
        return ControlledMonitor.TryEnter(_key);
    }

    /// <summary>Controlled <see cref="global::System.Threading.Lock.TryEnter(int)"/>.</summary>
    /// <param name="millisecondsTimeout">Zero for a non-blocking try; -1 or a finite positive value to block. Finite positive timeouts are modelled as infinite inside a simulation (virtual-time timeouts are virtual-time control).</param>
    /// <returns><see langword="true"/> if the lock was acquired.</returns>
    public bool TryEnter(int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Lock.TryEnter");
        return ControlledMonitor.TryEnter(_key, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="global::System.Threading.Lock.TryEnter(TimeSpan)"/>.</summary>
    /// <param name="timeout">The timeout, interpreted as for <see cref="TryEnter(int)"/>.</param>
    /// <returns><see langword="true"/> if the lock was acquired.</returns>
    public bool TryEnter(TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Lock.TryEnter");
        return ControlledMonitor.TryEnter(_key, timeout);
    }

    /// <summary>
    /// The controlled analogue of <see cref="global::System.Threading.Lock.Scope"/>: a ref struct returned by
    /// <see cref="EnterScope"/> whose <see cref="Dispose"/> releases the lock exactly once, matching the
    /// C# <c>lock</c> statement's <c>try/finally</c> lowering.
    /// </summary>
    public ref struct Scope
    {
        private ControlledLock? _owner;

        internal Scope(ControlledLock owner)
        {
            _owner = owner;
        }

        /// <summary>Releases the lock acquired by <see cref="EnterScope"/>. Idempotent.</summary>
        public void Dispose()
        {
            SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Lock.Scope.Dispose");
            var owner = _owner;
            _owner = null;
            owner?.Exit();
        }
    }
}
