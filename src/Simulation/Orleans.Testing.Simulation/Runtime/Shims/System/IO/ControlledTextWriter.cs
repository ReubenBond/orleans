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
/// Controlled asynchronous members of <see cref="TextWriter"/>. The BCL's base implementations, which a writer that does
/// not override them inherits, run the synchronous <c>Write</c>, <c>WriteLine</c>, or <see cref="TextWriter.Flush"/> on a
/// thread-pool thread, or loop over the other asynchronous members through BCL awaits. The rewriter redirects the call
/// sites here, receiver first. A writer which overrides a member is called as written; otherwise the shim runs the BCL's
/// base algorithm with the same calls on the writer, each synchronous write as a fresh controlled operation concurrent
/// with the caller, as on the thread pool, awaited on the coordinator. An override's base call runs the base algorithm
/// through the <c>Base</c> shim. The non-virtual overloads forward to the controlled virtual members as the BCL's do.
/// </summary>
public static class ControlledTextWriter
{
    private const string TypeName = "System.IO.TextWriter.";
    private const string WriteAsyncApi = TypeName + "WriteAsync";
    private const string WriteLineAsyncApi = TypeName + "WriteLineAsync";
    private const string FlushAsyncApi = TypeName + "FlushAsync";

    /// <summary>Controlled <see cref="TextWriter.WriteAsync(char)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The character to write.</param>
    /// <returns>A task which completes when the character has been written.</returns>
    public static Task WriteAsync(TextWriter writer, char value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return WriterOverrides.Of(writer).WriteAsyncChar ? writer.WriteAsync(value) : BaseWriteAsync(writer, value);
    }

