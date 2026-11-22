using System.Buffers;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.AzureStorage.MetaStore;

/// <summary>
/// One register in an Azure block blob whose ETag is the register version.
/// </summary>
/// <remarks>
/// A write copies its value into the <see cref="BinaryData"/> it uploads, before the request is dispatched; a read's
/// entry wraps the array of the <see cref="BinaryData"/> the download returns, without a copy.
/// </remarks>
internal sealed class AzureBlobConditionalRegister : IConditionalRegister
{
    private readonly BlobClient _blob;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBlobConditionalRegister"/> class.
    /// </summary>
    public AzureBlobConditionalRegister(BlobClient blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        _blob = blob;
    }

    /// <summary>
    /// Gets the register's blob name within its container.
    /// </summary>
    internal string BlobName => _blob.Name;

    /// <inheritdoc/>
    public async ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return MapEntry(response.Value.Content, response.Value.Details.ETag);
        }
        catch (RequestFailedException exception) when (
            !cancellationToken.IsCancellationRequested
            && AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound))
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<RegisterWriteOutcome> WriteAsync(
        MetaStoreVersion? expectedVersion,
        ReadOnlySequence<byte> value,
        CancellationToken cancellationToken = default)
    {
        // "The MetaStore component has a simple API: it is a single versioned register supporting a
        // conditional write. Reading the MetaStore returns a value with an attached version. Writing to it
        // requires supplying a new value and an expected existing version."
        // See research/papers/delos/paper.md, section 3.2.
        BlobContentInfo uploaded;
        try
        {
            uploaded = (await _blob.UploadAsync(
                BinaryData.FromBytes(value.ToArray()),
                new BlobUploadOptions { Conditions = AzureBlobRequests.ConditionsFor(expectedVersion?.Value) },
                cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException exception) when (
            !cancellationToken.IsCancellationRequested
            && AzureBlobRequests.IsConditionFailure(exception, expectedAbsent: expectedVersion is null))
        {
            return RegisterWriteOutcome.Mismatch.Instance;
        }

        return new RegisterWriteOutcome.Applied(new MetaStoreVersion(AzureBlobRequests.AppliedETag(uploaded.ETag)));
    }

    internal static MetaStoreEntry MapEntry(BinaryData content, ETag etag) =>
        new(new MetaStoreVersion(etag.ToString()), ArcBuffer.Wrap(content.ToMemory()));
}
