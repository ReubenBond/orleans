using System.Runtime.InteropServices;

namespace Orleans.Durable.Storage.Engine.Wal.IoUring;

/// <summary>
/// The Linux io_uring ABI used by <see cref="IoUringWalStorage"/>, for little-endian 64-bit Linux.
/// </summary>
/// <remarks>
/// The system call numbers are shared by x86-64 and arm64. Layouts follow <c>include/uapi/linux/io_uring.h</c>.
/// </remarks>
internal static partial class IoUringInterop
{
    public const long SetupSyscall = 425;
    public const long EnterSyscall = 426;

    public const uint SetupCoopTaskRun = 1u << 8;
    public const uint SetupSingleIssuer = 1u << 12;
    public const uint SetupDeferTaskRun = 1u << 13;

    public const uint FeatureSingleMmap = 1u << 0;

    public const uint EnterGetEvents = 1u << 0;

    public const int FsyncDataSync = 1;

    public const byte OpReadv = 1;
    public const byte OpWritev = 2;
    public const byte OpFsync = 3;
    public const byte OpRead = 22;

    public const long OffSqRing = 0;
    public const long OffSqes = 0x10000000;

    public const int ProtReadWrite = 0x1 | 0x2;
    public const int MapSharedPopulate = 0x01 | 0x8000;

    public const int EventFdCloseOnExec = 0x80000;

    public const int Eintr = 4;
    public const int Eagain = 11;
    public const int Ebusy = 16;

    [StructLayout(LayoutKind.Sequential)]
    public struct SqRingOffsets
    {
        public uint Head;
        public uint Tail;
        public uint RingMask;
        public uint RingEntries;
        public uint Flags;
        public uint Dropped;
        public uint Array;
        public uint Reserved1;
        public ulong UserAddress;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CqRingOffsets
    {
        public uint Head;
        public uint Tail;
        public uint RingMask;
        public uint RingEntries;
        public uint Overflow;
        public uint Cqes;
        public uint Flags;
        public uint Reserved1;
        public ulong UserAddress;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public uint SqEntries;
        public uint CqEntries;
        public uint Flags;
        public uint SqThreadCpu;
        public uint SqThreadIdle;
        public uint Features;
        public uint WqFd;
        public uint Reserved0;
        public uint Reserved1;
        public uint Reserved2;
        public SqRingOffsets SqOffsets;
        public CqRingOffsets CqOffsets;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct Sqe
    {
        [FieldOffset(0)]
        public byte Opcode;

        [FieldOffset(1)]
        public byte Flags;

        [FieldOffset(2)]
        public ushort IoPriority;

        [FieldOffset(4)]
        public int Fd;

        [FieldOffset(8)]
        public ulong Offset;

        [FieldOffset(16)]
        public ulong Address;

        [FieldOffset(24)]
        public uint Length;

        [FieldOffset(28)]
        public int OperationFlags;

        [FieldOffset(32)]
        public ulong UserData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Cqe
    {
        public ulong UserData;
        public int Result;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoVector
    {
        public void* Base;
        public nuint Length;
    }

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial long Syscall(long number, long argument1, long argument2, long argument3, long argument4, long argument5, long argument6);

    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial nint Mmap(nint address, nuint length, int protection, int flags, int fd, long offset);

    [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int Munmap(nint address, nuint length);

    [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int EventFd(uint initial, int flags);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial nint Write(int fd, void* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int Close(int fd);
}