    /// <summary>Controlled base call of <see cref="TextWriter.WriteAsync(char)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The character to write.</param>
    /// <returns>A task which completes when the character has been written.</returns>
    public static Task BaseWriteAsync(TextWriter writer, char value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return IoWorker.Start(() => writer.Write(value), WriteAsyncApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteAsync(Rune)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The rune to write.</param>
    /// <returns>A task which completes when the rune has been written.</returns>
    public static Task WriteAsync(TextWriter writer, Rune value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return WriterOverrides.Of(writer).WriteAsyncRune ? writer.WriteAsync(value) : BaseWriteAsync(writer, value);
    }

    /// <summary>
    /// Controlled base call of <see cref="TextWriter.WriteAsync(Rune)"/>: a surrogate pair is written by one controlled
    /// operation, and any other rune through <see cref="TextWriter.WriteAsync(char)"/>.
    /// </summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The rune to write.</param>
    /// <returns>A task which completes when the rune has been written.</returns>
    public static Task BaseWriteAsync(TextWriter writer, Rune value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        (char first, char? second) = Chars(value);
        return second is { } low
            ? IoWorker.Start(
                () =>
                {
                    writer.Write(first);
                    writer.Write(low);
                },
                WriteAsyncApi,
                CancellationToken.None)
            : WriteAsync(writer, first);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteAsync(string)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The string to write.</param>
    /// <returns>A task which completes when the string has been written.</returns>
    public static Task WriteAsync(TextWriter writer, string? value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return WriterOverrides.Of(writer).WriteAsyncString ? writer.WriteAsync(value) : BaseWriteAsync(writer, value);
    }

    /// <summary>Controlled base call of <see cref="TextWriter.WriteAsync(string)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The string to write.</param>
    /// <returns>A task which completes when the string has been written.</returns>
    public static Task BaseWriteAsync(TextWriter writer, string? value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return IoWorker.Start(() => writer.Write(value), WriteAsyncApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteAsync(StringBuilder, CancellationToken)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The text to write.</param>
    /// <param name="cancellationToken">The token which cancels the writes.</param>
    /// <returns>A task which completes when the text has been written.</returns>
    public static Task WriteAsync(TextWriter writer, StringBuilder? value, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return WriterOverrides.Of(writer).WriteAsyncStringBuilder
            ? writer.WriteAsync(value, cancellationToken)
            : BaseWriteAsync(writer, value, cancellationToken);
    }

    /// <summary>
    /// Controlled base call of <see cref="TextWriter.WriteAsync(StringBuilder, CancellationToken)"/>: each chunk through
    /// <see cref="TextWriter.WriteAsync(ReadOnlyMemory{char}, CancellationToken)"/>.
    /// </summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The text to write.</param>
    /// <param name="cancellationToken">The token which cancels the writes.</param>
    /// <returns>A task which completes when the text has been written.</returns>
    public static Task BaseWriteAsync(TextWriter writer, StringBuilder? value, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return
            cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) :
            value == null ? Task.CompletedTask :
            WriteChunksAsync(writer, value, writeNewLine: false, cancellationToken);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteAsync(char[], int, int)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="buffer">The characters to write.</param>
    /// <param name="index">The index in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The number of characters to write.</param>
    /// <returns>A task which completes when the characters have been written.</returns>
    public static Task WriteAsync(TextWriter writer, char[] buffer, int index, int count)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return WriterOverrides.Of(writer).WriteAsyncArray
            ? writer.WriteAsync(buffer, index, count)
            : BaseWriteAsync(writer, buffer, index, count);
    }

    /// <summary>Controlled base call of <see cref="TextWriter.WriteAsync(char[], int, int)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="buffer">The characters to write.</param>
    /// <param name="index">The index in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The number of characters to write.</param>
    /// <returns>A task which completes when the characters have been written.</returns>
    public static Task BaseWriteAsync(TextWriter writer, char[] buffer, int index, int count)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return IoWorker.Start(() => writer.Write(buffer, index, count), WriteAsyncApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteAsync(ReadOnlyMemory{char}, CancellationToken)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="buffer">The characters to write.</param>
    /// <param name="cancellationToken">The token which cancels the write before it starts.</param>
    /// <returns>A task which completes when the characters have been written.</returns>
    public static Task WriteAsync(TextWriter writer, ReadOnlyMemory<char> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return WriterOverrides.Of(writer).WriteAsyncMemory
            ? writer.WriteAsync(buffer, cancellationToken)
            : BaseWriteAsync(writer, buffer, cancellationToken);
    }

    /// <summary>
    /// Controlled base call of <see cref="TextWriter.WriteAsync(ReadOnlyMemory{char}, CancellationToken)"/>: an
    /// array-backed buffer through <see cref="TextWriter.WriteAsync(char[], int, int)"/>, any other in one controlled
    /// operation.
    /// </summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="buffer">The characters to write.</param>
    /// <param name="cancellationToken">The token which cancels the write before it starts.</param>
    /// <returns>A task which completes when the characters have been written.</returns>
    public static Task BaseWriteAsync(TextWriter writer, ReadOnlyMemory<char> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return
            cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) :
            MemoryMarshal.TryGetArray(buffer, out ArraySegment<char> array) ? WriteAsync(writer, array.Array!, array.Offset, array.Count) :
            IoWorker.Start(() => writer.Write(buffer.Span), WriteAsyncApi, cancellationToken);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteAsync(string, CancellationToken)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The string to write.</param>
    /// <param name="cancellationToken">The token which cancels the write before it starts.</param>
    /// <returns>A task which completes when the string has been written.</returns>
    public static Task WriteAsync(TextWriter writer, string? value, CancellationToken cancellationToken) =>
        WriteAsync(writer, value.AsMemory(), cancellationToken);

    /// <summary>Controlled <see cref="TextWriter.WriteAsync(char[])"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="buffer">The characters to write.</param>
    /// <returns>A task which completes when the characters have been written.</returns>
    public static Task WriteAsync(TextWriter writer, char[]? buffer)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        RequireReceiver(writer);
        return buffer == null ? Task.CompletedTask : WriteAsync(writer, buffer, 0, buffer.Length);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteLineAsync(char)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The character to write.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task WriteLineAsync(TextWriter writer, char value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return WriterOverrides.Of(writer).WriteLineAsyncChar ? writer.WriteLineAsync(value) : BaseWriteLineAsync(writer, value);
    }

    /// <summary>Controlled base call of <see cref="TextWriter.WriteLineAsync(char)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The character to write.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task BaseWriteLineAsync(TextWriter writer, char value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return IoWorker.Start(() => writer.WriteLine(value), WriteLineAsyncApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteLineAsync(Rune)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The rune to write.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task WriteLineAsync(TextWriter writer, Rune value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return WriterOverrides.Of(writer).WriteLineAsyncRune ? writer.WriteLineAsync(value) : BaseWriteLineAsync(writer, value);
    }

    /// <summary>Controlled base call of <see cref="TextWriter.WriteLineAsync(Rune)"/>; see <see cref="BaseWriteAsync(TextWriter, Rune)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The rune to write.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task BaseWriteLineAsync(TextWriter writer, Rune value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        (char first, char? second) = Chars(value);
        return second is { } low
            ? IoWorker.Start(
                () =>
                {
                    writer.Write(first);
                    writer.WriteLine(low);
                },
                WriteLineAsyncApi,
                CancellationToken.None)
            : WriteLineAsync(writer, first);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteLineAsync(string)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The string to write.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task WriteLineAsync(TextWriter writer, string? value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return WriterOverrides.Of(writer).WriteLineAsyncString ? writer.WriteLineAsync(value) : BaseWriteLineAsync(writer, value);
    }

    /// <summary>Controlled base call of <see cref="TextWriter.WriteLineAsync(string)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The string to write.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task BaseWriteLineAsync(TextWriter writer, string? value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return IoWorker.Start(() => writer.WriteLine(value), WriteLineAsyncApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteLineAsync(StringBuilder, CancellationToken)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The text to write.</param>
    /// <param name="cancellationToken">The token which cancels the writes.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task WriteLineAsync(TextWriter writer, StringBuilder? value, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return WriterOverrides.Of(writer).WriteLineAsyncStringBuilder
            ? writer.WriteLineAsync(value, cancellationToken)
            : BaseWriteLineAsync(writer, value, cancellationToken);
    }

    /// <summary>
    /// Controlled base call of <see cref="TextWriter.WriteLineAsync(StringBuilder, CancellationToken)"/>: each chunk, and
    /// then the line terminator, through <see cref="TextWriter.WriteAsync(ReadOnlyMemory{char}, CancellationToken)"/>.
    /// </summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The text to write.</param>
    /// <param name="cancellationToken">The token which cancels the writes.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task BaseWriteLineAsync(TextWriter writer, StringBuilder? value, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return
            cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) :
            value == null ? WriteAsync(writer, CoreNewLine(writer), cancellationToken) :
            WriteChunksAsync(writer, value, writeNewLine: true, cancellationToken);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteLineAsync(char[], int, int)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="buffer">The characters to write.</param>
    /// <param name="index">The index in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The number of characters to write.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task WriteLineAsync(TextWriter writer, char[] buffer, int index, int count)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return WriterOverrides.Of(writer).WriteLineAsyncArray
            ? writer.WriteLineAsync(buffer, index, count)
            : BaseWriteLineAsync(writer, buffer, index, count);
    }

    /// <summary>Controlled base call of <see cref="TextWriter.WriteLineAsync(char[], int, int)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="buffer">The characters to write.</param>
    /// <param name="index">The index in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">The number of characters to write.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task BaseWriteLineAsync(TextWriter writer, char[] buffer, int index, int count)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return IoWorker.Start(() => writer.WriteLine(buffer, index, count), WriteLineAsyncApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteLineAsync(ReadOnlyMemory{char}, CancellationToken)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="buffer">The characters to write.</param>
    /// <param name="cancellationToken">The token which cancels the write before it starts.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task WriteLineAsync(TextWriter writer, ReadOnlyMemory<char> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return WriterOverrides.Of(writer).WriteLineAsyncMemory
            ? writer.WriteLineAsync(buffer, cancellationToken)
            : BaseWriteLineAsync(writer, buffer, cancellationToken);
    }

