using Orleans.Consensus.CasPaxos.Server;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

/// <summary>
/// Scripts the adapter boundary only; distributed behavior belongs to Orleans.Consensus.CasPaxos.Testing.
/// </summary>
internal sealed class RegisterServiceStub : IRegisterService, IDisposable
{
    internal Func<string, CancellationToken, ValueTask<RegisterValue>>? OnRead { get; init; }

    internal Func<string, ByteString?, ByteString, CancellationToken, ValueTask<CompareExchangeResult>>? OnWrite { get; init; }

    internal List<ReadCall> Reads { get; } = [];

    internal List<WriteCall> Writes { get; } = [];

    internal int DisposeCount { get; private set; }

    public ValueTask<RegisterValue> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        Reads.Add(new ReadCall(key, cancellationToken));
        return OnRead is { } read
            ? read(key, cancellationToken)
            : throw new InvalidOperationException("Unexpected register read.");
    }

    public ValueTask<CompareExchangeResult> CompareExchangeAsync(
        string key,
        ByteString? expectedVersion,
        ByteString value,
        CancellationToken cancellationToken = default)
    {
        Writes.Add(new WriteCall(key, expectedVersion, value, cancellationToken));
        return OnWrite is { } write
            ? write(key, expectedVersion, value, cancellationToken)
            : throw new InvalidOperationException("Unexpected register write.");
    }

    public ValueTask<RegisterKeyPage> ListKeysAsync(
        string prefix = "",
        string? afterKey = null,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The MetaStore adapter must not enumerate registers.");

    public void Dispose() => DisposeCount++;

    internal sealed record ReadCall(string Key, CancellationToken CancellationToken);

    internal sealed record WriteCall(
        string Key,
        ByteString? ExpectedVersion,
        ByteString Value,
        CancellationToken CancellationToken);
}
