using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using static Orleans.Durable.Storage.Engine.Wal.IoUring.IoUringInterop;

namespace Orleans.Durable.Storage.Engine.Wal.IoUring;


/// <summary>
/// One io_uring with a single issuing thread, shared by every log of an <see cref="IoUringWalStorage"/>.
/// </summary>
/// <remarks>
/// The ring is created with <c>IORING_SETUP_SINGLE_ISSUER</c> and <c>IORING_SETUP_DEFER_TASKRUN</c>, so the
/// engine thread both submits and reaps. Callers queue operations and write an eventfd which the engine keeps a read
/// outstanding on; the engine then submits everything queued and blocks in <c>io_uring_enter</c> until a completion
/// arrives. Each operation's <c>user_data</c> packs a slot and a generation, so a stale completion cannot complete a
/// recycled slot. An operation's buffers stay pinned and its file handle referenced until its last completion.
/// <para>
/// A failed <c>io_uring_enter</c> fails the ring, which every operation needs: the engine reports it to
/// <see cref="FatalErrorHandler"/> (<see cref="FatalErrorSite.IoUringRing"/>), which stops the process
/// (conventions.md §14), and when a test intercepts the report, completes every operation in flight or queued, and every
/// later one, with the reported failure, so no waiter hangs.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class IoUringEngine : IDisposable
{
    private const uint Entries = 256;
    private const ulong WakeUserData = ulong.MaxValue;

    private readonly ConcurrentQueue<IoUringOperation> _submissions = new();
    private readonly Queue<IoUringOperation> _retries = new();
    private readonly IoUringSyncs _syncs = new();
    private readonly IoUringOperation?[] _slots = new IoUringOperation?[Entries - 1];
    private readonly uint[] _generations = new uint[Entries - 1];
    private readonly Stack<int> _free = new(Enumerable.Range(0, (int)Entries - 1).Reverse());
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private int _ringFd = -1;
    private int _eventFd = -1;
    private nint _ring;
    private nuint _ringSize;
    private nint _sqeMemory;
    private nuint _sqeSize;
    private uint* _sqHead;
    private uint* _sqTail;
    private uint _sqMask;
    private uint* _cqHead;
    private uint* _cqTail;
    private uint _cqMask;
    private Cqe* _cqes;
    private Sqe* _sqes;
    private ulong* _wakeBuffer;
    private bool _wakeArmed;
    private int _inFlight;
    private volatile bool _stopping;

    // The reported failure of the ring, which every later operation completes with.
    private IOException? _failure;

    // Test hooks: an errno which the next operation's completion, or the next io_uring_enter, takes in place of the
    // kernel's result, so a test reaches the storage's reports of device errors it cannot provoke; zero when unset.
    private int _failNextCompletion;
    private int _failNextEnter;

    public IoUringEngine()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "WriteAheadLog io_uring" };
        _thread.Start();
        try
        {
            _started.Task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            throw new PlatformNotSupportedException("io_uring could not be set up; IoUringWalStorage requires Linux 6.1 or later with io_uring enabled.", exception);
        }
    }

    public void Submit(IoUringOperation operation)
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            operation.Fail(failure);
            return;
        }

        if (_stopping)
        {
            // A failed ring published its failure before it stopped; only disposal stops the engine without one.
            if (Volatile.Read(ref _failure) is { } stopped)
            {
                operation.Fail(stopped);
                return;
            }

            throw new ObjectDisposedException(nameof(IoUringWalStorage));
        }

        _submissions.Enqueue(operation);
        if (Volatile.Read(ref _failure) is { } failed)
        {
            // The ring failed after the check, and may have failed the queue before this operation joined it.
            FailQueued(failed);
            return;
        }

        Wake();
    }

    /// <summary>
    /// Makes the next operation's completion fail with <paramref name="error"/>, an errno, as a device error fails it.
    /// </summary>
    internal void FailNextCompletion(int error) => Volatile.Write(ref _failNextCompletion, error);

    /// <summary>
    /// Makes the engine's next <c>io_uring_enter</c> fail with <paramref name="error"/>, an errno, which fails the ring.
    /// </summary>
    internal void FailNextEnter(int error) => Volatile.Write(ref _failNextEnter, error);

    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        Wake();
        _thread.Join();
    }

    private void Wake()
    {
        ulong one = 1;
        _ = Write(_eventFd, &one, sizeof(ulong));
    }

    private void Run()
    {
        try
        {
            Setup();
        }
        catch (Exception exception)
        {
            Teardown();
            _ = _started.TrySetException(exception);
            return;
        }

        _ = _started.TrySetResult();
        try
        {
            while (true)
            {
                var toSubmit = Prepare();
                if (_stopping && _inFlight == 0 && _submissions.IsEmpty && _retries.Count == 0 && _syncs.IsEmpty)
                {
                    break;
                }

                Enter(toSubmit);
                Reap();
            }
        }
        catch (IOException exception)
        {
            // Only Enter throws it: the ring failed.
            Stop(exception);
        }
        finally
        {
            Teardown();
        }
    }

    /// <summary>
    /// Reports the ring's failure, which stops the process, and, when a test intercepts the report, completes every
    /// operation with it. An operation the kernel consumed may still be in flight there, so its buffers stay pinned and
    /// its handle referenced; only the stopped process's exit releases them.
    /// </summary>
    private void Stop(IOException exception)
    {
        FatalErrorHandler.StopOnStorageFailure(
            FatalErrorSite.IoUringRing, string.Create(CultureInfo.InvariantCulture, $"/proc/self/fd/{_ringFd}"), exception);

        // The failure is published before the stop, so a submission which sees the engine stopping finds the failure
        // first and completes with it.
        Volatile.Write(ref _failure, exception);
        _stopping = true;
        for (var slot = 0; slot < _slots.Length; slot++)
        {
            if (_slots[slot] is { } operation)
            {
                _slots[slot] = null;
                operation.Abandon(exception);
            }
        }

        _inFlight = 0;
        foreach (var waiting in _syncs.TakeWaiting())
        {
            waiting.Abandon(exception);
        }
        while (_retries.TryDequeue(out var retry))
        {
            retry.Fail(exception);
        }

        FailQueued(exception);
    }

    private void FailQueued(IOException exception)
    {
        while (_submissions.TryDequeue(out var operation))
        {
            operation.Fail(exception);
        }
    }

    private void Setup()
    {
        _eventFd = EventFd(0, EventFdCloseOnExec);
        ThrowIfFailed(_eventFd, "eventfd");
        _wakeBuffer = (ulong*)NativeMemory.Alloc(sizeof(ulong));
        var parameters = new Params { Flags = SetupSingleIssuer | SetupDeferTaskRun | SetupCoopTaskRun };
        var fd = Syscall(SetupSyscall, Entries, (long)&parameters, 0, 0, 0, 0);
        ThrowIfFailed(fd, "io_uring_setup");
        _ringFd = (int)fd;
        if ((parameters.Features & FeatureSingleMmap) == 0)
        {
            throw new PlatformNotSupportedException("The kernel's io_uring lacks IORING_FEAT_SINGLE_MMAP.");
        }

        _ringSize = Math.Max(
            parameters.SqOffsets.Array + parameters.SqEntries * sizeof(uint),
            parameters.CqOffsets.Cqes + parameters.CqEntries * (uint)sizeof(Cqe));
        _ring = Mmap(0, _ringSize, ProtReadWrite, MapSharedPopulate, _ringFd, OffSqRing);
        ThrowIfFailed(_ring, "mmap of the io_uring rings");
        _sqeSize = parameters.SqEntries * (nuint)sizeof(Sqe);
        _sqeMemory = Mmap(0, _sqeSize, ProtReadWrite, MapSharedPopulate, _ringFd, OffSqes);
        ThrowIfFailed(_sqeMemory, "mmap of the io_uring submission entries");

        var ring = (byte*)_ring;
        _sqHead = (uint*)(ring + parameters.SqOffsets.Head);
        _sqTail = (uint*)(ring + parameters.SqOffsets.Tail);
        _sqMask = *(uint*)(ring + parameters.SqOffsets.RingMask);
        var array = (uint*)(ring + parameters.SqOffsets.Array);
        for (uint index = 0; index < parameters.SqEntries; index++)
        {
            array[index] = index;
        }

        _cqHead = (uint*)(ring + parameters.CqOffsets.Head);
        _cqTail = (uint*)(ring + parameters.CqOffsets.Tail);
        _cqMask = *(uint*)(ring + parameters.CqOffsets.RingMask);
        _cqes = (Cqe*)(ring + parameters.CqOffsets.Cqes);
        _sqes = (Sqe*)_sqeMemory;
    }

    /// <summary>
    /// Fills submission entries for the wake read, retried operations, and queued operations while slots remain.
    /// </summary>
    private uint Prepare()
    {
        uint count = 0;
        var tail = *_sqTail;
        if (!_wakeArmed && !_stopping)
        {
            var sqe = NextSqe(ref tail);
            sqe->Opcode = OpRead;
            sqe->Fd = _eventFd;
            sqe->Address = (ulong)_wakeBuffer;
            sqe->Length = sizeof(ulong);
            sqe->UserData = WakeUserData;
            _wakeArmed = true;
            count++;
        }

        // A file's data sync covers every write to it whose transfer completed before the sync is submitted, so each file
        // has at most one sync in flight, and writes which complete meanwhile share the next.
        while (_free.Count > 0 && _syncs.TryTake(out var handle, out var covered))
        {
            count += Place(IoUringOperation.Sync(handle, covered), ref tail);
        }

        while (_free.Count > 0 && (_retries.TryDequeue(out var operation) || _submissions.TryDequeue(out operation)))
        {
            count += Place(operation, ref tail);
        }

        Volatile.Write(ref *_sqTail, tail);
        return count;
    }

    private uint Place(IoUringOperation operation, ref uint tail)
    {
        var slot = _free.Pop();
        var generation = unchecked(++_generations[slot]);
        _slots[slot] = operation;
        var sqe = NextSqe(ref tail);
        operation.Prepare(sqe);
        sqe->UserData = ((ulong)generation << 32) | (uint)slot;
        _inFlight++;
        return 1;
    }

    private Sqe* NextSqe(ref uint tail)
    {
        var sqe = &_sqes[tail & _sqMask];
        *sqe = default;
        tail = unchecked(tail + 1);
        return sqe;
    }

    private void Enter(uint toSubmit)
    {
        if (Volatile.Read(ref _failNextEnter) != 0 && Interlocked.Exchange(ref _failNextEnter, 0) is var injected and not 0)
        {
            throw new IOException($"io_uring_enter failed: {Marshal.GetPInvokeErrorMessage(injected)}");
        }

        while (true)
        {
            var result = Syscall(EnterSyscall, _ringFd, toSubmit, 1, EnterGetEvents, 0, 0);
            if (result >= 0)
            {
                return;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error is not (Eintr or Eagain or Ebusy))
            {
                throw new IOException($"io_uring_enter failed: {Marshal.GetPInvokeErrorMessage(error)}");
            }

            // Every entry was consumed by the first attempt or none was; retry only the wait.
            toSubmit = Volatile.Read(ref *_sqHead) == *_sqTail ? 0 : toSubmit;
        }
    }

    private void Reap()
    {
        var head = *_cqHead;
        var tail = Volatile.Read(ref *_cqTail);
        while (head != tail)
        {
            var cqe = _cqes[head & _cqMask];
            head = unchecked(head + 1);
            if (cqe.UserData == WakeUserData)
            {
                _wakeArmed = false;
                continue;
            }

            var slot = (int)unchecked((uint)cqe.UserData);
            var operation = _slots[slot];
            if (operation is null || _generations[slot] != unchecked((uint)(cqe.UserData >> 32)))
            {
                continue;
            }

            _slots[slot] = null;
            _free.Push(slot);
            _inFlight--;
            var result = Volatile.Read(ref _failNextCompletion) != 0 && Interlocked.Exchange(ref _failNextCompletion, 0) is var injected and not 0
                ? -injected
                : cqe.Result;
            var completion = operation.Complete(result);
            if (completion == IoUringCompletion.Resubmit)
            {
                _retries.Enqueue(operation);
            }
            else if (completion == IoUringCompletion.AwaitSync)
            {
                _syncs.Add(operation);
            }
            else if (operation.IsSync)
            {
                _syncs.Completed(operation.Handle);
            }
        }

        Volatile.Write(ref *_cqHead, head);
    }

    private void Teardown()
    {
        while (_submissions.TryDequeue(out var operation) || _retries.TryDequeue(out operation))
        {
            // An operation queued after a failed ring's queue was failed completes with the ring's failure.
            operation.Fail((Exception?)Volatile.Read(ref _failure) ?? new ObjectDisposedException(nameof(IoUringWalStorage)));
        }

        if (_sqeMemory > 0)
        {
            _ = Munmap(_sqeMemory, _sqeSize);
        }

        if (_ring > 0)
        {
            _ = Munmap(_ring, _ringSize);
        }

        if (_ringFd >= 0)
        {
            _ = Close(_ringFd);
        }

        if (_eventFd >= 0)
        {
            _ = Close(_eventFd);
        }

        NativeMemory.Free(_wakeBuffer);
    }

    private static void ThrowIfFailed(long result, string operation)
    {
        if (result < 0)
        {
            throw new IOException($"{operation} failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }
    }
}

/// <summary>
/// The engine's data syncs: for each file, whether a sync is queued or in flight, and the writes whose transfers
/// completed since the in-flight sync was submitted.
/// </summary>
/// <remarks>
/// A data sync covers only the writes whose transfers completed before it was submitted, so a write which completes
/// while its file's sync is in flight waits for the next. Each file has at most one sync in flight, which every waiting
/// write shares. Only the engine thread calls it.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class IoUringSyncs
{
    private readonly Dictionary<SafeFileHandle, FileSync> _files = [with(ReferenceEqualityComparer.Instance)];
    private readonly Queue<FileSync> _ready = new();

    public bool IsEmpty => _files.Count == 0;

    /// <summary>
    /// Adds a write whose transfer completed, to be covered by its file's next data sync.
    /// </summary>
    public void Add(IoUringOperation write)
    {
        if (!_files.TryGetValue(write.Handle, out var file))
        {
            file = new FileSync(write.Handle);
            _files.Add(write.Handle, file);
        }

        file.Waiting.Add(write);
        Queue(file);
    }

    /// <summary>
    /// Takes the next file whose waiting writes need a data sync, marking its sync in flight.
    /// </summary>
    public bool TryTake(out SafeFileHandle handle, out List<IoUringOperation> covered)
    {
        if (!_ready.TryDequeue(out var file))
        {
            handle = null!;
            covered = null!;
            return false;
        }

        file.Queued = false;
        file.InFlight = true;
        handle = file.Handle;
        covered = file.Waiting;
        file.Waiting = [];
        return true;
    }

    /// <summary>
    /// Records that a file's in-flight data sync completed, queueing the next for writes which completed meanwhile.
    /// </summary>
    public void Completed(SafeFileHandle handle)
    {
        var file = _files[handle];
        file.InFlight = false;
        if (file.Waiting.Count == 0)
        {
            _ = _files.Remove(handle);
            return;
        }

        Queue(file);
    }

    /// <summary>
    /// Removes every file and returns the writes no sync was submitted for, when the ring fails.
    /// </summary>
    public List<IoUringOperation> TakeWaiting()
    {
        var waiting = _files.Values.SelectMany(static file => file.Waiting).ToList();
        _files.Clear();
        _ready.Clear();
        return waiting;
    }

    private void Queue(FileSync file)
    {
        if (!file.InFlight && !file.Queued)
        {
            file.Queued = true;
            _ready.Enqueue(file);
        }
    }

    private sealed class FileSync(SafeFileHandle handle)
    {
        public SafeFileHandle Handle { get; } = handle;

        public List<IoUringOperation> Waiting { get; set; } = [];

        public bool InFlight { get; set; }

        public bool Queued { get; set; }
    }
}

/// <summary>
/// What the engine does with an operation after one completion.
/// </summary>
internal enum IoUringCompletion
{
    /// <summary>The operation completed or failed.</summary>
    Done,

    /// <summary>The operation must be resubmitted for its remaining bytes.</summary>
    Resubmit,

    /// <summary>A write transferred every byte, and becomes durable with its file's next data sync.</summary>
    AwaitSync,
}

/// <summary>
/// One positioned vectored read; a positioned vectored write, which completes once a data sync of its file submitted
/// after its transfer succeeds; or such a data sync, carried through the engine until it completes.
/// </summary>
/// <remarks>
/// A short transfer is resubmitted for the remaining bytes. A read completes early at the end of the file: at a
/// transfer which moves nothing or ends part-way through an aligned page.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class IoUringOperation
{
    internal const int MaxVectors = 1024;
    private readonly byte _opcode;
    private readonly SafeFileHandle _handle;
    private readonly long _offset;
    private readonly MemoryHandle[] _pins;
    private readonly IoVector* _vectors;
    private readonly int _count;
    private long _total;
    private readonly TaskCompletionSource<long> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _done;
    private int _first;
    private int _pinned;
    private bool _referenced;

    // The writes a data sync makes durable.
    private List<IoUringOperation>? _covered;

    private IoUringOperation(byte opcode, SafeFileHandle handle, long offset, int count)
    {
        _opcode = opcode;
        _handle = handle;
        _offset = offset;
        _count = count;
        _pins = ArrayPool<MemoryHandle>.Shared.Rent(count);
        _vectors = (IoVector*)NativeMemory.Alloc((nuint)Math.Max(1, count), (nuint)sizeof(IoVector));
        try
        {
            handle.DangerousAddRef(ref _referenced);
        }
        catch
        {
            Release();
            throw;
        }
    }

    public Task<long> Completion => _completion.Task;

    public SafeFileHandle Handle => _handle;

    public bool IsSync => _opcode == OpFsync;

    /// <summary>
    /// Creates the data sync which completes <paramref name="covered"/>, writes whose transfers completed before it is
    /// submitted.
    /// </summary>
    public static IoUringOperation Sync(SafeFileHandle handle, List<IoUringOperation> covered) =>
        new(OpFsync, handle, 0, 0) { _covered = covered };

    public static IoUringOperation Write(SafeFileHandle handle, long offset, ReadOnlyMemory<ArcBuffer> buffers)
    {
        var count = 0;
        foreach (var buffer in buffers.Span)
        {
            foreach (var memory in buffer.MemorySegments)
            {
                if (!memory.IsEmpty)
                {
                    count++;
                }
            }
        }

        var operation = new IoUringOperation(OpWritev, handle, offset, count);
        try
        {
            var index = 0;
            foreach (var buffer in buffers.Span)
            {
                foreach (var memory in buffer.MemorySegments)
                {
                    if (!memory.IsEmpty)
                    {
                        operation.Pin(index++, MemoryMarshal.AsMemory(memory));
                    }
                }
            }
        }
        catch
        {
            operation.Release();
            throw;
        }

        return operation;
    }

    public static IoUringOperation Read(SafeFileHandle handle, long offset, List<Memory<byte>> segments)
    {
        var operation = new IoUringOperation(OpReadv, handle, offset, segments.Count);
        try
        {
            for (var index = 0; index < segments.Count; index++)
            {
                operation.Pin(index, segments[index]);
            }
        }
        catch
        {
            operation.Release();
            throw;
        }

        return operation;
    }

    private void Pin(int index, Memory<byte> segment)
    {
        _pins[index] = segment.Pin();
        _pinned++;
        _vectors[index].Base = _pins[index].Pointer;
        _vectors[index].Length = (nuint)segment.Length;
        _total += segment.Length;
    }

    public void Prepare(Sqe* sqe)
    {
        sqe->Opcode = _opcode;
        sqe->Fd = (int)_handle.DangerousGetHandle();
        if (_opcode == OpFsync)
        {
            sqe->OperationFlags = FsyncDataSync;
            return;
        }

        sqe->Offset = (ulong)(_offset + _done);
        sqe->Address = (ulong)(_vectors + _first);

        // Linux refuses a vectored transfer above UIO_MAXIOV vectors with EINVAL; the rest follows as a short transfer.
        sqe->Length = (uint)Math.Min(_count - _first, MaxVectors);

    }

    /// <summary>
    /// Applies one completion.
    /// </summary>
    public IoUringCompletion Complete(int result)
    {
        if (result < 0)
        {
            if (-result is Eintr or Eagain)
            {
                return IoUringCompletion.Resubmit;
            }

            Fail(new IOException($"io_uring operation {_opcode} failed: {Marshal.GetPInvokeErrorMessage(-result)}"));
            return IoUringCompletion.Done;
        }

        if (_opcode == OpFsync)
        {
            Release();
            foreach (var write in _covered!)
            {
                write.Synced();
            }

            _ = _completion.TrySetResult(0);
            return IoUringCompletion.Done;
        }

        _done += result;
        if (_done < _total && !EndsRead(result))
        {
            if (result == 0)
            {
                Fail(new IOException("io_uring write made no progress."));
                return IoUringCompletion.Done;
            }

            var skip = (nuint)result;
            while (_first < _count && skip >= _vectors[_first].Length)
            {
                skip -= _vectors[_first].Length;
                _first++;
            }

            if (_first < _count)
            {
                _vectors[_first].Base = (byte*)_vectors[_first].Base + skip;
                _vectors[_first].Length -= skip;
            }

            return IoUringCompletion.Resubmit;
        }

        if (_opcode == OpWritev)
        {
            // The write completes only once a data sync submitted after its transfer makes its bytes, and the file
            // length they need, durable.
            return IoUringCompletion.AwaitSync;
        }

        Release();
        _ = _completion.TrySetResult(_done);
        return IoUringCompletion.Done;
    }

    public void Fail(Exception exception)
    {
        Release();
        if (_covered is not null)
        {
            foreach (var write in _covered)
            {
                write.Fail(exception);
            }
        }

        _ = _completion.TrySetException(exception);
    }

    /// <summary>
    /// Completes the operation with <paramref name="exception"/> while the kernel may still hold it: its buffers stay
    /// pinned and its handle referenced, since the kernel may yet transfer into them. A data sync abandons the writes it
    /// covers.
    /// </summary>
    public void Abandon(Exception exception)
    {
        if (_covered is not null)
        {
            foreach (var write in _covered)
            {
                write.Abandon(exception);
            }
        }

        _ = _completion.TrySetException(exception);
    }

    private void Synced()
    {
        Release();
        _ = _completion.TrySetResult(_done);
    }

    /// <summary>
    /// Returns whether a read's transfer reached the end of the file: it moved nothing, or stopped part-way through an
    /// aligned page, which a direct read does only at the end of the file. Resubmitting from there would be unaligned.
    /// </summary>
    private bool EndsRead(int result) => _opcode == OpReadv && (result == 0 || _done % WalDirectIo.Alignment != 0);

    private void Release()
    {
        for (var index = 0; index < _pinned; index++)
        {
            _pins[index].Dispose();
        }

        if (_pins.Length > 0)
        {
            ArrayPool<MemoryHandle>.Shared.Return(_pins, clearArray: true);
        }
        NativeMemory.Free(_vectors);
        if (_referenced)
        {
            _referenced = false;
            _handle.DangerousRelease();
        }
    }
}
