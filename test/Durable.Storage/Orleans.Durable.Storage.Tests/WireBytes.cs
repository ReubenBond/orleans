using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Tests;

/// <summary>Orleans and wire buffers as the grain messages carry them (OR-I18), for tests to build and inspect.</summary>
internal static class WireBytes
{
    /// <summary>Returns a new Orleans buffer of <paramref name="bytes"/>, its only owner; the empty buffer when there are none.</summary>
    public static OrleansBuffer Of(params ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return default;
        }

        using var writer = new OrleansBufferWriter();
        writer.Write(bytes);
        return writer.ConsumeSlice(bytes.Length);
    }

    /// <summary>Returns a new zero-copy <see cref="WireArcBuffer"/> over Durable Storage's pool holding <paramref name="bytes"/>.</summary>
    public static WireArcBuffer Arc(params ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty ? WireArcBuffer.Empty : WireArcBuffer.Take(ArcBuffer.CopyFrom(bytes));

    /// <summary>Returns a read's DATA answer of <paramref name="bytes"/> in a new <see cref="WireArcBuffer"/>.</summary>
    public static RemoteReplicaRead.Data Data(params ReadOnlySpan<byte> bytes) => new(Arc(bytes));

    /// <summary>
    /// Returns <paramref name="request"/> as Orleans' proxy sends it: with an independent zero-copy lease on the payload,
    /// which <see cref="WireArcBufferCopier"/> retains as the call is made (OR-I18).
    /// </summary>
    public static LedgerReplicaWriteRequest AsSent(LedgerReplicaWriteRequest request) =>
        request with { Payload = (request.Payload ?? WireArcBuffer.Empty).RetainCopy() };

    /// <summary>
    /// Returns whether the pages of <paramref name="buffer"/>, a copy of a buffer's only owner taken before its release,
    /// were released: reading them through it then fails, since a released page's version moves on. A buffer variable
    /// which was itself disposed always reads as released, so callers pass a copy.
    /// </summary>
    public static bool IsReleased(OrleansBuffer buffer)
    {
        try
        {
            _ = buffer.ToArray();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Returns whether <paramref name="buffer"/> has been consumed or disposed.</summary>
    public static bool IsReleased(WireArcBuffer? buffer) => buffer is null || buffer.IsConsumed;
}

/// <summary>
/// The buffers a test hands to Clog, each of which its receiver owns and must release on every path on which it
/// receives it (OR-I18): disposing the ledger, at a test's teardown, asserts that each was released.
/// </summary>
internal sealed class OrleansBufferLedger : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<OrleansBuffer> _tracked = [];
    private readonly List<WireArcBuffer> _trackedWire = [];

    /// <summary>Records <paramref name="buffer"/>, whose receiver is to release it, and returns it.</summary>
    public OrleansBuffer Track(OrleansBuffer buffer)
    {
        if (!buffer.IsEmpty)
        {
            lock (_lock)
            {
                _tracked.Add(buffer);
            }
        }

        return buffer;
    }

    /// <summary>Records <paramref name="buffer"/>, whose receiver is to consume or release it, and returns it.</summary>
    public WireArcBuffer Track(WireArcBuffer buffer)
    {
        if (!buffer.IsEmpty)
        {
            lock (_lock)
            {
                _trackedWire.Add(buffer);
            }
        }

        return buffer;
    }

    /// <summary>Records the bytes of <paramref name="data"/>, whose receiver is to release them, and returns it.</summary>
    public RemoteReplicaRead.Data Track(RemoteReplicaRead.Data data)
    {
        _ = Track(data.Bytes);
        return data;
    }

    /// <summary>Records the payload of <paramref name="request"/>, whose receiver is to release it, and returns it.</summary>
    public LedgerReplicaWriteRequest Track(LedgerReplicaWriteRequest request)
    {
        _ = Track(request.Payload);
        return request;
    }

    /// <summary>Asserts that every recorded buffer was released.</summary>
    public void Dispose()
    {
        OrleansBuffer[] tracked;
        WireArcBuffer[] trackedWire;
        lock (_lock)
        {
            tracked = [.. _tracked];
            trackedWire = [.. _trackedWire];
        }

        Assert.All(tracked, static buffer => Assert.True(WireBytes.IsReleased(buffer), $"An Orleans buffer of {buffer.Length} bytes which Clog received was never released (OR-I18)."));
        Assert.All(trackedWire, static buffer => Assert.True(WireBytes.IsReleased(buffer), $"A WireArcBuffer of {buffer.Length} bytes which Clog received was never released (OR-I18)."));
    }
}
