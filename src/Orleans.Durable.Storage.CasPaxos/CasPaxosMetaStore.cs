using System.Buffers;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.CasPaxos;

/// <summary>
/// One MetaStore register in a Fast CASPaxos conditional register, whose version is the canonical Base64 of the
/// protocol's opaque version bytes.
/// </summary>
public sealed class CasPaxosMetaStore : IConditionalRegister
{
    private readonly IRegisterService _registers;
    private readonly string _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="CasPaxosMetaStore"/> class over a caller-owned service.
    /// </summary>
    /// <param name="registers">The register service.</param>
    /// <param name="key">The complete backend register key.</param>
    public CasPaxosMetaStore(IRegisterService registers, string key)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(key);
        _registers = registers;
        _key = key;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The protocol's <see cref="ByteString"/> owns a copy of the register's bytes, which the read copies again into the
    /// array its entry wraps.
    /// </remarks>
    public async ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _registers.ReadAsync(_key, cancellationToken).ConfigureAwait(false);
        if (!result.IsPresent)
        {
            return null;
        }

        // The register contract pairs a present value with its opaque version.
        return new MetaStoreEntry(EncodeVersion(result.Version!.Value), ArcBuffer.Wrap(result.Value!.Value.ToArray()));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The value is copied into the protocol's <see cref="ByteString"/> before the request is dispatched. An applied
    /// write's version is the one the protocol reports as the register's current version.
    /// </remarks>
    public async ValueTask<RegisterWriteOutcome> WriteAsync(
        MetaStoreVersion? expectedVersion,
        ReadOnlySequence<byte> value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ByteString? version = null;
        if (expectedVersion is not null)
        {
            version = DecodeVersion(expectedVersion);
            if (version is null)
            {
                return RegisterWriteOutcome.Mismatch.Instance;
            }
        }

        // "Writing to it requires supplying a new value and an expected existing version."
        // See research/papers/delos/paper.md, section 3.2. Callers decide unknown outcomes by re-reading (MS-I3).
        var result = await _registers.CompareExchangeAsync(
            _key,
            version,
            ToByteString(value),
            cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case CompareExchangeStatus.Applied:
                return result.Current is { IsPresent: true, Version: { } appliedVersion }
                    ? new RegisterWriteOutcome.Applied(EncodeVersion(appliedVersion))
                    : throw new IOException("The Fast CASPaxos service reported an applied write without the register's version, so the write's outcome is unknown. Re-read the register to establish the operation's goal.");
            case CompareExchangeStatus.Mismatch:
                // A mismatch is reported only while the caller's token is not cancelled (metastore.md §6).
                cancellationToken.ThrowIfCancellationRequested();
                return RegisterWriteOutcome.Mismatch.Instance;
            case CompareExchangeStatus.Unknown:
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException(
                    "The Fast CASPaxos MetaStore write has an unknown outcome. Re-read the register to establish the operation's goal.",
                    result.Error);
            default:
                throw new InvalidDataException("The Fast CASPaxos service returned an invalid conditional-write outcome.");
        }
    }

    private static MetaStoreVersion EncodeVersion(ByteString version) => new(Convert.ToBase64String(version.ToArray()));

    /// <summary>
    /// Copies <paramref name="value"/> into the protocol's <see cref="ByteString"/>, which copies the span it is given:
    /// a value spanning ledgers passes through a pooled array, so the value is copied into memory the
    /// <see cref="ByteString"/> owns once.
    /// </summary>
    private static ByteString ToByteString(in ReadOnlySequence<byte> value)
    {
        if (value.IsSingleSegment)
        {
            return new ByteString(value.FirstSpan);
        }

        var length = checked((int)value.Length);
        var gathered = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            value.CopyTo(gathered);
            return new ByteString(gathered.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(gathered);
        }
    }

    private static ByteString? DecodeVersion(MetaStoreVersion version)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(version.Value);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!string.Equals(Convert.ToBase64String(bytes), version.Value, StringComparison.Ordinal))
        {
            return null;
        }

        var decoded = new ByteString(bytes);
        return RegisterVersion.IsValid(decoded) ? decoded : null;
    }
}
