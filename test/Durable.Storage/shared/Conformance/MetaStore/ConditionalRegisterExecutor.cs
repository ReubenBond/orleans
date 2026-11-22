using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Runs <see cref="ConditionalRegisterSpec"/> requests against an <see cref="IConditionalRegister"/>.
/// </summary>
/// <remarks>
/// A request names a version by the value of the write which gave it. The executor learns each value's version from the
/// write which applied it and from the reads which return it, requires every later answer for that value to carry the
/// same version, and requires no version to be reused across distinct values, so each value has one fresh version and a
/// version an applied write returns is the register's version after it (MS-I5). A write whose outcome is unknown, or
/// which applied and answered a mismatch, returns no version: the executor learns that value's version only from a read.
/// It names a value whose version it never learned with a well-formed version no write issued, which a register answers
/// with a mismatch. A call which throws propagates its exception: its caller decides whether the call's outcome is
/// unknown. The executor disposes every entry it reads.
/// </remarks>
internal sealed class ConditionalRegisterExecutor
{
    private readonly Lock _lock = new();
    private readonly IConditionalRegister _register;
    private readonly Dictionary<byte, MetaStoreVersion> _versions;
    private readonly Dictionary<MetaStoreVersion, byte> _valuesByVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConditionalRegisterExecutor"/> class which knows no version yet.
    /// </summary>
    internal ConditionalRegisterExecutor(IConditionalRegister register)
        : this(register, [], [])
    {
    }

    private ConditionalRegisterExecutor(
        IConditionalRegister register,
        Dictionary<byte, MetaStoreVersion> versions,
        Dictionary<MetaStoreVersion, byte> valuesByVersion)
    {
        _register = register;
        _versions = versions;
        _valuesByVersion = valuesByVersion;
    }

    /// <summary>
    /// Returns an executor over <paramref name="register"/>, another view of the same register, which starts from what
    /// this one knows and learns apart from it.
    /// </summary>
    internal ConditionalRegisterExecutor Over(IConditionalRegister register)
    {
        lock (_lock)
        {
            return new(register, [with(_versions)], [with(_valuesByVersion)]);
        }
    }

    /// <summary>
    /// Writes <paramref name="write"/>'s payload, naming the version learned for <c>write.Expected</c>, and learns the
    /// version an applied write returns.
    /// </summary>
    /// <exception cref="InvalidDataException">An applied write returned another version than an earlier answer gave its
    /// value.</exception>
    internal async Task<RegisterOutcome> WriteAsync(RegisterWrite write, CancellationToken cancellationToken)
    {
        var outcome = await _register.WriteAsync(
            Expected(write),
            new ReadOnlySequence<byte>(ConditionalRegisterSpec.Payload(write.Value)),
            cancellationToken);
        return outcome switch
        {
            RegisterWriteOutcome.Applied applied => LearnApplied(write.Value, applied.Version),
            RegisterWriteOutcome.Mismatch => new RegisterOutcome(RegisterOutcomeKind.Mismatch),
            null => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// Reads the register and learns the version of the value it holds.
    /// </summary>
    /// <exception cref="InvalidDataException">The register holds bytes no write of the spec stores, or a value at another
    /// version than an earlier answer gave it.</exception>
    internal async Task<RegisterOutcome> ReadAsync(CancellationToken cancellationToken)
    {
        using var entry = await _register.ReadAsync(cancellationToken);
        if (entry is null)
        {
            return new RegisterOutcome(RegisterOutcomeKind.Absent);
        }

        var bytes = entry.Value.ToArray();
        if (bytes.Length == 0 || !bytes.AsSpan().SequenceEqual(ConditionalRegisterSpec.Payload(bytes[0])))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"The register holds {bytes.Length} bytes which no write stores."));
        }

        Learn(bytes[0], entry.Version, "a read returned");
        return new RegisterOutcome(RegisterOutcomeKind.Present, bytes[0]);
    }

    private RegisterOutcome LearnApplied(byte value, MetaStoreVersion version)
    {
        Learn(value, version, "an applied write returned");
        return new RegisterOutcome(RegisterOutcomeKind.Applied);
    }

    private void Learn(byte value, MetaStoreVersion version, string source)
    {
        lock (_lock)
        {
            if (_versions.TryGetValue(value, out var known))
            {
                if (known != version)
                {
                    throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"For value {value}, {source} version {version.Value}, which an earlier answer gave as {known.Value}."));
                }

                Debug.Assert(_valuesByVersion[version] == value, "The forward and reverse version maps must stay coupled.");
                return;
            }

            if (_valuesByVersion.TryGetValue(version, out var owner))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"For value {value}, {source} version {version.Value}, which an earlier answer already gave to value {owner}."));
            }

            _versions.Add(value, version);
            _valuesByVersion.Add(version, value);
        }
    }

    private MetaStoreVersion? Expected(RegisterWrite write)
    {
        if (write.Expected is not { } value)
        {
            return null;
        }

        lock (_lock)
        {
            return _versions.TryGetValue(value, out var version)
                ? version
                : new MetaStoreVersion(new Guid(0x55555555, 0, 0, 0, 0, 0, 0, 0, 0, 0, value).ToString("N"));
        }
    }
}
