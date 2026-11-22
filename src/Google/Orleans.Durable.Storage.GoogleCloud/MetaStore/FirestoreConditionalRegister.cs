using System.Buffers;
using System.Buffers.Binary;
using Google.Cloud.Firestore;
using Grpc.Core;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;
using ProtobufTimestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Orleans.Durable.Storage.GoogleCloud.MetaStore;

/// <summary>
/// One register in a Firestore document whose update time is the register version.
/// </summary>
/// <remarks>
/// The version is the Base64 of 12 bytes: the document's update time, seconds (<c>int64</c> big-endian) then
/// nanoseconds (<c>int32</c> big-endian). A write copies its value into an array, which the client copies into the
/// document's bytes field, before the request is dispatched; a read's entry wraps the array the client returns for the
/// field, without a further copy.
/// </remarks>
internal sealed class FirestoreConditionalRegister : IConditionalRegister
{
    internal const string ValueFieldName = "value";

    /// <summary>
    /// The field holding a fresh identifier per write: Firestore leaves a document's update time unchanged by a write
    /// which changes none of its fields, so each write changes this one, and every applied write gives the register a
    /// version it never had (MS-I5).
    /// </summary>
    internal const string WriteFieldName = "write";

    private const int EncodedVersionLength = sizeof(long) + sizeof(int);

    private readonly FirestoreMetaStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="FirestoreConditionalRegister"/> class.
    /// </summary>
    public FirestoreConditionalRegister(FirestoreMetaStore store, string documentPath)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        _store = store;
        DocumentPath = documentPath;
    }

    /// <summary>
    /// Gets the document path relative to the database.
    /// </summary>
    internal string DocumentPath { get; }

    /// <inheritdoc/>
    public async ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var document = await GetDocumentAsync(cancellationToken).ConfigureAwait(false);
        DocumentSnapshot snapshot;
        try
        {
            snapshot = await document.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException exception) when (IsLocallyRequestedCancellation(exception, cancellationToken))
        {
            throw CreateCancellationException(exception, cancellationToken);
        }

        if (!snapshot.Exists)
        {
            return null;
        }

        var updateTime = snapshot.UpdateTime
            ?? throw new InvalidDataException("Firestore returned an existing document without an update time.");
        var value = snapshot.GetValue<byte[]>(new FieldPath(ValueFieldName));
        return new MetaStoreEntry(EncodeVersion(updateTime), ArcBuffer.Wrap(value));
    }

    /// <inheritdoc/>
    public async ValueTask<RegisterWriteOutcome> WriteAsync(
        MetaStoreVersion? expectedVersion,
        ReadOnlySequence<byte> value,
        CancellationToken cancellationToken = default)
    {
        Precondition? precondition = null;
        if (expectedVersion is not null)
        {
            // A token this backend cannot have issued matches no version of the register.
            precondition = TryCreatePrecondition(expectedVersion);
            if (precondition is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return RegisterWriteOutcome.Mismatch.Instance;
            }
        }

        var document = await GetDocumentAsync(cancellationToken).ConfigureAwait(false);

        // "The MetaStore component has a simple API: it is a single versioned register supporting a
        // conditional write. Reading the MetaStore returns a value with an attached version. Writing to it
        // requires supplying a new value and an expected existing version."
        // See research/papers/delos/paper.md, section 3.2.
        WriteResult result;
        try
        {
            result = precondition is null
                ? await document.CreateAsync(
                    CreateDocumentData(value),
                    cancellationToken).ConfigureAwait(false)
                : await document.UpdateAsync(
                    CreateDocumentUpdates(value),
                    precondition,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException exception) when (IsLocallyRequestedCancellation(exception, cancellationToken))
        {
            throw CreateCancellationException(exception, cancellationToken);
        }
        catch (RpcException exception) when (
            !cancellationToken.IsCancellationRequested
            && IsVersionConflict(exception.StatusCode, expectedVersion is null))
        {
            return RegisterWriteOutcome.Mismatch.Instance;
        }

        // The write's update time is the document's: the version a read of the value returns.
        return new RegisterWriteOutcome.Applied(EncodeVersion(result.UpdateTime));
    }

    internal static Dictionary<string, object> CreateDocumentData(in ReadOnlySequence<byte> value) =>
        new(StringComparer.Ordinal)
        {
            [ValueFieldName] = value.ToArray(),
            [WriteFieldName] = Guid.NewGuid().ToString("N"),
        };

    internal static Dictionary<FieldPath, object> CreateDocumentUpdates(in ReadOnlySequence<byte> value) =>
        new()
        {
            [new FieldPath(ValueFieldName)] = value.ToArray(),
            [new FieldPath(WriteFieldName)] = Guid.NewGuid().ToString("N"),
        };

    internal static bool IsLocallyRequestedCancellation(
        RpcException exception,
        CancellationToken cancellationToken) =>
        exception.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested;

    internal static OperationCanceledException CreateCancellationException(
        RpcException exception,
        CancellationToken cancellationToken) =>
        new("The Firestore operation was canceled.", exception, cancellationToken);

    internal static MetaStoreVersion EncodeVersion(Timestamp timestamp)
    {
        var proto = timestamp.ToProto();
        Span<byte> bytes = stackalloc byte[EncodedVersionLength];
        BinaryPrimitives.WriteInt64BigEndian(bytes, proto.Seconds);
        BinaryPrimitives.WriteInt32BigEndian(bytes[sizeof(long)..], proto.Nanos);
        return new MetaStoreVersion(Convert.ToBase64String(bytes));
    }

    /// <summary>
    /// Decodes a version into a last-update-time precondition, or returns <see langword="null"/> when the version
    /// is not a token this backend issues.
    /// </summary>
    internal static Precondition? TryCreatePrecondition(MetaStoreVersion version)
    {
        Span<byte> bytes = stackalloc byte[EncodedVersionLength];
        if (!Convert.TryFromBase64String(version.Value, bytes, out var written)
            || written != EncodedVersionLength
            || !string.Equals(Convert.ToBase64String(bytes), version.Value, StringComparison.Ordinal))
        {
            return null;
        }

        var seconds = BinaryPrimitives.ReadInt64BigEndian(bytes);
        var nanos = BinaryPrimitives.ReadInt32BigEndian(bytes[sizeof(long)..]);
        try
        {
            return Precondition.LastUpdated(
                Timestamp.FromProto(new ProtobufTimestamp { Seconds = seconds, Nanos = nanos }));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static bool IsVersionConflict(StatusCode statusCode, bool isCreate) =>
        isCreate
            ? statusCode == StatusCode.AlreadyExists
            : statusCode is StatusCode.FailedPrecondition or StatusCode.NotFound;

    private async ValueTask<DocumentReference> GetDocumentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = await _store.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        return database.Document(DocumentPath);
    }
}
