using System.Diagnostics;
using System.IO.Hashing;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Computes CRC-32C (conventions.md §4.1) across a batch's pages with System.IO.Hashing, one instance per thread, so that
/// encoding and decoding allocate one only in each thread's first use.
/// </summary>
internal static class BatchCrc
{
    [ThreadStatic]
    private static Crc32? _perThread;

    [ThreadStatic]
    private static bool _inUse;

    /// <summary>Returns this thread's CRC-32C, reset, for one user at a time until the lease is disposed.</summary>
    public static Lease Start()
    {
        var crc = _perThread ??= new Crc32(Crc32ParameterSet.Crc32C);
        Enter(crc);
        crc.Reset();
        return new Lease(crc);
    }

    [Conditional("DEBUG")]
    private static void Enter(Crc32 crc)
    {
        Debug.Assert(!_inUse && ReferenceEquals(crc, _perThread), "A thread's CRC-32C has one user at a time.");
        _inUse = true;
    }

    /// <summary>One use of a thread's CRC-32C.</summary>
    public readonly ref struct Lease(Crc32 crc)
    {
        public uint Hash => crc.GetCurrentHashAsUInt32();

        public void Append(ReadOnlySpan<byte> bytes) => crc.Append(bytes);

        public void Dispose() => Exit(crc);

        [Conditional("DEBUG")]
        private static void Exit(Crc32 crc)
        {
            Debug.Assert(_inUse && ReferenceEquals(crc, _perThread), "A thread's CRC-32C is released by its user.");
            _inUse = false;
        }
    }
}
