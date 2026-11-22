using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Shims.System.IO;

/// <summary>
/// Controlled asynchronous members of <see cref="TextReader"/>. The BCL's base implementations, which a reader that does
/// not override them inherits, run the synchronous <see cref="TextReader.ReadLine"/>, <see cref="TextReader.Read(Span{char})"/>,
/// or <see cref="TextReader.ReadBlock(Span{char})"/> on a thread-pool thread, or loop over such reads through BCL
/// awaits. The rewriter redirects the call sites here, receiver first. A reader which overrides a member is called as
/// written; otherwise the shim runs the BCL's base algorithm with the same calls on the reader, each synchronous read as a
/// fresh controlled operation concurrent with the caller, as on the thread pool, awaited on the coordinator. An
/// override's base call runs the base algorithm through the <c>Base</c> shim.
/// </summary>
/// <remarks>
/// The base <c>ReadToEndAsync</c>, <c>ReadAsync(char[], int, int)</c>, and <c>ReadBlockAsync(char[], int, int)</c> read
/// through the internal virtual <c>TextReader.ReadAsyncInternal</c>, whose base runs <see cref="TextReader.Read(Span{char})"/>
/// on the thread pool. Only <see cref="StreamReader"/> overrides it, and it overrides each of those public members too, so
/// a reader whose public member reaches the base algorithm here reads through the base <c>ReadAsyncInternal</c>.
/// </remarks>
public static class ControlledTextReader
{
    private const string TypeName = "System.IO.TextReader.";
    private const string ReadLineAsyncApi = TypeName + "ReadLineAsync";
    private const string ReadToEndAsyncApi = TypeName + "ReadToEndAsync";
    private const string ReadAsyncApi = TypeName + "ReadAsync";
    private const string ReadBlockAsyncApi = TypeName + "ReadBlockAsync";

    // The BCL's message (SR.Argument_InvalidOffLen), which StreamConformanceTests compares with the BCL's own exception.
    private const string InvalidOffLenMessage =
        "Offset and length were out of bounds for the array or count is greater than the number of elements from index to " +
        "the end of the source collection.";

