using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// A fault the <see cref="TransportDouble"/> injects into one request.
/// </summary>
internal enum TransportFault
{
    /// <summary>The request passes unchanged.</summary>
    None,

    /// <summary>The service never sees the request, and the client receives a 503.</summary>
    FailBefore,

    /// <summary>The request lands, and the client loses its answer as a broken connection loses it.</summary>
    LoseAnswer,

    /// <summary>One byte of the request's body is flipped after the client computed its checksums.</summary>
    Corrupt,

    /// <summary>
    /// The transport reads half the request's body, then waits for <see cref="TransportDouble.ReleaseStall"/> before it
    /// reads the rest, which it never sends; the client's wait ends only by its cancellation.
    /// </summary>
    Stall,

    /// <summary>The response's body breaks after <see cref="TransportDouble.BreakAfter"/> bytes.</summary>
    BreakResponse,
}

/// <summary>
/// A request the <see cref="TransportDouble"/> saw, with its body when the double stubs the service.
/// </summary>
internal sealed record StubbedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    /// <summary>
    /// Returns the request's header <paramref name="name"/>, or <see langword="null"/> when it carries none.
    /// </summary>
    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;
}

/// <summary>
/// The HTTP transport beneath a storage client: it passes requests to the emulator, injecting faults into the next
/// request a predicate matches or into a seeded random share of every request; or, given a responder, stubs the service,
/// recording each request and answering it without a network.
/// </summary>
/// <remarks>
/// A call which runs within <see cref="TrackCall"/> counts the faults injected into its own requests, so a test can tell a
/// call whose outcome a fault left unknown from one which failed by itself.
/// </remarks>
internal sealed class TransportDouble : DelegatingHandler
{
    private static readonly AsyncLocal<StrongBox<int>?> CallFaults = new();

    private readonly Lock _sync = new();
    private readonly List<StubbedRequest> _requests = [];
    private readonly TaskCompletionSource _stallReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TransportFault _next;
    private Func<HttpRequestMessage, bool>? _matches;
    private Random? _random;
    private Func<Task>? _onAnswerLost;
    private double _rate;
    private int _openResponses;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransportDouble"/> class, which passes requests to the network.
    /// </summary>
    public TransportDouble() => InnerHandler = new SocketsHttpHandler();

    public TransportDouble(HttpMessageHandler innerHandler) => InnerHandler = innerHandler;

