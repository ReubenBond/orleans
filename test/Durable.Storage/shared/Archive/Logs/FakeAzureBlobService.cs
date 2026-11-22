using System.Globalization;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>An operation of the Azure Blob service which <see cref="FakeAzureBlobService"/> serves.</summary>
internal enum FakeAzureOperation
{
    /// <summary><c>Put Blob</c> of an append blob, which creates or re-creates it empty.</summary>
    CreateAppendBlob,

    /// <summary><c>Put Blob</c> of a block blob with its content.</summary>
    PutBlockBlob,

    /// <summary><c>Append Block</c>.</summary>
    AppendBlock,

    /// <summary><c>Get Blob Properties</c>.</summary>
    GetProperties,

    /// <summary><c>Get Blob</c>, whole or ranged.</summary>
    GetBlob,

    /// <summary><c>Get Blob Tags</c>.</summary>
    GetTags,

    /// <summary><c>List Blobs</c>.</summary>
    List,

    /// <summary><c>Delete Blob</c>.</summary>
    Delete,

    /// <summary><c>Create Container</c>, which the service's one container, which always exists, answers 409.</summary>
    CreateContainer,
}

/// <summary>A request <see cref="FakeAzureBlobService"/> is about to serve, which its fault injector sees.</summary>
/// <param name="Operation">The request's operation.</param>
/// <param name="Blob">The blob's name, or the listing's prefix.</param>
/// <param name="Offset">An <c>Append Block</c>'s <c>appendpos</c>, or a ranged read's offset; otherwise 0.</param>
internal readonly record struct FakeAzureRequest(FakeAzureOperation Operation, string Blob, long Offset);

/// <summary>A fault <see cref="FakeAzureBlobService"/> injects into one request.</summary>
internal enum FakeAzureFault
{
    /// <summary>The request is served.</summary>
    None,

    /// <summary>The connection breaks before the service sees the request; the client may retry it.</summary>
    FailBefore,

    /// <summary>The service serves the request and the connection breaks before its answer; the client may retry it.</summary>
    FailAfter,

    /// <summary>One byte of the request's body flips after the client computed its checksum, as damage in transit would.</summary>
    Corrupt,

    /// <summary>
    /// The connection breaks before the answer, and the request, still in flight, lands only after the next request the
    /// service serves: a straggler which lands after its own retry, or after its call gave up.
    /// </summary>
    DeliverLater,

    /// <summary>The service serves the request and answers 500, as a server failing after the write landed; the client retries it.</summary>
    ServerErrorAfterLanding,

    /// <summary>A read's response body breaks halfway, which the SDK resumes from where it broke under the response's ETag.</summary>
    BreakMidBody,
}

/// <summary>One request the service served, for snapshots: its operation, blob, conditions, and status.</summary>
/// <param name="Operation">The operation.</param>
/// <param name="Blob">The blob's name, or the listing's prefix.</param>
/// <param name="Headers">The request's conditions, its blob type, tags, metadata, range, and whether it carried a <c>Content-MD5</c>, by header name.</param>
/// <param name="Status">The response's status code.</param>
internal sealed record FakeAzureRecord(FakeAzureOperation Operation, string Blob, SortedDictionary<string, string> Headers, int Status);

/// <summary>
/// A fake of the Azure Blob service at the HTTP layer, beneath the real SDK (archive-logs.md §10): append and block
/// blobs, their metadata, index tags, ETags, and committed block counts; <c>If-Match</c>, <c>If-None-Match</c>,
/// <c>x-ms-if-tags</c>, and <c>appendpos</c> conditions evaluated atomically with each write; ranged reads; listings; and
/// <c>Content-MD5</c> checked against the body. It models the preconditions of archive-logs.md §9.1 as a live
/// account answers them, so the store's tests run on every change, and a simulation schedules each request's delay.
/// </summary>
/// <remarks>
/// Each response's body is served from pooled pages its stream leases, so a store which leaves a response undisposed
/// leaks pages the leak checks report. A fault injector sees every request and may break the connection before or after
/// the service serves it, which the SDK's own retries then meet.
/// </remarks>
/// <param name="faults">The fault injector, which sees every request; by default none.</param>
/// <param name="checkInvariants">
/// Whether to check, after each write, what archive logs keep of the write-ahead blob it touched, itself or through its
/// checkpoint (archive-logs.md §5, §7.3): its tag is its <c>wal_generation</c>, the checkpoint it names exists, its
/// committed blocks are at most its length, and its generation never falls; each violation is recorded in
/// <see cref="Violations"/>.
/// </param>
/// <param name="schedule">
/// Awaited before each request, in the SDK's pipeline, as a simulation's delay; by default a yield. The service itself
/// answers synchronously, so no continuation of the BCL's HTTP client, which no simulation rewrites, waits on it.
/// </param>
/// <param name="strict">
/// Whether a request which carries a condition the fake does not model, such as <c>If-Modified-Since</c>, a lease, or an
/// <c>If-None-Match</c> naming an ETag, is refused with 501 <c>UnmodelledCondition</c> and recorded in
/// <see cref="Violations"/>, rather than served as if it carried none.
/// </param>
internal sealed partial class FakeAzureBlobService(Func<FakeAzureRequest, FakeAzureFault>? faults = null, Func<CancellationToken, Task>? schedule = null, bool checkInvariants = false, bool strict = false) : HttpMessageHandler
{
    /// <summary>The container the service holds.</summary>
    public const string Container = "clog";

