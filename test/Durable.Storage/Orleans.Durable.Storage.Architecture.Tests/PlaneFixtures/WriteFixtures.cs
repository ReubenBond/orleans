using System.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.Architecture.Tests.PlaneFixtures;

// Data-plane fixtures for PlaneSeparationTests' checks of the control plane's writes: interfaces shaped as the
// MetaStore's register and the LSM's object store, implementations of each, and a caller of each member.

/// <summary>A register, as the MetaStore's is; generic, so a call of its members names an instantiation.</summary>
/// <typeparam name="TValue">The register's value.</typeparam>
public interface IFixtureRegister<TValue>
{
    ValueTask<TValue?> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> WriteAsync(TValue value, CancellationToken cancellationToken = default);
}

/// <summary>An object store, as the LSM's is.</summary>
public interface IFixtureObjectStore
{
    ValueTask<bool> CreateAsync(string name, CancellationToken cancellationToken = default);

    ValueTask<int?> ReadAsync(string name, CancellationToken cancellationToken = default);

    ValueTask<bool> ReplaceAsync(string name, int expectedVersion, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>Writes a register through an instantiation of its generic interface.</summary>
public static class WritesRegister
{
    public static ValueTask<bool> WriteAsync(IFixtureRegister<int> register, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(register);
        return register.WriteAsync(1, cancellationToken);
    }
}

/// <summary>Only reads a register.</summary>
public static class ReadsRegister
{
    public static ValueTask<int> ReadAsync(IFixtureRegister<int> register, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(register);
        return register.ReadAsync(cancellationToken);
    }
}

/// <summary>Replaces an object through an interface of this assembly.</summary>
public static class ReplacesObject
{
    public static ValueTask<bool> ReplaceAsync(IFixtureObjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.ReplaceAsync("manifest", expectedVersion: 1, cancellationToken);
    }
}

/// <summary>Deletes an object.</summary>
public static class DeletesObject
{
    public static ValueTask DeleteAsync(IFixtureObjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.DeleteAsync("run", cancellationToken);
    }
}

/// <summary>Creates and reads objects, as the request path may.</summary>
public static class CreatesAndReadsObjects
{
    public static ValueTask<bool> CreateAsync(IFixtureObjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.CreateAsync("data", cancellationToken);
    }

    public static ValueTask<int?> ReadAsync(IFixtureObjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.ReadAsync("data", cancellationToken);
    }
}

/// <summary>
/// An object store implementation, as the LSM's memory store is: a call through it names this type's members, which
/// implicitly implement the interface's.
/// </summary>
public sealed class ConcreteFixtureObjectStore : IFixtureObjectStore
{
    public ValueTask<bool> CreateAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public ValueTask<int?> ReadAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult<int?>(null);

    public ValueTask<bool> ReplaceAsync(string name, int expectedVersion, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(true);

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>
/// A register implementation whose write explicitly implements an instantiation of the generic interface: a method
/// implementation names the write, through a member reference to the instantiation.
/// </summary>
public sealed class ExplicitFixtureRegister : IFixtureRegister<int>
{
    public ValueTask<int> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(0);

    ValueTask<bool> IFixtureRegister<int>.WriteAsync(int value, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}

/// <summary>Replaces, creates, and reads through the concrete store; only the replace is a write.</summary>
public static class CallsConcreteStore
{
    public static ValueTask<bool> ReplaceAsync(ConcreteFixtureObjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.ReplaceAsync("manifest", expectedVersion: 1, cancellationToken);
    }

    public static ValueTask<bool> CreateAsync(ConcreteFixtureObjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.CreateAsync("data", cancellationToken);
    }

    public static ValueTask<int?> ReadAsync(ConcreteFixtureObjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.ReadAsync("data", cancellationToken);
    }
}

/// <summary>Writes the MetaStore's register: a member reference to an interface of another assembly.</summary>
public static class WritesMetaStoreRegister
{
    public static ValueTask<RegisterWriteOutcome> WriteAsync(IConditionalRegister register, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(register);
        return register.WriteAsync(expectedVersion: null, ReadOnlySequence<byte>.Empty, cancellationToken);
    }
}

/// <summary>
/// Writes the MetaStore's memory register through its concrete type: a member reference to another assembly's
/// implementation of the interface.
/// </summary>
public static class WritesInMemoryRegister
{
    public static ValueTask<RegisterWriteOutcome> WriteAsync(InMemoryConditionalRegister register, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(register);
        return register.WriteAsync(expectedVersion: null, ReadOnlySequence<byte>.Empty, cancellationToken);
    }
}

/// <summary>Only reads the MetaStore's register.</summary>
public static class ReadsMetaStoreRegister
{
    public static ValueTask<MetaStoreEntry?> ReadAsync(IConditionalRegister register, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(register);
        return register.ReadAsync(cancellationToken);
    }
}