    /// <summary>
    /// Gets or sets the responder which stubs the service: when set, no request reaches the network.
    /// </summary>
    internal Func<StubbedRequest, HttpResponseMessage>? Respond { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a stubbed request's body is recorded; otherwise the double reads it to its
    /// end and records none, for a request too large to keep.
    /// </summary>
    internal bool RecordBodies { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of bytes a <see cref="TransportFault.BreakResponse"/> passes before it breaks.
    /// </summary>
    internal int BreakAfter { get; set; } = 1;

    /// <summary>
    /// Gets or sets work the double awaits when a response breaks, before the client sees the break.
    /// </summary>
    internal Func<Task>? OnBreak { get; set; }

    /// <summary>
    /// Sets what runs once after a <see cref="TransportFault.LoseAnswer"/> request lands and before its answer is lost, as
    /// another client's request landing before the retry does.
    /// </summary>
    internal Func<Task>? OnAnswerLost
    {
        get => Volatile.Read(ref _onAnswerLost);
        set => Volatile.Write(ref _onAnswerLost, value);
    }

    /// <summary>
    /// Gets the number of faults injected.
    /// </summary>
    internal int Injected { get; private set; }

    /// <summary>
    /// Gets the requests the double saw.
    /// </summary>
    internal IReadOnlyList<StubbedRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// Gets the successful downloads whose responses the client has not yet disposed.
    /// </summary>
    internal int OpenResponses => Volatile.Read(ref _openResponses);

    /// <summary>
    /// Gets the task which completes, with the exception the client's content threw, once a stalled request's body was
    /// read to its end after <see cref="ReleaseStall"/>.
    /// </summary>
    internal Task<Exception?> StalledBody { get; private set; } = Task.FromResult<Exception?>(null);

    /// <summary>
    /// Returns whether <paramref name="request"/> uploads an object's content: a <c>PUT</c> with a body, as Azure's
    /// <c>Put Blob</c>, S3's <c>PutObject</c>, and a Cloud Storage resumable session's data are.
    /// </summary>
    internal static bool IsUpload(HttpRequestMessage request) =>
        request.Method == HttpMethod.Put && request.Content?.Headers.ContentLength is > 0;

    /// <summary>
    /// Returns whether <paramref name="request"/> downloads an object's content.
    /// </summary>
    internal static bool IsDownload(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get && request.RequestUri is { } uri
        && !uri.Query.Contains("comp=list", StringComparison.Ordinal)
        && !uri.Query.Contains("list-type", StringComparison.Ordinal)
        && !uri.AbsolutePath.EndsWith("/o", StringComparison.Ordinal);

    /// <summary>
    /// Starts counting, in the calling flow, the faults injected into the requests it makes.
    /// </summary>
    /// <returns>The count, which the flow's requests increment.</returns>
    internal static StrongBox<int> TrackCall() => CallFaults.Value = new StrongBox<int>();

    /// <summary>
    /// Injects <paramref name="fault"/> into the next request <paramref name="matches"/> accepts.
    /// </summary>
    internal void FailNext(TransportFault fault, Func<HttpRequestMessage, bool> matches)
    {
        lock (_sync)
        {
            _next = fault;
            _matches = matches;
        }
    }

    /// <summary>
    /// Injects <see cref="TransportFault.FailBefore"/> or <see cref="TransportFault.LoseAnswer"/> into a share
    /// <paramref name="rate"/> of every request, drawn from <paramref name="seed"/>.
    /// </summary>
    internal void FailRandomly(int seed, double rate)
    {
        lock (_sync)
        {
            _random = new Random(seed);
            _rate = rate;
        }
    }

    /// <summary>
    /// Lets a stalled request's body be read to its end.
    /// </summary>
    internal void ReleaseStall() => _stallReleased.TrySetResult();

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var fault = Take(request);
        if (fault != TransportFault.None && CallFaults.Value is { } faults)
        {
            _ = Interlocked.Increment(ref faults.Value);
        }

        if (Respond is { } respond)
        {
            return await StubAsync(request, respond, cancellationToken);
        }

        switch (fault)
        {
            case TransportFault.FailBefore:
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request, Content = new ByteArrayContent([]) };
            case TransportFault.LoseAnswer:
                using (await base.SendAsync(request, cancellationToken))
                {
                    if (Interlocked.Exchange(ref _onAnswerLost, null) is { } lost)
                    {
                        await lost();
                    }

                    throw new TransportDoubleException();
                }

            case TransportFault.Corrupt:
                await CorruptAsync(request, cancellationToken);
                return await ForwardAsync(request, cancellationToken);
            case TransportFault.Stall:
                return await StallAsync(request, cancellationToken);
            case TransportFault.BreakResponse:
                var response = await ForwardAsync(request, cancellationToken);
                response.Content = new BreakingContent(response.Content, BreakAfter, OnBreak);
                return response;
            case TransportFault.None:
                return await ForwardAsync(request, cancellationToken);
            default:
                throw new InvalidOperationException("The transport double knows no such fault.");
        }
    }

    /// <summary>
    /// Replaces the request's body with a copy whose middle byte is flipped, keeping its headers, its checksums among
    /// them.
    /// </summary>
    private static async Task CorruptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var original = request.Content!;
        var bytes = await original.ReadAsByteArrayAsync(cancellationToken);
        bytes[bytes.Length / 2] ^= 0xFF;
        var corrupted = new ByteArrayContent(bytes);
        foreach (var header in original.Headers)
        {
            _ = corrupted.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Content = corrupted;
    }

    private async Task<HttpResponseMessage> StubAsync(HttpRequestMessage request, Func<StubbedRequest, HttpResponseMessage> respond, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value);
        }