    /// <summary>Controlled <see cref="TextReader.ReadLineAsync()"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <returns>A task which completes with the line read, or <see langword="null"/> at the end.</returns>
    public static Task<string?> ReadLineAsync(TextReader reader)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadLineAsyncApi);
        return ReaderOverrides.Of(reader).ReadLineAsync ? reader.ReadLineAsync() : BaseReadLineAsync(reader);
    }

    /// <summary>Controlled base call of <see cref="TextReader.ReadLineAsync()"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <returns>A task which completes with the line read, or <see langword="null"/> at the end.</returns>
    public static Task<string?> BaseReadLineAsync(TextReader reader)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadLineAsyncApi);
        return IoWorker.Start(reader.ReadLine, ReadLineAsyncApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="TextReader.ReadLineAsync(CancellationToken)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="cancellationToken">The token which cancels the read before it starts.</param>
    /// <returns>A value task which completes with the line read, or <see langword="null"/> at the end.</returns>
    public static ValueTask<string?> ReadLineAsync(TextReader reader, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadLineAsyncApi);
        return ReaderOverrides.Of(reader).ReadLineAsyncToken
            ? reader.ReadLineAsync(cancellationToken)
            : BaseReadLineAsync(reader, cancellationToken);
    }

    /// <summary>Controlled base call of <see cref="TextReader.ReadLineAsync(CancellationToken)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="cancellationToken">The token which cancels the read before it starts.</param>
    /// <returns>A value task which completes with the line read, or <see langword="null"/> at the end.</returns>
    public static ValueTask<string?> BaseReadLineAsync(TextReader reader, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadLineAsyncApi);
        return new ValueTask<string?>(IoWorker.Start(reader.ReadLine, ReadLineAsyncApi, cancellationToken));
    }

    /// <summary>Controlled <see cref="TextReader.ReadToEndAsync()"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <returns>A task which completes with the rest of the text.</returns>
    public static Task<string> ReadToEndAsync(TextReader reader)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadToEndAsyncApi);
        return ReaderOverrides.Of(reader).ReadToEndAsync ? reader.ReadToEndAsync() : BaseReadToEndAsync(reader);
    }

    /// <summary>Controlled base call of <see cref="TextReader.ReadToEndAsync()"/>, which calls <c>ReadToEndAsync(default)</c>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <returns>A task which completes with the rest of the text.</returns>
    public static Task<string> BaseReadToEndAsync(TextReader reader)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadToEndAsyncApi);
        return ReadToEndAsync(reader, default);
    }

    /// <summary>Controlled <see cref="TextReader.ReadToEndAsync(CancellationToken)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="cancellationToken">The token which cancels the reads.</param>
    /// <returns>A task which completes with the rest of the text.</returns>
    public static Task<string> ReadToEndAsync(TextReader reader, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadToEndAsyncApi);
        return ReaderOverrides.Of(reader).ReadToEndAsyncToken
            ? reader.ReadToEndAsync(cancellationToken)
            : BaseReadToEndAsync(reader, cancellationToken);
    }

    /// <summary>Controlled base call of <see cref="TextReader.ReadToEndAsync(CancellationToken)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="cancellationToken">The token which cancels the reads.</param>
    /// <returns>A task which completes with the rest of the text.</returns>
    public static Task<string> BaseReadToEndAsync(TextReader reader, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadToEndAsyncApi);
        return ReadToEndCore(reader, cancellationToken);
    }

    // TextReader.ReadToEndAsync(CancellationToken), an async method, so every exception faults the task.
    private static async Task<string> ReadToEndCore(TextReader reader, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(4096);
        char[] chars = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            int len;
            while ((len = await Coordinated(ReadAsyncInternal(reader, chars, cancellationToken))) != 0)
            {
                sb.Append(chars, 0, len);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }

        return sb.ToString();
    }

    /// <summary>Controlled <see cref="TextReader.ReadAsync(char[], int, int)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="index">The index in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The maximum number of characters to read.</param>
    /// <returns>A task which completes with the number of characters read.</returns>
    public static Task<int> ReadAsync(TextReader reader, char[] buffer, int index, int count)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAsyncApi);
        return ReaderOverrides.Of(reader).ReadAsyncArray
            ? reader.ReadAsync(buffer, index, count)
            : BaseReadAsync(reader, buffer, index, count);
    }

    /// <summary>Controlled base call of <see cref="TextReader.ReadAsync(char[], int, int)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="index">The index in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The maximum number of characters to read.</param>
    /// <returns>A task which completes with the number of characters read.</returns>
    public static Task<int> BaseReadAsync(TextReader reader, char[] buffer, int index, int count)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAsyncApi);
        ValidateBuffer(buffer, index, count);
        return ReadAsyncInternal(reader, new Memory<char>(buffer, index, count), default);
    }

    /// <summary>Controlled <see cref="TextReader.ReadAsync(Memory{char}, CancellationToken)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="buffer">The memory to read into.</param>
    /// <param name="cancellationToken">The token which cancels the read before it starts.</param>
    /// <returns>A value task which completes with the number of characters read.</returns>
    public static ValueTask<int> ReadAsync(TextReader reader, Memory<char> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAsyncApi);
        return ReaderOverrides.Of(reader).ReadAsyncMemory
            ? reader.ReadAsync(buffer, cancellationToken)
            : BaseReadAsync(reader, buffer, cancellationToken);
    }

    /// <summary>Controlled base call of <see cref="TextReader.ReadAsync(Memory{char}, CancellationToken)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="buffer">The memory to read into.</param>
    /// <param name="cancellationToken">The token which cancels the read before it starts.</param>
    /// <returns>A value task which completes with the number of characters read.</returns>
    public static ValueTask<int> BaseReadAsync(TextReader reader, Memory<char> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAsyncApi);

        // As in the BCL, an array-backed buffer reads through ReadAsync(char[], int, int), which takes no token.
        return new ValueTask<int>(MemoryMarshal.TryGetArray(buffer, out ArraySegment<char> array)
            ? ReadAsync(reader, array.Array!, array.Offset, array.Count)
            : IoWorker.Start(() => reader.Read(buffer.Span), ReadAsyncApi, cancellationToken));
    }

    /// <summary>Controlled <see cref="TextReader.ReadBlockAsync(char[], int, int)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="index">The index in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The maximum number of characters to read.</param>
    /// <returns>A task which completes with the number of characters read.</returns>
    public static Task<int> ReadBlockAsync(TextReader reader, char[] buffer, int index, int count)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadBlockAsyncApi);
        return ReaderOverrides.Of(reader).ReadBlockAsyncArray
            ? reader.ReadBlockAsync(buffer, index, count)
            : BaseReadBlockAsync(reader, buffer, index, count);
    }

    /// <summary>Controlled base call of <see cref="TextReader.ReadBlockAsync(char[], int, int)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="index">The index in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The maximum number of characters to read.</param>
    /// <returns>A task which completes with the number of characters read.</returns>
    public static Task<int> BaseReadBlockAsync(TextReader reader, char[] buffer, int index, int count)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadBlockAsyncApi);
        ValidateBuffer(buffer, index, count);
        return ReadBlockAsyncInternal(reader, new Memory<char>(buffer, index, count));
    }

    /// <summary>Controlled <see cref="TextReader.ReadBlockAsync(Memory{char}, CancellationToken)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="buffer">The memory to fill.</param>
    /// <param name="cancellationToken">The token which cancels the read before it starts.</param>
    /// <returns>A value task which completes with the number of characters read.</returns>
    public static ValueTask<int> ReadBlockAsync(TextReader reader, Memory<char> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadBlockAsyncApi);
        return ReaderOverrides.Of(reader).ReadBlockAsyncMemory
            ? reader.ReadBlockAsync(buffer, cancellationToken)
            : BaseReadBlockAsync(reader, buffer, cancellationToken);
    }

    /// <summary>Controlled base call of <see cref="TextReader.ReadBlockAsync(Memory{char}, CancellationToken)"/>.</summary>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="buffer">The memory to fill.</param>
    /// <param name="cancellationToken">The token which cancels the read before it starts.</param>
    /// <returns>A value task which completes with the number of characters read.</returns>
    public static ValueTask<int> BaseReadBlockAsync(TextReader reader, Memory<char> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadBlockAsyncApi);
        return new ValueTask<int>(MemoryMarshal.TryGetArray(buffer, out ArraySegment<char> array)
            ? ReadBlockAsync(reader, array.Array!, array.Offset, array.Count)
            : IoWorker.Start(() => reader.ReadBlock(buffer.Span), ReadBlockAsyncApi, cancellationToken));
    }

    // The base TextReader.ReadAsyncInternal: Read(Span<char>) on the thread pool.
    private static Task<int> ReadAsyncInternal(TextReader reader, Memory<char> buffer, CancellationToken cancellationToken) =>
        IoWorker.Start(() => reader.Read(buffer.Span), ReadAsyncApi, cancellationToken);

    // TextReader.ReadBlockAsyncInternal, which the char[] overload calls without a token.
    private static async Task<int> ReadBlockAsyncInternal(TextReader reader, Memory<char> buffer)
    {
        int n = 0, i;
        do
        {
            i = await Coordinated(ReadAsyncInternal(reader, buffer.Slice(n), default));
            n += i;
        }
        while (i > 0 && n < buffer.Length);

        return n;
    }

    private static void ValidateBuffer(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - index < count)
        {
            throw new ArgumentException(InvalidOffLenMessage);
        }
    }

    private static CoordinatedValueTaskAwaitable<int> Coordinated(Task<int> task) => new(new ValueTask<int>(task));

    // Records, per reader type, which asynchronous members it overrides; see ControlledStream.
    private sealed class ReaderOverrides
    {
        private static readonly ConditionalWeakTable<Type, ReaderOverrides> Cache = [];

        private ReaderOverrides(TextReader reader)
        {
            ReadLineAsync = Overrides((Func<Task<string?>>)reader.ReadLineAsync);
            ReadLineAsyncToken = Overrides((Func<CancellationToken, ValueTask<string?>>)reader.ReadLineAsync);
            ReadToEndAsync = Overrides((Func<Task<string>>)reader.ReadToEndAsync);
            ReadToEndAsyncToken = Overrides((Func<CancellationToken, Task<string>>)reader.ReadToEndAsync);
            ReadAsyncArray = Overrides((Func<char[], int, int, Task<int>>)reader.ReadAsync);
            ReadAsyncMemory = Overrides((Func<Memory<char>, CancellationToken, ValueTask<int>>)reader.ReadAsync);
            ReadBlockAsyncArray = Overrides((Func<char[], int, int, Task<int>>)reader.ReadBlockAsync);
            ReadBlockAsyncMemory = Overrides((Func<Memory<char>, CancellationToken, ValueTask<int>>)reader.ReadBlockAsync);
        }

        public bool ReadLineAsync { get; }

        public bool ReadLineAsyncToken { get; }

        public bool ReadToEndAsync { get; }

        public bool ReadToEndAsyncToken { get; }

        public bool ReadAsyncArray { get; }

        public bool ReadAsyncMemory { get; }

        public bool ReadBlockAsyncArray { get; }

        public bool ReadBlockAsyncMemory { get; }

        public static ReaderOverrides Of(TextReader reader) =>
            Cache.TryGetValue(reader.GetType(), out ReaderOverrides? overrides) ? overrides : Record(reader);

        private static bool Overrides(Delegate member) => member.Method.DeclaringType != typeof(TextReader);

        // Separate from Of so that a cache hit allocates no closure over the reader.
        private static ReaderOverrides Record(TextReader reader) =>
            Cache.GetValue(reader.GetType(), _ => new ReaderOverrides(reader));
    }
}