    /// <summary>The conditional headers the fake does not model, which a strict fake refuses.</summary>
    private static readonly string[] UnmodelledConditions =
    [
        "If-Modified-Since", "If-Unmodified-Since", "x-ms-lease-id", "x-ms-blob-condition-maxsize",
        "x-ms-if-sequence-number-le", "x-ms-if-sequence-number-lt", "x-ms-if-sequence-number-eq",
        "x-ms-source-if-match", "x-ms-source-if-none-match", "x-ms-source-if-modified-since",
        "x-ms-source-if-unmodified-since", "x-ms-source-if-tags",
    ];

    /// <summary>The oldest service version whose <c>x-ms-if-tags</c> a write may carry: 2022-11-02, on which archive logs rest.</summary>
    private const string TagConditionsVersion = "2022-11-02";

    private static readonly DateTimeOffset Modified = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Lock _lock = new();
    private readonly SortedDictionary<string, Blob> _blobs = [with(StringComparer.Ordinal)];
    private readonly List<FakeAzureRecord> _records = [];
    private readonly Func<CancellationToken, Task> _schedule = schedule ?? (static async _ => await Task.Yield());
    private readonly Queue<Call> _deferred = [];
    private readonly Dictionary<FakeAzureFault, int> _fired = [];
    private readonly Dictionary<string, ulong> _generations = [with(StringComparer.Ordinal)];
    private readonly List<string> _violations = [];
    private long _etags;
    private HttpClient? _http;