        var body = request.Content is null ? [] : RecordBodies ? await request.Content.ReadAsByteArrayAsync(cancellationToken) : await DrainAsync(request.Content, cancellationToken);
        var stubbed = new StubbedRequest(request.Method, request.RequestUri!, headers, body);
        lock (_sync)
        {
            _requests.Add(stubbed);
        }

        var response = respond(stubbed);
        response.RequestMessage = request;
        return response;
    }

    private async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (IsDownload(request) && response.IsSuccessStatusCode)
        {
            _ = Interlocked.Increment(ref _openResponses);
            response.Content = new TrackedContent(response.Content, () => Interlocked.Decrement(ref _openResponses));
        }

        return response;
    }

    private async Task<HttpResponseMessage> StallAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var length = request.Content!.Headers.ContentLength ?? 0;
        var sink = new StallingSink(length / 2, _stallReleased.Task);
        StalledBody = CopyBodyAsync(request.Content, sink);
        await sink.Reached.WaitAsync(cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
        throw new UnreachableException();
    }

    private static async Task<byte[]> DrainAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await content.CopyToAsync(Stream.Null, cancellationToken);
        return [];
    }

    private static async Task<Exception?> CopyBodyAsync(HttpContent content, Stream sink)
    {
        try
        {
            await content.CopyToAsync(sink);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private TransportFault Take(HttpRequestMessage request)
    {
        lock (_sync)
        {
            if (_matches is not null && _matches(request))
            {
                var fault = _next;
                _matches = null;
                Injected++;
                return fault;
            }

            if (_random is not null && _random.NextDouble() < _rate)
            {
                Injected++;
                return _random.Next(2) == 0 ? TransportFault.FailBefore : TransportFault.LoseAnswer;
            }

            return TransportFault.None;
        }
    }

    /// <summary>
    /// A write-only stream which takes <c>threshold</c> bytes, then waits for its release before it takes more.
    /// </summary>
    private sealed class StallingSink(long threshold, Task released) : Stream
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _written;

        public Task Reached => _reached.Task;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_written >= threshold)
            {
                _ = _reached.TrySetResult();
                await released;
            }

            _written += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// A response body whose disposal the double counts.
    /// </summary>
    private sealed class TrackedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action _disposed;
        private int _disposedOnce;

        public TrackedContent(HttpContent inner, Action disposed)
        {
            _inner = inner;
            _disposed = disposed;
            foreach (var header in inner.Headers)
            {
                _ = Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);

        protected override async Task<Stream> CreateContentReadStreamAsync() => new TrackedStream(await _inner.ReadAsStreamAsync(), Release);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new TrackedStream(await _inner.ReadAsStreamAsync(cancellationToken), Release);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Release();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Releases the response once, whether the client disposes its body's stream or the response.
        /// </summary>
        private void Release()
        {
            if (Interlocked.Exchange(ref _disposedOnce, 1) == 0)
            {
                _inner.Dispose();
                _disposed();
            }
        }
    }

    private sealed class TrackedStream(Stream inner, Action released) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                released();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A response body which breaks after a number of bytes, as a dropped connection breaks one.
    /// </summary>
    private sealed class BreakingContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly int _breakAfter;
        private readonly Func<Task>? _onBreak;

        public BreakingContent(HttpContent inner, int breakAfter, Func<Task>? onBreak)
        {
            _inner = inner;
            _breakAfter = breakAfter;
            _onBreak = onBreak;
            foreach (var header in inner.Headers)
            {
                _ = Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var source = await CreateContentReadStreamAsync();
            await source.CopyToAsync(stream);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new BreakingStream(await _inner.ReadAsStreamAsync(), _breakAfter, _onBreak);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class BreakingStream(Stream inner, int breakAfter, Func<Task>? onBreak) : Stream
    {
        private int _passed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _passed;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_passed >= breakAfter)
            {
                if (onBreak is not null)
                {
                    await onBreak();
                }

                throw new IOException("The transport double broke the response.");
            }

            var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, breakAfter - _passed)], cancellationToken);
            _passed += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// The exception the <see cref="TransportDouble"/> raises when it loses an answer.
/// </summary>
internal sealed class TransportDoubleException()
    : HttpRequestException("The transport double lost the answer.", new IOException("The connection broke after the request landed."));
