namespace Orleans.Testing.Simulation.Runtime.Tasks;

/// <summary>A cancellable registration for readiness-gated controlled work.</summary>
public interface ISimulationWorkRegistration
{
    /// <summary>Gets whether the registration has been canceled.</summary>
    bool IsCanceled { get; }

    /// <summary>Cancels the registration so its continuation can never become runnable.</summary>
    void Cancel();
}
