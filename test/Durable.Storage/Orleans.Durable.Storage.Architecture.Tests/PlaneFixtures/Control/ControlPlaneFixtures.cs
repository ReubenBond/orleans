using Orleans.Durable.Storage.Architecture.Tests.PlaneFixtures.Hosting;

namespace Orleans.Durable.Storage.Architecture.Tests.PlaneFixtures.Control;

// The control plane of a fixture layer for PlaneSeparationTests.

/// <summary>A control-plane base class.</summary>
public abstract class ControlBase
{
    public abstract int Number { get; }
}

/// <summary>A control-plane interface, as a node control grain's contract is.</summary>
public interface IControlContract
{
    int Number { get; }
}

/// <summary>A control-plane value.</summary>
public sealed class ControlValue(int number)
{
    public static readonly ControlValue Shared = new(0);

    public int Number { get; } = number;
}

/// <summary>A generic control-plane type.</summary>
/// <typeparam name="T">The value it holds.</typeparam>
public sealed class ControlBox<T>
{
    public T? Value { get; set; }
}

/// <summary>A control-plane service: a concrete class, as the layers' control-plane types are.</summary>
public sealed class ControlService
{
    private int commits;

    public int Commit() => ++commits;
}

/// <summary>Control-plane operations.</summary>
public static class ControlOperations
{
    public static int Answer() => 42;

    public static T Echo<T>(T value) => value;
}

/// <summary>A control-plane attribute.</summary>
[AttributeUsage(AttributeTargets.All)]
public sealed class ControlMarkerAttribute : Attribute;

/// <summary>A control-plane enum, which an attribute argument may box.</summary>
public enum ControlLevel
{
    Low,
    High,
}

/// <summary>A control-plane value type, to which a pointer may point.</summary>
/// <param name="X">A coordinate.</param>
public readonly record struct ControlPoint(int X);

/// <summary>Control-plane event data.</summary>
public sealed class ControlEventArgs : EventArgs
{
    public int Number { get; init; }
}

/// <summary>A control-plane exception.</summary>
public sealed class ControlException : Exception
{
    public ControlException()
    {
    }

    public ControlException(string message)
        : base(message)
    {
    }

    public ControlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The control plane uses the data plane, which the check allows.</summary>
public static class ControlUsesDataPlane
{
    public static int Read(DataPlaneOnly value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Number;
    }
}

/// <summary>The control plane calls the hosting namespace, which the check forbids.</summary>
public static class ControlUsesHosting
{
    public static int Count() => FixtureWiring.Registrations;
}
