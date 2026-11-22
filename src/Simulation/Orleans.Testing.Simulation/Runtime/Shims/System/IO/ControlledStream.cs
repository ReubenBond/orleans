using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Shims.System.IO;

/// <summary>
/// <para>
/// Controlled asynchronous members of <see cref="Stream"/>. The BCL's base implementations, which a stream that does not
/// override them inherits, run the synchronous <see cref="Stream.Read(byte[], int, int)"/>,
/// <see cref="Stream.Write(byte[], int, int)"/>, or <see cref="Stream.Flush"/> on a thread-pool thread, block the caller
/// on a semaphore (<c>BeginRead</c>, <c>BeginWrite</c>) or on the task (<c>EndRead</c>, <c>EndWrite</c>), or await the
/// other virtual members through BCL awaiters. The rewriter redirects the call sites here, receiver first. A stream
/// which overrides a member is called as written; otherwise the shim runs the BCL's base algorithm with the same calls
/// on the stream, on the coordinator: a synchronous read, write, or flush runs as a fresh controlled operation
/// concurrent with the caller, as on the thread pool, and every await is a controlled await. An override's base call
/// (<c>base.ReadAsync(...)</c>), a non-virtual <c>call</c>, runs the base algorithm through the <c>Base</c> shim.
/// </para>
/// <para>
/// As in the BCL, base reads and writes of one stream are serialized in FIFO order, sharing one gate (the BCL's
/// <c>_asyncActiveSemaphore</c>): <c>ReadAsync</c> and <c>WriteAsync</c> wait for it asynchronously and release it when
/// their operation finishes; <c>BeginRead</c> and <c>BeginWrite</c> wait for it synchronously, parking the caller on the
/// coordinator, and <c>EndRead</c> and <c>EndWrite</c> release it. A base <c>ReadAsync</c> or <c>WriteAsync</c> on a
/// stream which overrides <c>BeginRead</c>/<c>EndRead</c> (or the write pair) completes through those overrides, as the
/// BCL's does. The non-virtual members (<c>ReadExactlyAsync</c>, <c>ReadAtLeastAsync</c>, and the overloads which forward
/// to a virtual member) run the BCL's algorithm over the controlled virtual members.
/// </para>
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "Mirrors Stream, as every controlled shim mirrors its framework type's name.")]
public static class ControlledStream
{
    private const string TypeName = "System.IO.Stream.";
    private const string ReadAsyncApi = TypeName + "ReadAsync";
    private const string WriteAsyncApi = TypeName + "WriteAsync";
    private const string FlushAsyncApi = TypeName + "FlushAsync";
    private const string CopyToAsyncApi = TypeName + "CopyToAsync";
    private const string BeginReadApi = TypeName + "BeginRead";
    private const string EndReadApi = TypeName + "EndRead";
    private const string BeginWriteApi = TypeName + "BeginWrite";
    private const string EndWriteApi = TypeName + "EndWrite";
    private const string ReadExactlyAsyncApi = TypeName + "ReadExactlyAsync";
    private const string ReadAtLeastAsyncApi = TypeName + "ReadAtLeastAsync";

    // The BCL's messages (SR.NotSupported_UnreadableStream, NotSupported_UnwritableStream, ObjectDisposed_StreamClosed,
    // InvalidOperation_WrongAsyncResultOrEndCalledMultiple, IO_EOF_ReadBeyondEOF), which StreamConformanceTests compares
    // with the BCL's own exceptions.
    private const string UnreadableStreamMessage = "Stream does not support reading.";
    private const string UnwritableStreamMessage = "Stream does not support writing.";
    private const string StreamClosedMessage = "Cannot access a closed Stream.";
    private const string WrongAsyncResultMessage =
        "Either the IAsyncResult object did not come from the corresponding async method on this type, or the End method " +
        "was called multiple times with the same IAsyncResult.";
    private const string ReadBeyondEndMessage = "Unable to read beyond the end of the stream.";

    // The BCL's default copy buffer size (Stream.GetCopyBufferSize).
    private const int DefaultCopyBufferSize = 81920;