    /// <summary>Controlled base call of <see cref="TextWriter.WriteLineAsync(ReadOnlyMemory{char}, CancellationToken)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="buffer">The characters to write.</param>
    /// <param name="cancellationToken">The token which cancels the write before it starts.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task BaseWriteLineAsync(TextWriter writer, ReadOnlyMemory<char> buffer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return
            cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) :
            MemoryMarshal.TryGetArray(buffer, out ArraySegment<char> array) ? WriteLineAsync(writer, array.Array!, array.Offset, array.Count) :
            IoWorker.Start(() => writer.WriteLine(buffer.Span), WriteLineAsyncApi, cancellationToken);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteLineAsync()"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <returns>A task which completes when the line terminator has been written.</returns>
    public static Task WriteLineAsync(TextWriter writer)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return WriterOverrides.Of(writer).WriteLineAsyncEmpty ? writer.WriteLineAsync() : BaseWriteLineAsync(writer);
    }

    /// <summary>
    /// Controlled base call of <see cref="TextWriter.WriteLineAsync()"/>: the line terminator through
    /// <see cref="TextWriter.WriteAsync(char[])"/>.
    /// </summary>
    /// <param name="writer">The receiving writer.</param>
    /// <returns>A task which completes when the line terminator has been written.</returns>
    public static Task BaseWriteLineAsync(TextWriter writer)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        return WriteAsync(writer, CoreNewLineField(writer));
    }

    /// <summary>Controlled <see cref="TextWriter.WriteLineAsync(string, CancellationToken)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="value">The string to write.</param>
    /// <param name="cancellationToken">The token which cancels the write before it starts.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task WriteLineAsync(TextWriter writer, string? value, CancellationToken cancellationToken) =>
        WriteLineAsync(writer, value.AsMemory(), cancellationToken);

    /// <summary>Controlled <see cref="TextWriter.WriteLineAsync(char[])"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="buffer">The characters to write.</param>
    /// <returns>A task which completes when the line has been written.</returns>
    public static Task WriteLineAsync(TextWriter writer, char[]? buffer)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        RequireReceiver(writer);
        return buffer == null ? WriteLineAsync(writer) : WriteLineAsync(writer, buffer, 0, buffer.Length);
    }

    /// <summary>Controlled <see cref="TextWriter.WriteLineAsync(CancellationToken)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="cancellationToken">The token which cancels the write before it starts.</param>
    /// <returns>A task which completes when the line terminator has been written.</returns>
    public static Task WriteLineAsync(TextWriter writer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteLineAsyncApi);
        RequireReceiver(writer);
        return WriteAsync(writer, CoreNewLine(writer), cancellationToken);
    }

    /// <summary>Controlled <see cref="TextWriter.FlushAsync()"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <returns>A task which completes when the writer has been flushed.</returns>
    public static Task FlushAsync(TextWriter writer)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(FlushAsyncApi);
        return WriterOverrides.Of(writer).FlushAsync ? writer.FlushAsync() : BaseFlushAsync(writer);
    }

    /// <summary>Controlled base call of <see cref="TextWriter.FlushAsync()"/>: a controlled operation which calls <see cref="TextWriter.Flush"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <returns>A task which completes when the writer has been flushed.</returns>
    public static Task BaseFlushAsync(TextWriter writer)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(FlushAsyncApi);
        return IoWorker.Start(writer.Flush, FlushAsyncApi, CancellationToken.None);
    }

    /// <summary>Controlled <see cref="TextWriter.FlushAsync(CancellationToken)"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="cancellationToken">The token which cancels the flush before it starts.</param>
    /// <returns>A task which completes when the writer has been flushed.</returns>
    public static Task FlushAsync(TextWriter writer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(FlushAsyncApi);
        return WriterOverrides.Of(writer).FlushAsyncToken
            ? writer.FlushAsync(cancellationToken)
            : BaseFlushAsync(writer, cancellationToken);
    }

    /// <summary>Controlled base call of <see cref="TextWriter.FlushAsync(CancellationToken)"/>, which calls <see cref="TextWriter.FlushAsync()"/>.</summary>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="cancellationToken">The token which cancels the flush before it starts.</param>
    /// <returns>A task which completes when the writer has been flushed.</returns>
    public static Task BaseFlushAsync(TextWriter writer, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(FlushAsyncApi);
        return cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : FlushAsyncWithoutToken(writer);
    }

    // The BCL's FlushAsync(CancellationToken) calls FlushAsync(), which takes no token.
    private static Task FlushAsyncWithoutToken(TextWriter writer) => FlushAsync(writer);

    // TextWriter.WriteAsync(StringBuilder)'s and WriteLineAsync(StringBuilder)'s async loops.
    private static async Task WriteChunksAsync(TextWriter writer, StringBuilder value, bool writeNewLine, CancellationToken cancellationToken)
    {
        foreach (ReadOnlyMemory<char> chunk in value.GetChunks())
        {
            await Coordinated(WriteAsync(writer, chunk, cancellationToken));
        }

        if (writeNewLine)
        {
            await Coordinated(WriteAsync(writer, CoreNewLine(writer), cancellationToken));
        }
    }

    // Rune.AsSpan, as the BCL's WriteAsync(Rune) and WriteLineAsync(Rune) split the rune.
    private static (char First, char? Second) Chars(Rune value)
    {
        Span<char> chars = stackalloc char[2];
        int length = value.EncodeToUtf16(chars);
        return length > 1 ? (chars[0], chars[1]) : (chars[0], null);
    }

    // The protected TextWriter.CoreNewLine, which the BCL's line terminators write (a derived writer may set it directly).
    private static ReadOnlyMemory<char> CoreNewLine(TextWriter writer) => CoreNewLineField(writer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "CoreNewLine")]
    private static extern ref char[] CoreNewLineField(TextWriter writer);

    // A null receiver throws NullReferenceException before anything else, as the BCL's callvirt does.
    private static void RequireReceiver(TextWriter writer) => _ = writer.GetType();

    private static CoordinatedValueTaskAwaitable Coordinated(Task task) => new(new ValueTask(task));

    // Records, per writer type, which asynchronous members it overrides; see ControlledStream.
    private sealed class WriterOverrides
    {
        private static readonly ConditionalWeakTable<Type, WriterOverrides> Cache = [];

        private WriterOverrides(TextWriter writer)
        {
            WriteAsyncChar = Overrides((Func<char, Task>)writer.WriteAsync);
            WriteAsyncRune = Overrides((Func<Rune, Task>)writer.WriteAsync);
            WriteAsyncString = Overrides((Func<string?, Task>)writer.WriteAsync);
            WriteAsyncStringBuilder = Overrides((Func<StringBuilder?, CancellationToken, Task>)writer.WriteAsync);
            WriteAsyncArray = Overrides((Func<char[], int, int, Task>)writer.WriteAsync);
            WriteAsyncMemory = Overrides((Func<ReadOnlyMemory<char>, CancellationToken, Task>)writer.WriteAsync);
            WriteLineAsyncChar = Overrides((Func<char, Task>)writer.WriteLineAsync);
            WriteLineAsyncRune = Overrides((Func<Rune, Task>)writer.WriteLineAsync);
            WriteLineAsyncString = Overrides((Func<string?, Task>)writer.WriteLineAsync);
            WriteLineAsyncStringBuilder = Overrides((Func<StringBuilder?, CancellationToken, Task>)writer.WriteLineAsync);
            WriteLineAsyncArray = Overrides((Func<char[], int, int, Task>)writer.WriteLineAsync);
            WriteLineAsyncMemory = Overrides((Func<ReadOnlyMemory<char>, CancellationToken, Task>)writer.WriteLineAsync);
            WriteLineAsyncEmpty = Overrides((Func<Task>)writer.WriteLineAsync);
            FlushAsync = Overrides((Func<Task>)writer.FlushAsync);
            FlushAsyncToken = Overrides((Func<CancellationToken, Task>)writer.FlushAsync);
        }

        public bool WriteAsyncChar { get; }

        public bool WriteAsyncRune { get; }

        public bool WriteAsyncString { get; }

        public bool WriteAsyncStringBuilder { get; }

        public bool WriteAsyncArray { get; }

        public bool WriteAsyncMemory { get; }

        public bool WriteLineAsyncChar { get; }

        public bool WriteLineAsyncRune { get; }

        public bool WriteLineAsyncString { get; }

        public bool WriteLineAsyncStringBuilder { get; }

        public bool WriteLineAsyncArray { get; }

        public bool WriteLineAsyncMemory { get; }

        public bool WriteLineAsyncEmpty { get; }

        public bool FlushAsync { get; }

        public bool FlushAsyncToken { get; }

        public static WriterOverrides Of(TextWriter writer) =>
            Cache.TryGetValue(writer.GetType(), out WriterOverrides? overrides) ? overrides : Record(writer);

        private static bool Overrides(Delegate member) => member.Method.DeclaringType != typeof(TextWriter);

        // Separate from Of so that a cache hit allocates no closure over the writer.
        private static WriterOverrides Record(TextWriter writer) =>
            Cache.GetValue(writer.GetType(), _ => new WriterOverrides(writer));
    }
}