    /// <summary>Gets or sets an error added to the offset each <c>Append Block</c> reports, as a misbehaving service would; for tests.</summary>
    public long AppendOffsetSkew { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an <c>Append Block</c> evaluates its conditions, waits on the service's
    /// schedule, and then appends without evaluating them again, so a concurrent write which lands between them is
    /// appended after: a race mutant of the service's atomic <c>appendpos</c> and tag conditions, which only an
    /// interleaving inside the wait reveals; for tests.
    /// </summary>
    public bool ChecksAppendConditionsBeforeWritingMutant { get; set; }

    /// <summary>Gets the violations of what archive logs keep of their blobs, oldest first.</summary>
    public IReadOnlyList<string> Violations
    {
        get
        {
            lock (_lock)
            {
                return [.. _violations];
            }
        }
    }

    /// <summary>Returns how many times each fault fired.</summary>
    public IReadOnlyDictionary<FakeAzureFault, int> FaultsFired()
    {
        lock (_lock)
        {
            return new Dictionary<FakeAzureFault, int>(_fired);
        }
    }

    /// <summary>Gets the requests served, oldest first.</summary>
    public IReadOnlyList<FakeAzureRecord> Records
    {
        get
        {
            lock (_lock)
            {
                return [.. _records];
            }
        }
    }

    /// <summary>
    /// Returns a client of the service's container, through the real SDK, whose <paramref name="maxRetries"/> retries meet
    /// the faults this service injects with short delays. Its HTTP client sets no timeout of its own, so under a simulation
    /// the SDK's network timeout, a virtual one, bounds each request.
    /// </summary>
    public BlobContainerClient Client(int maxRetries = 3) => ClientOf(Container, maxRetries);

    /// <summary>
    /// Returns a client of a container the service does not hold, every request to which the service answers 404
    /// <c>ContainerNotFound</c>.
    /// </summary>
    public BlobContainerClient MissingContainerClient(int maxRetries = 3) => ClientOf(MissingContainer, maxRetries);

    /// <summary>The name of a container the service does not hold.</summary>
    public const string MissingContainer = "missing";

    private BlobContainerClient ClientOf(string container, int maxRetries)
    {
        lock (_lock)
        {
            _http ??= new HttpClient(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }

        var options = new BlobClientOptions { Transport = new HttpClientTransport(_http) };
        options.AddPolicy(new SchedulePolicy(_schedule), HttpPipelinePosition.PerRetry);
        options.RetryPolicy = new RetryPolicy(maxRetries, new FixedDelay());
        return new BlobContainerClient(new Uri("https://fake.blob.core.windows.net/" + container), options);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Returns a blob's content and metadata, or <see langword="null"/> when it is absent; for tests.</summary>
    public (byte[] Content, IReadOnlyList<KeyValuePair<string, string>> Metadata, IReadOnlyDictionary<string, string> Tags)? Peek(string name)
    {
        lock (_lock)
        {
            return _blobs.TryGetValue(name, out var blob) ? (blob.Content(), [.. blob.Metadata], new Dictionary<string, string>(blob.Tags, StringComparer.Ordinal)) : null;
        }
    }

    /// <summary>Removes a blob, as damage or another writer would; for tests.</summary>
    public void Remove(string name)
    {
        lock (_lock)
        {
            _ = _blobs.Remove(name);
        }
    }

    /// <summary>Sets a blob's metadata, as damage or another writer would; for tests.</summary>
    public void SetMetadata(string name, IReadOnlyList<KeyValuePair<string, string>> metadata)
    {
        lock (_lock)
        {
            var blob = _blobs[name];
            blob.Metadata = [.. metadata];
            blob.ETag = ++_etags;
        }
    }

    /// <summary>Sets the committed block count a blob's reads report, as a misbehaving service would; for tests.</summary>
    public void SetReportedBlocks(string name, int blocks)
    {
        lock (_lock)
        {
            _blobs[name].ReportedBlocks = blocks;
        }
    }

    /// <summary>Gets a value indicating whether a request is still in flight, which may land at the next request served.</summary>
    public bool HasDeferred
    {
        get
        {
            lock (_lock)
            {
                return _deferred.Count > 0;
            }
        }
    }

    /// <summary>Lands every request still in flight; for tests, at a case's end.</summary>
    public void DeliverDeferred()
    {
        lock (_lock)
        {
            ServeDeferred();
        }
    }

    /// <summary>Appends bytes to an append blob as one block, as another writer would; for tests.</summary>
    public void AppendRaw(string name, byte[] block)
    {
        lock (_lock)
        {
            var blob = _blobs[name];
            blob.Blocks.Add(block);
            blob.ETag = ++_etags;
        }
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!ChecksAppendConditionsBeforeWritingMutant || !InContainer(request))
        {
            return Send(request, cancellationToken);
        }

        var (call, fault) = Prepare(request, cancellationToken);
        if (call.Request.Operation != FakeAzureOperation.AppendBlock || fault != FakeAzureFault.None)
        {
            return Respond(request, call, fault);
        }

        lock (_lock)
        {
            if (_blobs.GetValueOrDefault(call.Request.Blob) is not { } blob || Conditions(call, blob) is not null)
            {
                return Respond(request, call, fault);
            }
        }

        await _schedule(cancellationToken);
        HttpResponseMessage response;
        lock (_lock)
        {
            response = ServeAndRecord(call, conditionsChecked: true);
        }

        response.RequestMessage = request;
        return response;
    }

    /// <inheritdoc/>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!InContainer(request))
        {
            ThrowIfNotAscii(request);
            return Error(HttpStatusCode.NotFound, "ContainerNotFound");
        }

        var (call, fault) = Prepare(request, cancellationToken);
        return Respond(request, call, fault);
    }

    /// <summary>Returns whether a request addresses the service's one container, which every other answers as missing.</summary>
    private static bool InContainer(HttpRequestMessage request) =>
        request.RequestUri is not { } uri || uri.AbsolutePath.StartsWith("/" + Container, StringComparison.Ordinal);

    /// <summary>
    /// Refuses a header which is not ASCII, a name's non-ASCII metadata among them: a socket handler refuses to send one
    /// before the service sees the request, and the fake refuses it as one does.
    /// </summary>
    private static void ThrowIfNotAscii(HttpRequestMessage request)
    {
        if (request.Headers.Any(static header => header.Value.Any(static value => !Ascii.IsValid(value))))
        {
            throw new HttpRequestException("Request headers must contain only ASCII characters.");
        }
    }

    /// <summary>Reads a request whole, parses it, and draws its fault.</summary>
    private (Call Call, FakeAzureFault Fault) Prepare(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = Receive(request, cancellationToken);
        var fault = faults?.Invoke(call.Request) ?? FakeAzureFault.None;
        if (fault == FakeAzureFault.Corrupt && call.Body.Length > 0)
        {
            call.Body[call.Body.Length / 2] ^= 0x20;
            call = Parse(request, call.Body);
        }

        return (call, fault);
    }

    /// <summary>Reads a request whole and parses it.</summary>
    private static Call Receive(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ThrowIfNotAscii(request);
        byte[] body = [];
        if (request.Content is { } requestContent)
        {
            using var stream = requestContent.ReadAsStream(cancellationToken);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            body = copy.ToArray();
        }

        return Parse(request, body);
    }

    /// <summary>Serves a parsed request with <paramref name="fault"/> injected.</summary>
    private HttpResponseMessage Respond(HttpRequestMessage request, Call call, FakeAzureFault fault)
    {
        HttpResponseMessage response;
        lock (_lock)
        {
            if (fault != FakeAzureFault.None)
            {
                _fired[fault] = _fired.GetValueOrDefault(fault) + 1;
            }

            if (fault is FakeAzureFault.FailBefore or FakeAzureFault.DeliverLater)
            {
                if (fault == FakeAzureFault.DeliverLater)
                {
                    _deferred.Enqueue(call);
                }

                throw new HttpRequestException("The fake Azure Blob service's connection broke before the answer, as injected.", new IOException("Injected."));
            }

            var deferred = _deferred.Count;
            response = ServeAndRecord(call);
            ServeDeferred(deferred);
        }

        switch (fault)
        {
            case FakeAzureFault.FailAfter:
                response.Dispose();
                throw new HttpRequestException("The fake Azure Blob service's connection broke after the request, as injected.", new IOException("Injected."));
            case FakeAzureFault.ServerErrorAfterLanding:
                response.Dispose();
                response = Error(HttpStatusCode.InternalServerError, "InternalError");
                break;
            case FakeAzureFault.BreakMidBody when call.Request.Operation == FakeAzureOperation.GetBlob && response.Content.Headers.ContentLength is long length and >= 2:
                response.Content = Breaking(response.Content, length);
                break;
            case FakeAzureFault.None:
            case FakeAzureFault.Corrupt:
            case FakeAzureFault.BreakMidBody:
            case FakeAzureFault.FailBefore:
            case FakeAzureFault.DeliverLater:
            default:
                break;
        }

        response.RequestMessage = request;
        return response;
    }

    /// <summary>Returns a response body which breaks after half its <paramref name="length"/> bytes.</summary>
    private static StreamContent Breaking(HttpContent content, long length)
    {
        var breaking = new StreamContent(new BreakingStream(content.ReadAsStream(), length / 2));
        foreach (var (name, values) in content.Headers)
        {
            _ = breaking.Headers.TryAddWithoutValidation(name, values);
        }

        return breaking;
    }

    /// <summary>
    /// Serves a call under the lock, records it, and checks the invariants after a write; a call whose conditions were
    /// <paramref name="conditionsChecked"/> before is served without evaluating them again.
    /// </summary>
    private HttpResponseMessage ServeAndRecord(Call call, bool conditionsChecked = false)
    {
        var response = Serve(call, conditionsChecked);
        _records.Add(new FakeAzureRecord(call.Request.Operation, call.Request.Blob, call.Recorded, (int)response.StatusCode));
        if (checkInvariants && call.Request.Operation is FakeAzureOperation.CreateAppendBlob or FakeAzureOperation.AppendBlock or FakeAzureOperation.PutBlockBlob or FakeAzureOperation.Delete)
        {
            CheckInvariants(call.Request.Blob);
        }

        return response;
    }

    /// <summary>Lands the first <paramref name="count"/> deferred requests, every one by default, under the lock; their answers are lost.</summary>
    private void ServeDeferred(int count = int.MaxValue)
    {
        for (var served = 0; served < count && _deferred.TryDequeue(out var call); served++)
        {
            ServeAndRecord(call).Dispose();
        }
    }

    /// <summary>
    /// Checks what archive logs keep of the write-ahead blob a write to <paramref name="written"/> may have changed, recording
    /// each violation: the blob itself, or the write-ahead blob of a checkpoint's log, the only one which names it. A
    /// write-ahead blob's invariants rest on it and its own checkpoint alone, so the blobs no write touched need no check.
    /// </summary>
    private void CheckInvariants(string written)
    {
        if (WalOf(written) is not { } wal || !_blobs.TryGetValue(wal, out var blob))
        {
            return;
        }

        var metadata = blob.Metadata.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (!ulong.TryParse(metadata.GetValueOrDefault("wal_generation"), NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
        {
            _violations.Add($"{wal} records no generation.");
            return;
        }

        if (blob.Tags.GetValueOrDefault("durable-gen") != generation.ToString("D20", CultureInfo.InvariantCulture))
        {
            _violations.Add($"{wal} records generation {generation} under the tag {blob.Tags.GetValueOrDefault("durable-gen")}.");
        }

        if (metadata.GetValueOrDefault("checkpoint") is { } checkpoint && !_blobs.ContainsKey(checkpoint))
        {
            _violations.Add($"{wal} names the checkpoint {checkpoint}, which does not exist.");
        }

        if (blob.Blocks.Count > blob.Length)
        {
            _violations.Add($"{wal} holds {blob.Blocks.Count} blocks in {blob.Length} bytes.");
        }

        if (generation < _generations.GetValueOrDefault(wal))
        {
            _violations.Add($"{wal}'s generation fell from {_generations[wal]} to {generation}.");
        }

        _generations[wal] = Math.Max(generation, _generations.GetValueOrDefault(wal));
    }

    /// <summary>
    /// Returns the write-ahead blob whose invariants a write to <paramref name="written"/> may change: the blob itself, or
    /// the write-ahead blob of a checkpoint's log; none for any other blob.
    /// </summary>
    private static string? WalOf(string written)
    {
        const string Wal = "wal/";
        const string Checkpoints = "checkpoints/";
        if (written.StartsWith(Wal, StringComparison.Ordinal))
        {
            return written;
        }

        var last = written.LastIndexOf('/');
        return written.StartsWith(Checkpoints, StringComparison.Ordinal) && last > Checkpoints.Length ? Wal + written[Checkpoints.Length..last] : null;
    }
    /// <summary>Returns the MD5 of <paramref name="body"/> as <c>Content-MD5</c> carries it.</summary>
    private static string Md5Of(byte[] body)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(body);
        return Convert.ToBase64String(md5.GetHashAndReset());
    }
    private static Call Parse(HttpRequestMessage request, byte[] body)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("A request has a URI.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(static pair => pair[0], static pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty, StringComparer.Ordinal);
        var path = uri.AbsolutePath;
        var prefix = "/" + Container;
        var blob = path.Length > prefix.Length + 1 ? Uri.UnescapeDataString(path[(prefix.Length + 1)..]) : string.Empty;
        string? Header(string name) =>
            request.Headers.TryGetValues(name, out var values) || (request.Content?.Headers.TryGetValues(name, out values) ?? false)
                ? string.Join(",", values)
                : null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "If-Match", "If-None-Match", "x-ms-if-tags", "x-ms-blob-condition-appendpos", "x-ms-blob-type", "x-ms-tags", "x-ms-range", "Range", "Content-MD5", "x-ms-blob-content-md5", "x-ms-version" })
        {
            if (Header(name) is { } value)
            {
                headers[name] = value;
            }
        }

        var metadata = request.Headers
            .Where(static header => header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
            .Select(static header => KeyValuePair.Create(header.Key["x-ms-meta-".Length..], string.Join(",", header.Value)))
            .ToList();
        var comp = query.GetValueOrDefault("comp");
        var operation = (request.Method.Method, comp) switch
        {
            ("PUT", null) when query.GetValueOrDefault("restype") == "container" => FakeAzureOperation.CreateContainer,
            ("PUT", "appendblock") => FakeAzureOperation.AppendBlock,
            ("PUT", null) when headers.GetValueOrDefault("x-ms-blob-type") == "AppendBlob" => FakeAzureOperation.CreateAppendBlob,
            ("PUT", null) => FakeAzureOperation.PutBlockBlob,
            ("HEAD", null) => FakeAzureOperation.GetProperties,
            ("GET", "list") => FakeAzureOperation.List,
            ("GET", "tags") => FakeAzureOperation.GetTags,
            ("GET", null) => FakeAzureOperation.GetBlob,
            ("DELETE", null) => FakeAzureOperation.Delete,
            _ => throw new NotSupportedException($"The fake Azure Blob service serves no {request.Method} with comp={comp}."),
        };
        var range = Range(headers.GetValueOrDefault("x-ms-range") ?? headers.GetValueOrDefault("Range"));
        var target = operation == FakeAzureOperation.List ? query.GetValueOrDefault("prefix") ?? string.Empty : blob;
        var offset = operation == FakeAzureOperation.AppendBlock && headers.TryGetValue("x-ms-blob-condition-appendpos", out var at)
            ? long.Parse(at, CultureInfo.InvariantCulture)
            : range?.Offset ?? 0;
        var recorded = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (header, value) in headers)
        {
            recorded[header.ToLowerInvariant()] = header.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase) || header.Equals("x-ms-blob-content-md5", StringComparison.OrdinalIgnoreCase) ? "present" : value;
        }