    private static readonly ConditionalWeakTable<Stream, AsyncActiveGate> Gates = [];
    private static readonly ConditionalWeakTable<IAsyncResult, ReadWriteOperation> Operations = [];

    /// <summary>Controlled <see cref="Stream.ReadAsync(byte[], int, int, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="offset">The offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <param name="cancellationToken">The token which cancels the read.</param>
    /// <returns>A task which completes with the number of bytes read.</returns>
    public static Task<int> ReadAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAsyncApi);
        return StreamOverrides.Of(stream).ReadAsyncArray
            ? stream.ReadAsync(buffer, offset, count, cancellationToken)
            : BaseReadAsync(stream, buffer, offset, count, cancellationToken);
    }

    /// <summary>Controlled base call of <see cref="Stream.ReadAsync(byte[], int, int, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="offset">The offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <param name="cancellationToken">The token which cancels the read.</param>
    /// <returns>A task which completes with the number of bytes read.</returns>
    public static Task<int> BaseReadAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAsyncApi);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken);
        }

        if (StreamOverrides.Of(stream).BeginEndRead)
        {
            return FromAsync(stream, buffer, offset, count, isRead: true);
        }

        return BeginReadWrite(stream, buffer, offset, count, null, null, isRead: true, apm: false, ReadAsyncApi);
    }

    /// <summary>Controlled <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The memory to read into.</param>
    /// <param name="cancellationToken">The token which cancels the read.</param>
    /// <returns>A value task which completes with the number of bytes read.</returns>
    public static ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAsyncApi);
        return StreamOverrides.Of(stream).ReadAsyncMemory
            ? stream.ReadAsync(buffer, cancellationToken)
            : BaseReadAsync(stream, buffer, cancellationToken);
    }

    /// <summary>Controlled base call of <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The memory to read into.</param>
    /// <param name="cancellationToken">The token which cancels the read.</param>
    /// <returns>A value task which completes with the number of bytes read.</returns>
    public static ValueTask<int> BaseReadAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAsyncApi);
        if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> array))
        {
            return new ValueTask<int>(ReadAsync(stream, array.Array!, array.Offset, array.Count, cancellationToken));
        }

        byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
        return FinishReadAsync(ReadAsync(stream, sharedBuffer, 0, buffer.Length, cancellationToken), sharedBuffer, buffer);

        static async ValueTask<int> FinishReadAsync(Task<int> readTask, byte[] localBuffer, Memory<byte> localDestination)
        {
            try
            {
                int result = await Coordinated(readTask);
                new ReadOnlySpan<byte>(localBuffer, 0, result).CopyTo(localDestination.Span);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(localBuffer);
            }
        }
    }

    /// <summary>Controlled <see cref="Stream.ReadAsync(byte[], int, int)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="offset">The offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <returns>A task which completes with the number of bytes read.</returns>
    public static Task<int> ReadAsync(Stream stream, byte[] buffer, int offset, int count) =>
        ReadAsync(stream, buffer, offset, count, CancellationToken.None);

    /// <summary>Controlled <see cref="Stream.WriteAsync(byte[], int, int, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The buffer to write from.</param>
    /// <param name="offset">The offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <param name="cancellationToken">The token which cancels the write.</param>
    /// <returns>A task which completes when the bytes have been written.</returns>
    public static Task WriteAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return StreamOverrides.Of(stream).WriteAsyncArray
            ? stream.WriteAsync(buffer, offset, count, cancellationToken)
            : BaseWriteAsync(stream, buffer, offset, count, cancellationToken);
    }

    /// <summary>Controlled base call of <see cref="Stream.WriteAsync(byte[], int, int, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The buffer to write from.</param>
    /// <param name="offset">The offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <param name="cancellationToken">The token which cancels the write.</param>
    /// <returns>A task which completes when the bytes have been written.</returns>
    public static Task BaseWriteAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (StreamOverrides.Of(stream).BeginEndWrite)
        {
            return FromAsync(stream, buffer, offset, count, isRead: false);
        }

        return BeginReadWrite(stream, buffer, offset, count, null, null, isRead: false, apm: false, WriteAsyncApi);
    }

    /// <summary>Controlled <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The memory to write from.</param>
    /// <param name="cancellationToken">The token which cancels the write.</param>
    /// <returns>A value task which completes when the bytes have been written.</returns>
    public static ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return StreamOverrides.Of(stream).WriteAsyncMemory
            ? stream.WriteAsync(buffer, cancellationToken)
            : BaseWriteAsync(stream, buffer, cancellationToken);
    }

    /// <summary>Controlled base call of <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The memory to write from.</param>
    /// <param name="cancellationToken">The token which cancels the write.</param>
    /// <returns>A value task which completes when the bytes have been written.</returns>
    public static ValueTask BaseWriteAsync(Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> array))
        {
            return new ValueTask(WriteAsync(stream, array.Array!, array.Offset, array.Count, cancellationToken));
        }

        byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
        buffer.Span.CopyTo(sharedBuffer);
        return new ValueTask(FinishWriteAsync(WriteAsync(stream, sharedBuffer, 0, buffer.Length, cancellationToken), sharedBuffer));

        static async Task FinishWriteAsync(Task writeTask, byte[] localBuffer)
        {
            try
            {
                await Coordinated(writeTask);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(localBuffer);
            }
        }
    }

    /// <summary>Controlled <see cref="Stream.WriteAsync(byte[], int, int)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The buffer to write from.</param>
    /// <param name="offset">The offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <returns>A task which completes when the bytes have been written.</returns>
    public static Task WriteAsync(Stream stream, byte[] buffer, int offset, int count) =>
        WriteAsync(stream, buffer, offset, count, CancellationToken.None);

    /// <summary>Controlled <see cref="Stream.FlushAsync(CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="cancellationToken">The token which cancels the flush before it starts.</param>
    /// <returns>A task which completes when the stream has been flushed.</returns>
    public static Task FlushAsync(Stream stream, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(FlushAsyncApi);
        return StreamOverrides.Of(stream).FlushAsync
            ? stream.FlushAsync(cancellationToken)
            : BaseFlushAsync(stream, cancellationToken);
    }

    /// <summary>Controlled base call of <see cref="Stream.FlushAsync(CancellationToken)"/>: a controlled operation which calls <see cref="Stream.Flush"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="cancellationToken">The token which cancels the flush before it starts.</param>
    /// <returns>A task which completes when the stream has been flushed.</returns>
    public static Task BaseFlushAsync(Stream stream, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(FlushAsyncApi);
        return IoWorker.Start(stream.Flush, FlushAsyncApi, cancellationToken);
    }

    /// <summary>Controlled <see cref="Stream.FlushAsync()"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <returns>A task which completes when the stream has been flushed.</returns>
    public static Task FlushAsync(Stream stream) => FlushAsync(stream, CancellationToken.None);

    /// <summary>Controlled <see cref="Stream.CopyToAsync(Stream, int, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="destination">The stream to copy to.</param>
    /// <param name="bufferSize">The size of the copy buffer.</param>
    /// <param name="cancellationToken">The token which cancels the copy.</param>
    /// <returns>A task which completes when the copy has finished.</returns>
    public static Task CopyToAsync(Stream stream, Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CopyToAsyncApi);
        return StreamOverrides.Of(stream).CopyToAsync
            ? stream.CopyToAsync(destination, bufferSize, cancellationToken)
            : BaseCopyToAsync(stream, destination, bufferSize, cancellationToken);
    }

    /// <summary>
    /// Controlled base call of <see cref="Stream.CopyToAsync(Stream, int, CancellationToken)"/>: the BCL's loop of
    /// <c>ReadAsync(Memory)</c> on this stream and <c>WriteAsync(ReadOnlyMemory)</c> on the destination, awaited on the
    /// coordinator.
    /// </summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="destination">The stream to copy to.</param>
    /// <param name="bufferSize">The size of the copy buffer.</param>
    /// <param name="cancellationToken">The token which cancels the copy.</param>
    /// <returns>A task which completes when the copy has finished.</returns>
    public static Task BaseCopyToAsync(Stream stream, Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CopyToAsyncApi);
        StreamAccess.ValidateCopyToArguments(null, destination, bufferSize);
        if (!stream.CanRead)
        {
            if (stream.CanWrite)
            {
                throw new NotSupportedException(UnreadableStreamMessage);
            }

            throw new ObjectDisposedException(stream.GetType().Name, StreamClosedMessage);
        }

        return Core(stream, destination, bufferSize, cancellationToken);

        static async Task Core(Stream source, Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                int bytesRead;
                while ((bytesRead = await new CoordinatedValueTaskAwaitable<int>(
                    ReadAsync(source, new Memory<byte>(buffer), cancellationToken))) != 0)
                {
                    await new CoordinatedValueTaskAwaitable(
                        WriteAsync(destination, new ReadOnlyMemory<byte>(buffer, 0, bytesRead), cancellationToken));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>Controlled <see cref="Stream.CopyToAsync(Stream)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="destination">The stream to copy to.</param>
    /// <returns>A task which completes when the copy has finished.</returns>
    public static Task CopyToAsync(Stream stream, Stream destination)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CopyToAsyncApi);
        return CopyToAsync(stream, destination, GetCopyBufferSize(stream), CancellationToken.None);
    }

    /// <summary>Controlled <see cref="Stream.CopyToAsync(Stream, int)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="destination">The stream to copy to.</param>
    /// <param name="bufferSize">The size of the copy buffer.</param>
    /// <returns>A task which completes when the copy has finished.</returns>
    public static Task CopyToAsync(Stream stream, Stream destination, int bufferSize) =>
        CopyToAsync(stream, destination, bufferSize, CancellationToken.None);

    /// <summary>Controlled <see cref="Stream.CopyToAsync(Stream, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="destination">The stream to copy to.</param>
    /// <param name="cancellationToken">The token which cancels the copy.</param>
    /// <returns>A task which completes when the copy has finished.</returns>
    public static Task CopyToAsync(Stream stream, Stream destination, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CopyToAsyncApi);
        return CopyToAsync(stream, destination, GetCopyBufferSize(stream), cancellationToken);
    }

    /// <summary>Controlled <see cref="Stream.ReadExactlyAsync(Memory{byte}, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The memory to fill.</param>
    /// <param name="cancellationToken">The token which cancels the reads.</param>
    /// <returns>A value task which completes when the buffer has been filled.</returns>
    public static ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadExactlyAsyncApi);
        RequireReceiver(stream);
        return Discard(ReadAtLeastAsyncCore(stream, buffer, buffer.Length, throwOnEndOfStream: true, cancellationToken));
    }

    /// <summary>Controlled <see cref="Stream.ReadExactlyAsync(byte[], int, int, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="offset">The offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The number of bytes to read.</param>
    /// <param name="cancellationToken">The token which cancels the reads.</param>
    /// <returns>A value task which completes when the bytes have been read.</returns>
    public static ValueTask ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadExactlyAsyncApi);
        RequireReceiver(stream);
        StreamAccess.ValidateBufferArguments(null, buffer, offset, count);
        return Discard(ReadAtLeastAsyncCore(stream, buffer.AsMemory(offset, count), count, throwOnEndOfStream: true, cancellationToken));
    }

    /// <summary>Controlled <see cref="Stream.ReadAtLeastAsync(Memory{byte}, int, bool, CancellationToken)"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The memory to read into.</param>
    /// <param name="minimumBytes">The minimum number of bytes to read.</param>
    /// <param name="throwOnEndOfStream">Whether reaching the end of the stream first throws.</param>
    /// <param name="cancellationToken">The token which cancels the reads.</param>
    /// <returns>A value task which completes with the number of bytes read.</returns>
    public static ValueTask<int> ReadAtLeastAsync(
        Stream stream,
        Memory<byte> buffer,
        int minimumBytes,
        bool throwOnEndOfStream,
        CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAtLeastAsyncApi);
        RequireReceiver(stream);
        StreamAccess.ValidateReadAtLeastArguments(null, buffer.Length, minimumBytes);
        return ReadAtLeastAsyncCore(stream, buffer, minimumBytes, throwOnEndOfStream, cancellationToken);
    }

    /// <summary>Controlled <see cref="Stream.BeginRead"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="offset">The offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <param name="callback">The callback to invoke once the read completes.</param>
    /// <param name="state">The state the result carries.</param>
    /// <returns>The pending read.</returns>
    public static IAsyncResult BeginRead(Stream stream, byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(BeginReadApi);
        return StreamOverrides.Of(stream).BeginRead
            ? stream.BeginRead(buffer, offset, count, callback, state)
            : BaseBeginRead(stream, buffer, offset, count, callback, state);
    }

    /// <summary>
    /// Controlled base call of <see cref="Stream.BeginRead"/>: waits, parked on the coordinator, for the stream's
    /// previous base operation to end, then reads with <see cref="Stream.Read(byte[], int, int)"/> in a controlled
    /// operation and invokes <paramref name="callback"/> there.
    /// </summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="offset">The offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <param name="callback">The callback to invoke once the read completes.</param>
    /// <param name="state">The state the result carries.</param>
    /// <returns>The pending read.</returns>
    public static IAsyncResult BaseBeginRead(Stream stream, byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(BeginReadApi);
        return BeginReadWrite(stream, buffer, offset, count, callback, state, isRead: true, apm: true, BeginReadApi);
    }

    /// <summary>Controlled <see cref="Stream.EndRead"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="asyncResult">The pending read.</param>
    /// <returns>The number of bytes read.</returns>
    public static int EndRead(Stream stream, IAsyncResult asyncResult)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EndReadApi);
        return StreamOverrides.Of(stream).EndRead
            ? stream.EndRead(asyncResult)
            : BaseEndRead(stream, asyncResult);
    }

    /// <summary>
    /// Controlled base call of <see cref="Stream.EndRead"/>: pumps the coordinator until the read completes, then releases
    /// the stream's gate.
    /// </summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="asyncResult">The pending read.</param>
    /// <returns>The number of bytes read.</returns>
    public static int BaseEndRead(Stream stream, IAsyncResult asyncResult)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EndReadApi);
        return EndReadWrite(stream, asyncResult, isRead: true, EndReadApi).GetAwaiter().GetResult();
    }

    /// <summary>Controlled <see cref="Stream.BeginWrite"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The buffer to write from.</param>
    /// <param name="offset">The offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <param name="callback">The callback to invoke once the write completes.</param>
    /// <param name="state">The state the result carries.</param>
    /// <returns>The pending write.</returns>
    public static IAsyncResult BeginWrite(Stream stream, byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(BeginWriteApi);
        return StreamOverrides.Of(stream).BeginWrite
            ? stream.BeginWrite(buffer, offset, count, callback, state)
            : BaseBeginWrite(stream, buffer, offset, count, callback, state);
    }

    /// <summary>Controlled base call of <see cref="Stream.BeginWrite"/>; see <see cref="BaseBeginRead"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="buffer">The buffer to write from.</param>
    /// <param name="offset">The offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <param name="callback">The callback to invoke once the write completes.</param>
    /// <param name="state">The state the result carries.</param>
    /// <returns>The pending write.</returns>
    public static IAsyncResult BaseBeginWrite(Stream stream, byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(BeginWriteApi);
        return BeginReadWrite(stream, buffer, offset, count, callback, state, isRead: false, apm: true, BeginWriteApi);
    }

    /// <summary>Controlled <see cref="Stream.EndWrite"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="asyncResult">The pending write.</param>
    public static void EndWrite(Stream stream, IAsyncResult asyncResult)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EndWriteApi);
        if (StreamOverrides.Of(stream).EndWrite)
        {
            stream.EndWrite(asyncResult);
        }
        else
        {
            BaseEndWrite(stream, asyncResult);
        }
    }

    /// <summary>Controlled base call of <see cref="Stream.EndWrite"/>; see <see cref="BaseEndRead"/>.</summary>
    /// <param name="stream">The receiving stream.</param>
    /// <param name="asyncResult">The pending write.</param>
    public static void BaseEndWrite(Stream stream, IAsyncResult asyncResult)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EndWriteApi);
        EndReadWrite(stream, asyncResult, isRead: false, EndWriteApi).GetAwaiter().GetResult();
    }

    // Stream.BeginReadInternal/BeginWriteInternal: the operation runs once the stream's gate admits it, which an XxAsync
    // call waits for asynchronously (serializeAsynchronously) and a BeginXx call synchronously, parked on the coordinator.
    private static Task<int> BeginReadWrite(
        Stream stream,
        byte[] buffer,
        int offset,
        int count,
        AsyncCallback? callback,
        object? state,
        bool isRead,
        bool apm,
        string apiName)
    {
        StreamAccess.ValidateBufferArguments(null, buffer, offset, count);
        if (isRead ? !stream.CanRead : !stream.CanWrite)
        {
            throw new NotSupportedException(isRead ? UnreadableStreamMessage : UnwritableStreamMessage);
        }

        SimulationExecutionSnapshot snapshot = SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        AsyncActiveGate gate = Gates.GetValue(stream, static _ => new AsyncActiveGate());
        var operation = new ReadWriteOperation(isRead, state);
        ExecutionContext? context = ExecutionContext.Capture();
        void Run() => SimulationTaskRuntime.QueueCapturedWork(
            snapshot,
            context,
            () =>
            {
                RaceSynchronization.Wait(gate);
                operation.Run(
                    () => isRead ? stream.Read(buffer, offset, count) : Write(stream, buffer, offset, count),
                    apm ? null : gate);

                // The BCL invokes the callback on the pool thread which completed the operation, after the task is
                // complete, concurrently with an EndXx which already observed the completion; a separate controlled
                // operation lets the scheduling strategy choose that order.
                if (callback is not null)
                {
                    SimulationTaskRuntime.QueueCapturedWork(snapshot, context, () => callback(operation.Task), apiName);
                }
            },
            apiName);

        if (apm)
        {
            Operations.Add(operation.Task, operation);
            gate.EnterAndWait(apiName);
            Run();
        }
        else
        {
            gate.Enter(Run);
        }

        return operation.Task;

        static int Write(Stream stream, byte[] buffer, int offset, int count)
        {
            stream.Write(buffer, offset, count);
            return 0;
        }
    }

    // Stream.EndRead/EndWrite: checks the result came from the matching base BeginXx and was not ended before, waits for
    // it on the coordinator, and releases the gate whatever the outcome.
    private static Task<int> EndReadWrite(Stream stream, IAsyncResult asyncResult, bool isRead, string apiName)
    {
        ArgumentNullException.ThrowIfNull(asyncResult);
        if (!Operations.TryGetValue(asyncResult, out ReadWriteOperation? operation) || operation.IsRead != isRead)
        {
            throw new ArgumentException(WrongAsyncResultMessage);
        }

        if (operation.EndCalled)
        {
            throw new InvalidOperationException(WrongAsyncResultMessage);
        }

        try
        {
            SimulationTaskRuntime.DrainUntil(() => operation.Task.IsCompleted, apiName, CancellationToken.None);
            RaceSynchronization.Wait(operation.Task);
            return operation.Task;
        }
        finally
        {
            operation.EndCalled = true;
            Gates.GetValue(stream, static _ => new AsyncActiveGate()).Exit();
        }
    }

    // TaskFactory.FromAsyncTrim over the stream's BeginRead/EndRead (or write pair), as the BCL's base ReadAsync and
    // WriteAsync complete through a stream which overrides them. The calls go through the controlled members, so a
    // half-overridden pair still reaches the controlled base of the other half.
    private static Task<int> FromAsync(Stream stream, byte[] buffer, int offset, int count, bool isRead)
    {
        var completion = new TaskCompletionSource<int>();
        void Complete(IAsyncResult asyncResult)
        {
            try
            {
                int result = isRead ? EndRead(stream, asyncResult) : EndWriteReturningZero(stream, asyncResult);
                SimulationCompletion.TrySetResult(completion, result, isRead ? ReadAsyncApi : WriteAsyncApi);
            }
            catch (OperationCanceledException exception)
            {
                SimulationCompletion.TrySetCanceled(completion, exception.CancellationToken, isRead ? ReadAsyncApi : WriteAsyncApi);
            }
            catch (Exception exception)
            {
                SimulationCompletion.TrySetException(completion, exception, isRead ? ReadAsyncApi : WriteAsyncApi);
            }

            RaceSynchronization.Signal(completion.Task);
        }

        AsyncCallback callback = asyncResult =>
        {
            if (!asyncResult.CompletedSynchronously)
            {
                Complete(asyncResult);
            }
        };
        IAsyncResult started = isRead
            ? BeginRead(stream, buffer, offset, count, callback, null)
            : BeginWrite(stream, buffer, offset, count, callback, null);
        if (started.CompletedSynchronously)
        {
            Complete(started);
        }

        return completion.Task;

        static int EndWriteReturningZero(Stream stream, IAsyncResult asyncResult)
        {
            EndWrite(stream, asyncResult);
            return 0;
        }
    }

    private static async ValueTask<int> ReadAtLeastAsyncCore(
        Stream stream,
        Memory<byte> buffer,
        int minimumBytes,
        bool throwOnEndOfStream,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < minimumBytes)
        {
            int read = await new CoordinatedValueTaskAwaitable<int>(ReadAsync(stream, buffer.Slice(totalRead), cancellationToken));
            if (read == 0)
            {
                if (throwOnEndOfStream)
                {
                    throw new EndOfStreamException(ReadBeyondEndMessage);
                }

                return totalRead;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static async ValueTask Discard(ValueTask<int> read) => await new CoordinatedValueTaskAwaitable<int>(read);

    // Stream.GetCopyBufferSize.
    private static int GetCopyBufferSize(Stream stream)
    {
        int bufferSize = DefaultCopyBufferSize;
        if (stream.CanSeek)
        {
            long length = stream.Length;
            long position = stream.Position;
            if (length <= position)
            {
                bufferSize = 1;
            }
            else
            {
                long remaining = length - position;
                if (remaining > 0)
                {
                    bufferSize = (int)Math.Min(bufferSize, remaining);
                }
            }
        }

        return bufferSize;
    }

    // A null receiver throws NullReferenceException before anything else, as the BCL's callvirt does.
    private static void RequireReceiver(Stream stream) => _ = stream.GetType();

    private static CoordinatedValueTaskAwaitable<int> Coordinated(Task<int> task) => new(new ValueTask<int>(task));

    private static CoordinatedValueTaskAwaitable Coordinated(Task task) => new(new ValueTask(task));

    // The serialization of a stream's base reads and writes (the BCL's _asyncActiveSemaphore, a SemaphoreSlim(1, 1)):
    // one operation holds it at a time, and waiters, asynchronous and synchronous alike, enter in FIFO order. Only the
    // logical thread touches it.
    private sealed class AsyncActiveGate
    {
        private readonly Queue<Action> _waiters = new();
        private bool _held;

        // Runs `entered` once the gate admits the caller; an asynchronous waiter's `entered` queues its operation.
        public void Enter(Action entered)
        {
            if (_held)
            {
                _waiters.Enqueue(entered);
                return;
            }

            _held = true;
            entered();
        }

        public void EnterAndWait(string apiName)
        {
            bool admitted = false;
            Enter(() => admitted = true);
            if (!admitted)
            {
                SimulationTaskRuntime.DrainUntil(() => admitted, apiName, CancellationToken.None);
            }
        }

        public void Exit()
        {
            if (!_held)
            {
                // SemaphoreSlim(1, 1).Release on a free semaphore.
                throw new SemaphoreFullException();
            }

            RaceSynchronization.Signal(this);
            if (_waiters.TryDequeue(out Action? next))
            {
                next();
            }
            else
            {
                _held = false;
            }
        }
    }

    // A base read or write: its task is the IAsyncResult BeginXx returns, carrying the caller's state, as the BCL's
    // ReadWriteTask does.
    private sealed class ReadWriteOperation(bool isRead, object? state)
    {
        private readonly TaskCompletionSource<int> _completion = new(state);

        public bool IsRead { get; } = isRead;

        public bool EndCalled { get; set; }

        public Task<int> Task => _completion.Task;

        // Runs the synchronous read or write, releases the gate (for an XxAsync operation; EndXx releases a BeginXx's),
        // and completes the task.
        public void Run(Func<int> body, AsyncActiveGate? release)
        {
            int result = 0;
            Exception? failure = null;
            try
            {
                result = body();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                release?.Exit();
            }

            if (failure is null)
            {
                SimulationCompletion.TrySetResult(_completion, result, IsRead ? ReadAsyncApi : WriteAsyncApi);
            }
            else
            {
                SimulationCompletion.TrySetException(_completion, failure, IsRead ? ReadAsyncApi : WriteAsyncApi);
            }

            RaceSynchronization.Signal(_completion.Task);
        }
    }

    // The protected and private static validation of Stream, which raises the BCL's exceptions and messages.
    private static class StreamAccess
    {
        [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ValidateBufferArguments")]
        public static extern void ValidateBufferArguments(Stream? type, byte[] buffer, int offset, int count);

        [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ValidateCopyToArguments")]
        public static extern void ValidateCopyToArguments(Stream? type, Stream destination, int bufferSize);

        [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ValidateReadAtLeastArguments")]
        public static extern void ValidateReadAtLeastArguments(Stream? type, int bufferLength, int minimumBytes);
    }

    // Records, per stream type, which asynchronous members it overrides. A delegate bound to a virtual member binds to the
    // most derived override, whose declaring type names the class which supplies it; the BCL's own check for the
    // Begin/End pairs (Stream.HasOverriddenBeginEndRead) asks the same of either member of a pair.
    private sealed class StreamOverrides
    {
        private static readonly ConditionalWeakTable<Type, StreamOverrides> Cache = [];

        private StreamOverrides(Stream stream)
        {
            ReadAsyncArray = Overrides((Func<byte[], int, int, CancellationToken, Task<int>>)stream.ReadAsync);
            ReadAsyncMemory = Overrides((Func<Memory<byte>, CancellationToken, ValueTask<int>>)stream.ReadAsync);
            WriteAsyncArray = Overrides((Func<byte[], int, int, CancellationToken, Task>)stream.WriteAsync);
            WriteAsyncMemory = Overrides((Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>)stream.WriteAsync);
            FlushAsync = Overrides((Func<CancellationToken, Task>)stream.FlushAsync);
            CopyToAsync = Overrides((Func<Stream, int, CancellationToken, Task>)stream.CopyToAsync);
            BeginRead = Overrides((Func<byte[], int, int, AsyncCallback?, object?, IAsyncResult>)stream.BeginRead);
            EndRead = Overrides((Func<IAsyncResult, int>)stream.EndRead);
            BeginWrite = Overrides((Func<byte[], int, int, AsyncCallback?, object?, IAsyncResult>)stream.BeginWrite);
            EndWrite = Overrides((Action<IAsyncResult>)stream.EndWrite);
        }

        public bool ReadAsyncArray { get; }

        public bool ReadAsyncMemory { get; }

        public bool WriteAsyncArray { get; }

        public bool WriteAsyncMemory { get; }

        public bool FlushAsync { get; }

        public bool CopyToAsync { get; }

        public bool BeginRead { get; }

        public bool EndRead { get; }

        public bool BeginWrite { get; }

        public bool EndWrite { get; }

        public bool BeginEndRead => BeginRead || EndRead;

        public bool BeginEndWrite => BeginWrite || EndWrite;

        public static StreamOverrides Of(Stream stream) =>
            Cache.TryGetValue(stream.GetType(), out StreamOverrides? overrides) ? overrides : Record(stream);

        private static bool Overrides(Delegate member) => member.Method.DeclaringType != typeof(Stream);

        // Separate from Of so that a cache hit allocates no closure over the stream.
        private static StreamOverrides Record(Stream stream) =>
            Cache.GetValue(stream.GetType(), _ => new StreamOverrides(stream));
    }
}
