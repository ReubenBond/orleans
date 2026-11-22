using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// Canned answers of Azure Blob Storage, S3, and Cloud Storage, in the shapes their SDKs parse, for stores over a
/// <see cref="TransportDouble"/> which stubs the service.
/// </summary>
internal static class StubResponses
{
    internal const string StubETag = "\"0x8DD0000000000A1\"";

    internal static readonly Uri Unserved = new("http://127.0.0.1:9");

    private static readonly string LastModified = DateTimeOffset.UnixEpoch.ToString("R");

    /// <summary>
    /// Returns an answer of <paramref name="status"/> with no body.
    /// </summary>
    internal static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    internal static HttpResponseMessage AzureError(HttpStatusCode status, string code)
    {
        var response = Text(status, $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>{code}</Code><Message>{code}</Message></Error>", "application/xml");
        response.Headers.Add("x-ms-error-code", code);
        return response;
    }

    internal static HttpResponseMessage AzureWritten(string etag = StubETag)
    {
        var response = Status(HttpStatusCode.Created);
        _ = response.Headers.TryAddWithoutValidation("ETag", etag);
        _ = response.Content.Headers.TryAddWithoutValidation("Last-Modified", LastModified);
        return response;
    }

    internal static HttpResponseMessage AzureRange(byte[] bytes, long from, long total) =>
        Ranged(bytes, from, total, static response =>
        {
            response.Headers.Add("x-ms-blob-type", "BlockBlob");
            _ = response.Headers.TryAddWithoutValidation("ETag", StubETag);
        });

    internal static HttpResponseMessage AzureListing(string prefix, string[] names, string? nextMarker)
    {
        var blobs = string.Concat(names.Select(name =>
            $"<Blob><Name>{prefix}{name}</Name><Properties><Content-Length>3</Content-Length><BlobType>BlockBlob</BlobType></Properties></Blob>"));
        return Text(
            HttpStatusCode.OK,
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><EnumerationResults ServiceEndpoint=\"{Unserved}\" ContainerName=\"{CloudEmulators.ContainerName}\"><Blobs>{blobs}</Blobs><NextMarker>{nextMarker}</NextMarker></EnumerationResults>",
            "application/xml");
    }

    internal static HttpResponseMessage S3Error(HttpStatusCode status, string code) =>
        Text(status, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{code}</Message><RequestId>1</RequestId></Error>", "application/xml");

    internal static HttpResponseMessage S3Written(string etag = StubETag)
    {
        var response = Status(HttpStatusCode.OK);
        _ = response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    internal static HttpResponseMessage S3Range(byte[] bytes, long from, long total) =>
        Ranged(bytes, from, total, static response => _ = response.Headers.TryAddWithoutValidation("ETag", StubETag));

    internal static HttpResponseMessage S3Listing(string prefix, string[] names, string? continuation)
    {
        var contents = string.Concat(names.Select(name => $"<Contents><Key>{prefix}{name}</Key><Size>3</Size></Contents>"));
        var truncated = continuation is null ? "false" : "true";
        return Text(
            HttpStatusCode.OK,
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>{CloudEmulators.ContainerName}</Name><KeyCount>{names.Length}</KeyCount><MaxKeys>1000</MaxKeys><IsTruncated>{truncated}</IsTruncated><NextContinuationToken>{continuation}</NextContinuationToken>{contents}</ListBucketResult>",
            "application/xml");
    }

    internal static HttpResponseMessage GcsError(HttpStatusCode status, string? reason)
    {
        var errors = reason is null ? string.Empty : $",\"errors\":[{{\"domain\":\"global\",\"reason\":\"{reason}\",\"message\":\"{reason}\"}}]";
        return Text(status, $"{{\"error\":{{\"code\":{(int)status},\"message\":\"{reason}\"{errors}}}}}", "application/json");
    }

    internal static HttpResponseMessage GcsSession(Uri? endpoint = null)
    {
        var response = Status(HttpStatusCode.OK);
        response.Headers.Location = new Uri(endpoint ?? Unserved, "upload/storage/v1/b/durable-storage-objects/o?uploadType=resumable&upload_id=stub");
        return response;
    }

    internal static HttpResponseMessage GcsObject(string name, long generation, long size, string? crc32c = null, string? md5 = null) =>
        Text(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["bucket"] = CloudEmulators.ContainerName,
                ["name"] = name,
                ["generation"] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["size"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["crc32c"] = crc32c,
                ["md5Hash"] = md5,
            }),
            "application/json");

    internal static HttpResponseMessage GcsRange(byte[] bytes, long from, long total) => Ranged(bytes, from, total, static _ => { });

    internal static HttpResponseMessage GcsListing(string prefix, string[] names, string? pageToken)
    {
        var items = string.Join(",", names.Select(name => $"{{\"name\":\"{prefix}{name}\",\"size\":\"3\"}}"));
        var token = pageToken is null ? string.Empty : $",\"nextPageToken\":\"{pageToken}\"";
        return Text(HttpStatusCode.OK, $"{{\"items\":[{items}]{token}}}", "application/json");
    }

    /// <summary>
    /// Returns the request's body, decompressed when the client compressed it.
    /// </summary>
    internal static string BodyText(StubbedRequest request)
    {
        if (request.Header("Content-Encoding") is "gzip")
        {
            using var compressed = new MemoryStream(request.Body);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        return Encoding.UTF8.GetString(request.Body);
    }

    /// <summary>
    /// Returns the base64 MD5 of <paramref name="content"/>.
    /// </summary>
    internal static string Md5Base64(byte[] content)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(content);
        return Convert.ToBase64String(md5.GetHashAndReset());
    }

    /// <summary>
    /// Returns the base64 big-endian CRC-32C of <paramref name="content"/>, computed apart from the stores' helper.
    /// </summary>
    internal static string Crc32CBase64(byte[] content) => Crc32CBase64(Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, content));

    /// <summary>
    /// Returns the base64 of the big-endian <paramref name="crc32c"/>, as the services carry it, computed apart from the
    /// stores' helper.
    /// </summary>
    internal static string Crc32CBase64(uint crc32c)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, crc32c);
        return Convert.ToBase64String(bytes);
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string body, string contentType)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private static HttpResponseMessage Ranged(byte[] bytes, long from, long total, Action<HttpResponseMessage> decorate)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentRange = new ContentRangeHeaderValue(from, from + Math.Max(bytes.Length, 1) - 1, total);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        _ = content.Headers.TryAddWithoutValidation("Last-Modified", LastModified);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        decorate(response);
        return response;
    }
}
