namespace Orleans.Durable.Storage.Tests;

/// <summary>Orleans buffers as the replica calls' grain messages carry them (OR-I18), for tests to build and inspect.</summary>
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

    /// <summary>Returns a read's DATA answer of <paramref name="bytes"/> in a new Orleans buffer.</summary>
    public static RemoteReplicaRead.Data Data(params ReadOnlySpan<byte> bytes) => new(Of(bytes));

    /// <summary>
    /// Returns <paramref name="request"/> as Orleans' proxy sends it: with a pin of its own on the payload, which Orleans'
    /// <c>ArcBufferCopier</c> takes as the call is made, and which its owner releases (OR-I18).
    /// </summary>
    public static LedgerReplicaWriteRequest AsSent(LedgerReplicaWriteRequest request) => request with { Payload = request.Payload.Slice(0) };

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
}

/// <summary>
/// The Orleans buffers a test hands to Clog, each of which its receiver owns and must release on every path on which it
/// receives it (OR-I18): disposing the ledger, at a test's teardown, asserts that each was released.
/// </summary>
internal sealed class OrleansBufferLedger : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<OrleansBuffer> _tracked = [];

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
        lock (_lock)
        {
            tracked = [.. _tracked];
        }

        Assert.All(tracked, static buffer => Assert.True(WireBytes.IsReleased(buffer), $"An Orleans buffer of {buffer.Length} bytes which Clog received was never released (OR-I18)."));
    }
}
