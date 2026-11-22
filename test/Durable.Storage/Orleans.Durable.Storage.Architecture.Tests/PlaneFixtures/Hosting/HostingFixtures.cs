using Orleans.Durable.Storage.Architecture.Tests.PlaneFixtures.Control;

namespace Orleans.Durable.Storage.Architecture.Tests.PlaneFixtures.Hosting;

// The hosting namespace of a fixture layer for PlaneSeparationTests: wiring which names both planes, and which neither
// plane names.

/// <summary>Wiring which registers a service of each plane.</summary>
public static class FixtureWiring
{
    public static int Registrations { get; private set; }

    public static void Register(ControlService control, DataPlaneOnly data)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(data);
        Registrations++;
    }
}

/// <summary>
/// Generic wiring: another type which names one of its members names it through a member reference to an
/// instantiation, whose signature names the control plane.
/// </summary>
/// <typeparam name="TService">The service it wires.</typeparam>
public sealed class FixtureWiring<TService>
{
    public ControlValue? Control;

    public TService? Service { get; set; }

    public bool Accepts(ControlValue? value) => value is null;
}
