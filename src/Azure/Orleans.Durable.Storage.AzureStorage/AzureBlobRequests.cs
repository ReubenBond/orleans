using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Orleans.Durable.Storage.AzureStorage;

/// <summary>
/// The conditions Clog's Azure Blob writes carry and the reading of the service's answers, shared by every store over
/// Azure Blob Storage.
/// </summary>
internal static class AzureBlobRequests
{
    /// <summary>
    /// The service version the composition pins its container's client to: the version Clog's emulator and live runs
    /// exercise, whose <c>startFrom</c> listing the object store uses.
    /// </summary>
    internal const BlobClientOptions.ServiceVersion PinnedServiceVersion = BlobClientOptions.ServiceVersion.V2026_10_06;

    /// <summary>
    /// The least service version a client of Clog's blobs may use: 2022-11-02, from which <c>Append Block</c> takes
    /// <c>x-ms-if-tags</c> and 100 MiB blocks.
    /// </summary>
    internal const BlobClientOptions.ServiceVersion MinimumServiceVersion = BlobClientOptions.ServiceVersion.V2022_11_02;

    /// <summary>
    /// Returns the conditions of a write which expects the blob to hold <paramref name="expectedETag"/>, or to be absent
    /// when it is <see langword="null"/>: <c>If-Match</c>, or <c>If-None-Match: *</c>.
    /// </summary>
    internal static BlobRequestConditions ConditionsFor(string? expectedETag) =>
        expectedETag is null
            ? new BlobRequestConditions { IfNoneMatch = ETag.All }
            : new BlobRequestConditions { IfMatch = new ETag(expectedETag) };

    /// <summary>
    /// Returns whether <paramref name="exception"/> reports that a write's condition failed against the blob's durable
    /// state: 412 <c>ConditionNotMet</c>, or <c>AppendPositionConditionNotMet</c> for an append; or, for a write which
    /// expected no blob, 409 <c>BlobAlreadyExists</c>, and for one which expected a blob, 404 <c>BlobNotFound</c>. Any other
    /// answer, a lease's 412 or a missing container's 404 among them, is not the blob's condition and propagates.
    /// </summary>
    internal static bool IsConditionFailure(RequestFailedException exception, bool expectedAbsent) =>
        HasErrorCode(exception, 412, BlobErrorCode.ConditionNotMet)
        || HasErrorCode(exception, 412, BlobErrorCode.AppendPositionConditionNotMet)
        || (expectedAbsent
            ? HasErrorCode(exception, 409, BlobErrorCode.BlobAlreadyExists)
            : HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound));

    /// <summary>
    /// Returns the ETag an applied write gave the blob, formatted as a download's ETag is, so both paths yield the same
    /// version.
    /// </summary>
    /// <exception cref="IOException">The answer carries no ETag, so the write's outcome is unknown.</exception>
    internal static string AppliedETag(ETag etag) =>
        etag == default
            ? throw new IOException("Azure Blob answered an applied upload without the blob's ETag, so the write's outcome is unknown. Re-read the blob to establish the operation's goal.")
            : etag.ToString();

    /// <summary>
    /// Returns whether the service refused a request for a reason no repetition changes: 400, 401, 403, or 404, a request
    /// the service read and refused, as against 408, 409, 412, 429, a 5xx, or a failed transport, whose outcome is unknown.
    /// </summary>
    internal static bool IsDefiniteRefusal(RequestFailedException exception) => exception.Status is 400 or 401 or 403 or 404;

    /// <summary>
    /// Returns whether <paramref name="exception"/> carries <paramref name="status"/> and <paramref name="errorCode"/>.
    /// </summary>
    internal static bool HasErrorCode(RequestFailedException exception, int status, BlobErrorCode errorCode) =>
        exception.Status == status
        && string.Equals(exception.ErrorCode, errorCode.ToString(), StringComparison.Ordinal);
}
