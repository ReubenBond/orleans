using CsCheck;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Registers <see cref="ObjectStoreSpec"/>'s operations with a conformance kit.
/// </summary>
internal static class ObjectStoreKit
{
    /// <summary>
    /// Registers the spec's operations, drawing requests over its four plain names and, with <paramref name="appends"/>,
    /// its appendable names; unless <paramref name="values"/> names others, sixteen values, and empty creates; reads of up
    /// to 256 bytes unless <paramref name="lengths"/> gives theirs; and listings unless <paramref name="listings"/> is unset;
    /// <paramref name="run"/> executes each request.
    /// </summary>
    /// <returns>The kit.</returns>
    internal static ConformanceKit<ObjectStoreState, TSystem> ObjectStoreOperations<TSystem>(
        this ConformanceKit<ObjectStoreState, TSystem> kit,
        Func<TSystem, object, Task<ObjectOutcome>> run,
        bool listings = true,
        Gen<byte>? values = null,
        Gen<int>? lengths = null,
        bool appends = false)
        where TSystem : IAsyncDisposable
    {
        var plain = Gen.Int[0, ObjectStoreSpec.PlainNames - 1];
        var created = Gen.Int[1, ObjectStoreSpec.PlainNames - 1];
        var replaced = Gen.Const(ObjectStoreSpec.ReplacedName);
        var appendable = Gen.Int[ObjectStoreSpec.PlainNames, ObjectStoreSpec.Names.Count - 1];
        var names = appends ? Gen.Int[0, ObjectStoreSpec.Names.Count - 1] : plain;
        var readable = appends ? Gen.Int[1, ObjectStoreSpec.Names.Count - 1] : created;
        values ??= Gen.Byte[1, 16];
        lengths ??= Gen.Int[1, 256];
        var appendWeight = appends ? 2 : 0;
        Task<ObjectOutcome> Run<TRequest>(TSystem system, TRequest request)
            where TRequest : notnull =>
            run(system, request);

        return kit
            .Operation(
                ObjectStoreSpec.CreateOperation,
                Gen.Select(created, Gen.Frequency((1, Gen.Const((byte)0)), (8, values)), static (name, value) => new ObjectCreate(name, value)),
                Run,
                weight: 3)
            .Operation(
                ObjectStoreSpec.ReplaceOperation,
                Gen.Select(replaced, Gen.Frequency((1, Gen.Const((byte?)null)), (16, values.Select(static value => (byte?)value))), values, static (name, expected, value) => new ObjectReplace(name, expected, value)),
                Run,
                weight: 3)
            .Operation(
                ObjectStoreSpec.ReadOperation,
                readable.SelectMany(name => ObjectStoreSpec.IsAppendable(name)
                    ? Gen.Select(Gen.Int[0, 4 * ObjectStoreSpec.ChunkLength], Gen.Int[1, 2 * ObjectStoreSpec.ChunkLength], (offset, length) => new ObjectRead(name, offset, length))
                    : Gen.Select(Gen.Int[0, 1100], lengths, (offset, length) => new ObjectRead(name, offset, length))),
                Run,
                weight: 2)
            .Operation(ObjectStoreSpec.ReadVersionedOperation, plain.Select(static name => new ObjectReadVersioned(name)), Run, weight: 2)
            .Operation(ObjectStoreSpec.PropertiesOperation, readable.Select(static name => new ObjectGetProperties(name)), Run, weight: 2)
            .Operation(ObjectStoreSpec.DeleteOperation, names.Select(static name => new ObjectDelete(name)), Run)
            .Operation(
                ObjectStoreSpec.ListOperation,
                Gen.Select(Gen.Int[0, ObjectStoreSpec.Prefixes.Count - 1], Gen.Frequency((2, Gen.Const(0)), (1, Gen.Int[1, ObjectStoreSpec.Starts.Count - 1])), static (prefix, start) => new ObjectList(prefix, start)),
                Run,
                weight: listings ? 1 : 0)
            .Operation(ObjectStoreSpec.CreateAppendableOperation, appendable.Select(static name => new ObjectCreateAppendable(name)), Run, weight: appendWeight)
            .Operation(
                ObjectStoreSpec.AppendOperation,
                Gen.Select(appendable, Gen.Int[0, 3], Gen.Byte[1, 16], static (name, appended, value) => new ObjectAppend(name, appended, value)),
                Run,
                weight: appends ? 4 : 0)
            .Operation(ObjectStoreSpec.SealOperation, appendable.Select(static name => new ObjectSeal(name)), Run, weight: appends ? 1 : 0)
            .Operation(ObjectStoreSpec.TombstoneOperation, appendable.Select(static name => new ObjectTombstone(name)), Run, weight: appends ? 1 : 0);
    }
}