        bool? md5Valid = headers.TryGetValue("Content-MD5", out var md5) ? md5 == Md5Of(body) : null;

        foreach (var (key, value) in metadata)
        {
            recorded["x-ms-meta-" + key] = value;
        }

        var unmodelled = UnmodelledConditions.FirstOrDefault(name => Header(name) is not null)
            ?? (headers.TryGetValue("If-None-Match", out var noneMatch) && noneMatch.Trim() != "*" ? "If-None-Match: " + noneMatch : null);
        return new Call(new FakeAzureRequest(operation, target, offset), headers, metadata, body, range, recorded, md5Valid, (query.GetValueOrDefault("include") ?? string.Empty).Split(',').Contains("metadata", StringComparer.OrdinalIgnoreCase), query.GetValueOrDefault("startFrom"), query.GetValueOrDefault("marker"), int.TryParse(query.GetValueOrDefault("maxresults"), NumberStyles.None, CultureInfo.InvariantCulture, out var most) ? most : 5_000, unmodelled);
    }

    private static (long Offset, long? Last)? Range(string? header)
    {
        if (header is null || !header.StartsWith("bytes=", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = header["bytes=".Length..].Split('-');
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), parts[1].Length == 0 ? null : long.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent($"<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>{code}</Code><Message>{code}</Message></Error>", Encoding.UTF8, "application/xml"),
        };
        response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
        return response;
    }

    private static HttpResponseMessage Leased(HttpStatusCode status, ReadOnlySpan<byte> bytes)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        using var buffer = writer.Length == 0 ? default : writer.ConsumeSlice(writer.Length);
        var content = new StreamContent(new ArcBufferReadStream(buffer));
        content.Headers.ContentLength = bytes.Length;
        return new HttpResponseMessage(status) { Content = content };
    }

    [GeneratedRegex("^\\s*\"(?<key>[^\"]+)\"\\s*=\\s*'(?<value>[^']*)'\\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TagEquality();

    private HttpResponseMessage Serve(Call call, bool conditionsChecked)
    {
        if (strict && call.Unmodelled is { } unmodelled)
        {
            _violations.Add($"A {call.Request.Operation} of '{call.Request.Blob}' carried the unmodelled condition {unmodelled}.");
            return Error(HttpStatusCode.NotImplemented, "UnmodelledCondition");
        }

        if (call.Request.Operation == FakeAzureOperation.CreateContainer)
        {
            return Error(HttpStatusCode.Conflict, "ContainerAlreadyExists");
        }

        var name = call.Request.Blob;
        var blob = _blobs.GetValueOrDefault(name);
        if (call.Request.Operation is not (FakeAzureOperation.List or FakeAzureOperation.CreateAppendBlob or FakeAzureOperation.PutBlockBlob) && blob is null)
        {
            // Under x-ms-if-tags too, as the live service answers (archive-logs.md §9.1, precondition 3).
            return Error(HttpStatusCode.NotFound, "BlobNotFound");
        }

        if (call.Headers.ContainsKey("x-ms-if-tags") && string.CompareOrdinal(call.Headers.GetValueOrDefault("x-ms-version"), TagConditionsVersion) < 0)
        {
            return Error(HttpStatusCode.BadRequest, "InvalidHeaderValue");
        }

        if (!conditionsChecked && Conditions(call, blob) is { } failed)
        {
            return Error(failed.Status, failed.Code);
        }

        switch (call.Request.Operation)
        {
            case FakeAzureOperation.CreateAppendBlob:
            case FakeAzureOperation.PutBlockBlob:
                if (call.Md5Valid == false)
                {
                    return Error(HttpStatusCode.BadRequest, "Md5Mismatch");
                }

                var created = new Blob(call.Request.Operation == FakeAzureOperation.CreateAppendBlob, ++_etags) { Metadata = [.. call.Metadata], Tags = Tags(call.Headers.GetValueOrDefault("x-ms-tags")) };
                if (call.Request.Operation == FakeAzureOperation.PutBlockBlob)
                {
                    created.Blocks.Add(call.Body);

                    // Put Blob stores the content MD5 the request sets, or else computes it, as the service does.
                    created.ContentMd5 = call.Headers.GetValueOrDefault("x-ms-blob-content-md5") ?? Md5Of(call.Body);
                }

                _blobs[name] = created;
                return Written(HttpStatusCode.Created, created);
            case FakeAzureOperation.AppendBlock:
                if (!blob!.IsAppend)
                {
                    return Error(HttpStatusCode.Conflict, "InvalidBlobType");
                }

                if (call.Body.Length > 100 << 20)
                {
                    return Error(HttpStatusCode.RequestEntityTooLarge, "RequestBodyTooLarge");
                }

                if (blob.Blocks.Count >= 50_000)
                {
                    return Error(HttpStatusCode.Conflict, "BlockCountExceedsLimit");
                }

                if (call.Md5Valid == false)
                {
                    return Error(HttpStatusCode.BadRequest, "Md5Mismatch");
                }

                var offset = blob.Length;
                blob.Blocks.Add(call.Body);
                blob.ETag = ++_etags;
                var appended = Written(HttpStatusCode.Created, blob);
                appended.Headers.TryAddWithoutValidation("x-ms-blob-append-offset", (offset + AppendOffsetSkew).ToString(CultureInfo.InvariantCulture));
                appended.Headers.TryAddWithoutValidation("x-ms-blob-committed-block-count", blob.Blocks.Count.ToString(CultureInfo.InvariantCulture));
                return appended;
            case FakeAzureOperation.GetProperties:
                var properties = Described(Leased(HttpStatusCode.OK, []), blob!);
                properties.Content.Headers.ContentLength = blob!.Length;
                return properties;
            case FakeAzureOperation.GetBlob:
                return Download(call, blob!);
            case FakeAzureOperation.GetTags:
                var tags = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><Tags><TagSet>");
                foreach (var (key, value) in blob!.Tags)
                {
                    _ = tags.Append(CultureInfo.InvariantCulture, $"<Tag><Key>{SecurityElement.Escape(key)}</Key><Value>{SecurityElement.Escape(value)}</Value></Tag>");
                }

                return Xml(tags.Append("</TagSet></Tags>").ToString());
            case FakeAzureOperation.List:
                return List(name, call.ListsMetadata, call.StartFrom, call.Marker, call.MaxResults);
            case FakeAzureOperation.Delete:
                _ = _blobs.Remove(name);
                return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new ByteArrayContent([]) };
            case FakeAzureOperation.CreateContainer:
            default:
                throw new NotSupportedException();
        }
    }

    /// <summary>Evaluates a write's or a read's conditions against the blob, atomically with the request.</summary>
    private static (HttpStatusCode Status, string Code)? Conditions(Call call, Blob? blob)
    {
        if (call.Headers.TryGetValue("If-None-Match", out var noneMatch) && noneMatch.Trim() == "*" && blob is not null)
        {
            return (HttpStatusCode.Conflict, "BlobAlreadyExists");
        }

        if (call.Headers.TryGetValue("If-Match", out var match) && (blob is null || match.Trim() is not "*" && match.Trim() != blob.Quoted))
        {
            return (HttpStatusCode.PreconditionFailed, "ConditionNotMet");
        }

        if (call.Headers.TryGetValue("x-ms-if-tags", out var expression))
        {
            var equality = TagEquality().Match(expression);
            if (!equality.Success)
            {
                return (HttpStatusCode.BadRequest, "InvalidHeaderValue");
            }

            // A Put Blob under x-ms-if-tags alone creates an absent blob, as the live service does (§9.1, precondition 3).
            if (blob is not null && blob.Tags.GetValueOrDefault(equality.Groups["key"].Value) != equality.Groups["value"].Value)
            {
                return (HttpStatusCode.PreconditionFailed, "ConditionNotMet");
            }
        }

        if (call.Headers.TryGetValue("x-ms-blob-condition-appendpos", out var position) && long.Parse(position, CultureInfo.InvariantCulture) != blob?.Length)
        {
            return (HttpStatusCode.PreconditionFailed, "AppendPositionConditionNotMet");
        }

        return null;
    }

    private static Dictionary<string, string> Tags(string? header) =>
        (header ?? string.Empty).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(static pair => Uri.UnescapeDataString(pair[0]), static pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);

    private static HttpResponseMessage Written(HttpStatusCode status, Blob blob)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
        response.Headers.TryAddWithoutValidation("ETag", blob.Quoted);
        response.Content.Headers.LastModified = Modified;
        return response;
    }

    private static HttpResponseMessage Described(HttpResponseMessage response, Blob blob)
    {
        response.Headers.TryAddWithoutValidation("ETag", blob.Quoted);
        response.Headers.TryAddWithoutValidation("x-ms-blob-type", blob.IsAppend ? "AppendBlob" : "BlockBlob");
        if (blob.IsAppend)
        {
            response.Headers.TryAddWithoutValidation("x-ms-blob-committed-block-count", (blob.ReportedBlocks ?? blob.Blocks.Count).ToString(CultureInfo.InvariantCulture));
        }

        if (blob.ContentMd5 is { } md5)
        {
            response.Content.Headers.TryAddWithoutValidation("Content-MD5", md5);
        }

        response.Headers.TryAddWithoutValidation("x-ms-tag-count", blob.Tags.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var (key, value) in blob.Metadata)
        {
            response.Headers.TryAddWithoutValidation("x-ms-meta-" + key, value);
        }

        response.Content.Headers.LastModified = Modified;
        return response;
    }

    private static HttpResponseMessage Download(Call call, Blob blob)
    {
        var content = blob.Content();
        if (call.Range is not { } range)
        {
            return Described(Leased(HttpStatusCode.OK, content), blob);
        }

        if (range.Offset >= content.Length)
        {
            return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange");
        }

        var last = Math.Min(range.Last ?? long.MaxValue, content.Length - 1);
        var response = Described(Leased(HttpStatusCode.PartialContent, content.AsSpan((int)range.Offset, (int)(last - range.Offset + 1))), blob);
        response.Content.Headers.TryAddWithoutValidation("Content-Range", string.Create(CultureInfo.InvariantCulture, $"bytes {range.Offset}-{last}/{content.Length}"));
        return response;
    }

    /// <summary>
    /// Lists the blobs under <paramref name="prefix"/> in name order, from <paramref name="startFrom"/> on when the request
    /// names one, with their metadata only when the request asks for it, as Azure does: at most
    /// <paramref name="maxResults"/> a page, from <paramref name="marker"/> on, the name of the first blob of the page,
    /// with <c>NextMarker</c> naming the blob after the page's last, or empty after the last page.
    /// </summary>
    private HttpResponseMessage List(string prefix, bool withMetadata, string? startFrom, string? marker, int maxResults)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
            .Append(CultureInfo.InvariantCulture, $"<EnumerationResults ServiceEndpoint=\"https://fake.blob.core.windows.net/\" ContainerName=\"{Container}\"><Prefix>{SecurityElement.Escape(prefix)}</Prefix><Blobs>");
        var listed = _blobs
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)
                && (startFrom is null || string.CompareOrdinal(pair.Key, startFrom) >= 0)
                && (marker is null || string.CompareOrdinal(pair.Key, marker) >= 0))
            .Take(maxResults + 1)
            .ToList();
        foreach (var (name, blob) in listed.Take(maxResults))
        {
            var metadata = withMetadata ? "<Metadata>" + string.Concat(blob.Metadata.Select(static pair => $"<{pair.Key}>{SecurityElement.Escape(pair.Value)}</{pair.Key}>")) + "</Metadata>" : string.Empty;
            _ = xml.Append(CultureInfo.InvariantCulture, $"<Blob><Name>{SecurityElement.Escape(name)}</Name><Properties><Last-Modified>{Modified:R}</Last-Modified><Etag>{blob.Quoted}</Etag><Content-Length>{blob.Length}</Content-Length><Content-Type>application/octet-stream</Content-Type><BlobType>{(blob.IsAppend ? "AppendBlob" : "BlockBlob")}</BlobType></Properties>{metadata}</Blob>");
        }

        var next = listed.Count > maxResults ? $"<NextMarker>{SecurityElement.Escape(listed[maxResults].Key)}</NextMarker>" : "<NextMarker />";
        return Xml(xml.Append("</Blobs>").Append(next).Append("</EnumerationResults>").ToString());
    }

    private static HttpResponseMessage Xml(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    /// <summary>A parsed request.</summary>
    private sealed record Call(
        FakeAzureRequest Request,
        Dictionary<string, string> Headers,
        List<KeyValuePair<string, string>> Metadata,
        byte[] Body,
        (long Offset, long? Last)? Range,
        SortedDictionary<string, string> Recorded,
        bool? Md5Valid,
        bool ListsMetadata,
        string? StartFrom,
        string? Marker,
        int MaxResults,
        string? Unmodelled);

    /// <summary>
    /// A retry delay of one millisecond without jitter: the SDK draws its jitter from a process-wide random source, which
    /// a simulation's replay would not reproduce.
    /// </summary>
    private sealed class FixedDelay() : DelayStrategy(maxDelay: null, jitterFactor: 0)
    {
        protected override TimeSpan GetNextDelayCore(global::Azure.Response? response, int retryNumber) => TimeSpan.FromMilliseconds(1);
    }

    /// <summary>Awaits the service's schedule before each attempt of a request, in the SDK's rewritten pipeline.</summary>
    private sealed class SchedulePolicy(Func<CancellationToken, Task> schedule) : HttpPipelinePolicy
    {
        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) => ProcessNext(message, pipeline);

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            await schedule(message.CancellationToken);
            await ProcessNextAsync(message, pipeline);
        }
    }

    /// <summary>A stream which breaks with an <see cref="IOException"/> once it has served <paramref name="limit"/> bytes.</summary>
    private sealed class BreakingStream(Stream inner, long limit) : Stream
    {
        private long _served;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_served >= limit)
            {
                throw new IOException("The fake Azure Blob service's response broke mid-body, as injected.");
            }

            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, limit - _served)]);
            _served += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

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

    /// <summary>A blob: its kind, blocks, metadata, tags, and ETag.</summary>
    private sealed class Blob(bool isAppend, long etag)
    {
        public bool IsAppend { get; } = isAppend;

        public List<byte[]> Blocks { get; } = [];

        public List<KeyValuePair<string, string>> Metadata { get; set; } = [];

        public Dictionary<string, string> Tags { get; set; } = [];

        public string? ContentMd5 { get; set; }

        public int? ReportedBlocks { get; set; }

        public long ETag { get; set; } = etag;

        public string Quoted => string.Create(CultureInfo.InvariantCulture, $"\"0x{ETag:X}\"");

        public long Length => Blocks.Sum(static block => (long)block.Length);

        public byte[] Content() => [.. Blocks.SelectMany(static block => block)];
    }
}
