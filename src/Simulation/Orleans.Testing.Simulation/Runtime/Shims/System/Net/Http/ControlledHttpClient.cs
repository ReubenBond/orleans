using System.Collections.Concurrent;
using System.Net.Http;
using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Net.Http;

/// <summary>
/// Controlled <see cref="HttpClient"/> sending members. The BCL bounds every request with the client's
/// <see cref="HttpClient.Timeout"/> by arming a real timer (<c>CancellationTokenSource.CancelAfter</c>), which would
/// cancel the request by wall-clock time. Each shim requires an infinite timeout and then calls the same BCL member, so
/// the request, its validation, and its outcome are the BCL's. A client with a finite timeout, which every client has
/// by default (100 seconds), throws <see cref="SimulationApiException"/>: set
/// <c>Timeout = Timeout.InfiniteTimeSpan</c> and bound the request with a token which a virtual delay cancels, such as a
/// <c>CancellationTokenSource</c> constructed with a timeout. The virtual <c>Send</c> and <c>SendAsync</c> check the
/// timeout only where they run <see cref="HttpClient"/>'s implementation: an override is called as written, and its base
/// call runs <see cref="HttpClient"/>'s implementation, checked.
/// </summary>
public static class ControlledHttpClient
{
    private const string Api = "System.Net.Http.HttpClient";

    // Whether a client type's Send or SendAsync is HttpClient's own implementation, by type and member.
    private static readonly ConcurrentDictionary<(Type Type, string Member), bool> ImplementedByHttpClient = new();

    private static readonly Func<HttpClient, HttpRequestMessage, CancellationToken, HttpResponseMessage> BaseSendCall =
        NonVirtualCall.Create<Func<HttpClient, HttpRequestMessage, CancellationToken, HttpResponseMessage>>(
            typeof(HttpClient), nameof(HttpClient.Send), typeof(HttpRequestMessage), typeof(CancellationToken));

    private static readonly Func<HttpClient, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> BaseSendAsyncCall =
        NonVirtualCall.Create<Func<HttpClient, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>(
            typeof(HttpClient), nameof(HttpClient.SendAsync), typeof(HttpRequestMessage), typeof(CancellationToken));

    /// <summary>Controlled <c>HttpClient.DeleteAsync(string?)</c>.</summary>
    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string? requestUri)
    {
        RequireInfiniteTimeout(client, "DeleteAsync");
        return client.DeleteAsync(requestUri);
    }

    /// <summary>Controlled <c>HttpClient.DeleteAsync(string?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteAsync");
        return client.DeleteAsync(requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.DeleteAsync(Uri?)</c>.</summary>
    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Uri? requestUri)
    {
        RequireInfiniteTimeout(client, "DeleteAsync");
        return client.DeleteAsync(requestUri);
    }

    /// <summary>Controlled <c>HttpClient.DeleteAsync(Uri?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Uri? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteAsync");
        return client.DeleteAsync(requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.GetAsync(string?)</c>.</summary>
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string? requestUri)
    {
        RequireInfiniteTimeout(client, "GetAsync");
        return client.GetAsync(requestUri);
    }

    /// <summary>Controlled <c>HttpClient.GetAsync(string?, HttpCompletionOption)</c>.</summary>
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string? requestUri, HttpCompletionOption completionOption)
    {
        RequireInfiniteTimeout(client, "GetAsync");
        return client.GetAsync(requestUri, completionOption);
    }

    /// <summary>Controlled <c>HttpClient.GetAsync(string?, HttpCompletionOption, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string? requestUri, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetAsync");
        return client.GetAsync(requestUri, completionOption, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.GetAsync(string?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetAsync");
        return client.GetAsync(requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.GetAsync(Uri?)</c>.</summary>
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, Uri? requestUri)
    {
        RequireInfiniteTimeout(client, "GetAsync");
        return client.GetAsync(requestUri);
    }

    /// <summary>Controlled <c>HttpClient.GetAsync(Uri?, HttpCompletionOption)</c>.</summary>
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, Uri? requestUri, HttpCompletionOption completionOption)
    {
        RequireInfiniteTimeout(client, "GetAsync");
        return client.GetAsync(requestUri, completionOption);
    }

    /// <summary>Controlled <c>HttpClient.GetAsync(Uri?, HttpCompletionOption, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, Uri? requestUri, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetAsync");
        return client.GetAsync(requestUri, completionOption, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.GetAsync(Uri?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, Uri? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetAsync");
        return client.GetAsync(requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.GetByteArrayAsync(string?)</c>.</summary>
    public static Task<byte[]> GetByteArrayAsync(HttpClient client, string? requestUri)
    {
        RequireInfiniteTimeout(client, "GetByteArrayAsync");
        return client.GetByteArrayAsync(requestUri);
    }

    /// <summary>Controlled <c>HttpClient.GetByteArrayAsync(string?, CancellationToken)</c>.</summary>
    public static Task<byte[]> GetByteArrayAsync(HttpClient client, string? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetByteArrayAsync");
        return client.GetByteArrayAsync(requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.GetByteArrayAsync(Uri?)</c>.</summary>
    public static Task<byte[]> GetByteArrayAsync(HttpClient client, Uri? requestUri)
    {
        RequireInfiniteTimeout(client, "GetByteArrayAsync");
        return client.GetByteArrayAsync(requestUri);
    }

    /// <summary>Controlled <c>HttpClient.GetByteArrayAsync(Uri?, CancellationToken)</c>.</summary>
    public static Task<byte[]> GetByteArrayAsync(HttpClient client, Uri? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetByteArrayAsync");
        return client.GetByteArrayAsync(requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.GetStreamAsync(string?)</c>.</summary>
    public static Task<Stream> GetStreamAsync(HttpClient client, string? requestUri)
    {
        RequireInfiniteTimeout(client, "GetStreamAsync");
        return client.GetStreamAsync(requestUri);
    }

    /// <summary>Controlled <c>HttpClient.GetStreamAsync(string?, CancellationToken)</c>.</summary>
    public static Task<Stream> GetStreamAsync(HttpClient client, string? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetStreamAsync");
        return client.GetStreamAsync(requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.GetStreamAsync(Uri?)</c>.</summary>
    public static Task<Stream> GetStreamAsync(HttpClient client, Uri? requestUri)
    {
        RequireInfiniteTimeout(client, "GetStreamAsync");
        return client.GetStreamAsync(requestUri);
    }

    /// <summary>Controlled <c>HttpClient.GetStreamAsync(Uri?, CancellationToken)</c>.</summary>
    public static Task<Stream> GetStreamAsync(HttpClient client, Uri? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetStreamAsync");
        return client.GetStreamAsync(requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.GetStringAsync(string?)</c>.</summary>
    public static Task<string> GetStringAsync(HttpClient client, string? requestUri)
    {
        RequireInfiniteTimeout(client, "GetStringAsync");
        return client.GetStringAsync(requestUri);
    }

    /// <summary>Controlled <c>HttpClient.GetStringAsync(string?, CancellationToken)</c>.</summary>
    public static Task<string> GetStringAsync(HttpClient client, string? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetStringAsync");
        return client.GetStringAsync(requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.GetStringAsync(Uri?)</c>.</summary>
    public static Task<string> GetStringAsync(HttpClient client, Uri? requestUri)
    {
        RequireInfiniteTimeout(client, "GetStringAsync");
        return client.GetStringAsync(requestUri);
    }

    /// <summary>Controlled <c>HttpClient.GetStringAsync(Uri?, CancellationToken)</c>.</summary>
    public static Task<string> GetStringAsync(HttpClient client, Uri? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetStringAsync");
        return client.GetStringAsync(requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.PatchAsync(string?, HttpContent?)</c>.</summary>
    public static Task<HttpResponseMessage> PatchAsync(HttpClient client, string? requestUri, HttpContent? content)
    {
        RequireInfiniteTimeout(client, "PatchAsync");
        return client.PatchAsync(requestUri, content);
    }

    /// <summary>Controlled <c>HttpClient.PatchAsync(string?, HttpContent?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PatchAsync(HttpClient client, string? requestUri, HttpContent? content, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PatchAsync");
        return client.PatchAsync(requestUri, content, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.PatchAsync(Uri?, HttpContent?)</c>.</summary>
    public static Task<HttpResponseMessage> PatchAsync(HttpClient client, Uri? requestUri, HttpContent? content)
    {
        RequireInfiniteTimeout(client, "PatchAsync");
        return client.PatchAsync(requestUri, content);
    }

    /// <summary>Controlled <c>HttpClient.PatchAsync(Uri?, HttpContent?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PatchAsync(HttpClient client, Uri? requestUri, HttpContent? content, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PatchAsync");
        return client.PatchAsync(requestUri, content, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.PostAsync(string?, HttpContent?)</c>.</summary>
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string? requestUri, HttpContent? content)
    {
        RequireInfiniteTimeout(client, "PostAsync");
        return client.PostAsync(requestUri, content);
    }

    /// <summary>Controlled <c>HttpClient.PostAsync(string?, HttpContent?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string? requestUri, HttpContent? content, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PostAsync");
        return client.PostAsync(requestUri, content, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.PostAsync(Uri?, HttpContent?)</c>.</summary>
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, Uri? requestUri, HttpContent? content)
    {
        RequireInfiniteTimeout(client, "PostAsync");
        return client.PostAsync(requestUri, content);
    }

    /// <summary>Controlled <c>HttpClient.PostAsync(Uri?, HttpContent?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, Uri? requestUri, HttpContent? content, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PostAsync");
        return client.PostAsync(requestUri, content, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.PutAsync(string?, HttpContent?)</c>.</summary>
    public static Task<HttpResponseMessage> PutAsync(HttpClient client, string? requestUri, HttpContent? content)
    {
        RequireInfiniteTimeout(client, "PutAsync");
        return client.PutAsync(requestUri, content);
    }

    /// <summary>Controlled <c>HttpClient.PutAsync(string?, HttpContent?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PutAsync(HttpClient client, string? requestUri, HttpContent? content, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PutAsync");
        return client.PutAsync(requestUri, content, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.PutAsync(Uri?, HttpContent?)</c>.</summary>
    public static Task<HttpResponseMessage> PutAsync(HttpClient client, Uri? requestUri, HttpContent? content)
    {
        RequireInfiniteTimeout(client, "PutAsync");
        return client.PutAsync(requestUri, content);
    }

    /// <summary>Controlled <c>HttpClient.PutAsync(Uri?, HttpContent?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PutAsync(HttpClient client, Uri? requestUri, HttpContent? content, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PutAsync");
        return client.PutAsync(requestUri, content, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.Send(HttpRequestMessage)</c>.</summary>
    public static HttpResponseMessage Send(HttpClient client, HttpRequestMessage request)
    {
        RequireInfiniteTimeout(client, "Send");
        return client.Send(request);
    }

    /// <summary>Controlled <c>HttpClient.Send(HttpRequestMessage, HttpCompletionOption)</c>.</summary>
    public static HttpResponseMessage Send(HttpClient client, HttpRequestMessage request, HttpCompletionOption completionOption)
    {
        RequireInfiniteTimeout(client, "Send");
        return client.Send(request, completionOption);
    }

    /// <summary>Controlled <c>HttpClient.Send(HttpRequestMessage, HttpCompletionOption, CancellationToken)</c>.</summary>
    public static HttpResponseMessage Send(HttpClient client, HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "Send");
        return client.Send(request, completionOption, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.Send(HttpRequestMessage, CancellationToken)</c>.</summary>
    public static HttpResponseMessage Send(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeoutIfRunByHttpClient(client, "Send");
        return client.Send(request, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.SendAsync(HttpRequestMessage)</c>.</summary>
    public static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        RequireInfiniteTimeout(client, "SendAsync");
        return client.SendAsync(request);
    }

    /// <summary>Controlled <c>HttpClient.SendAsync(HttpRequestMessage, HttpCompletionOption)</c>.</summary>
    public static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, HttpCompletionOption completionOption)
    {
        RequireInfiniteTimeout(client, "SendAsync");
        return client.SendAsync(request, completionOption);
    }

    /// <summary>Controlled <c>HttpClient.SendAsync(HttpRequestMessage, HttpCompletionOption, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "SendAsync");
        return client.SendAsync(request, completionOption, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClient.SendAsync(HttpRequestMessage, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeoutIfRunByHttpClient(client, "SendAsync");
        return client.SendAsync(request, cancellationToken);
    }

    /// <summary>The base call <c>HttpClient.Send(HttpRequestMessage, CancellationToken)</c>, from an override.</summary>
    public static HttpResponseMessage BaseSend(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "Send");
        return BaseSendCall(client, request, cancellationToken);
    }

    /// <summary>The base call <c>HttpClient.SendAsync(HttpRequestMessage, CancellationToken)</c>, from an override.</summary>
    public static Task<HttpResponseMessage> BaseSendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "SendAsync");
        return BaseSendAsyncCall(client, request, cancellationToken);
    }

    private static void RequireInfiniteTimeout(HttpClient client, string member) =>
        RequireInfiniteTimeoutFor(client, Api + "." + member);

    // The virtual Send and SendAsync arm the timer only in HttpClient's implementation: an override which answers without
    // calling it starts none, and one which calls it does so through a base call, which BaseSend and BaseSendAsync check.
    private static void RequireInfiniteTimeoutIfRunByHttpClient(HttpClient client, string member)
    {
        if (client is null || RunsHttpClientImplementation(client, member))
        {
            RequireInfiniteTimeoutFor(client!, Api + "." + member);
            return;
        }

        SimulationRuntimeDispatch.RequireActiveSimulation(Api + "." + member);
    }

    /// <summary>
    /// Whether <paramref name="client"/>'s virtual <c>Send</c> or <c>SendAsync</c> with a request and a token is
    /// <see cref="HttpClient"/>'s own implementation, the one which bounds the request with the client's timeout.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="member">The member: <c>Send</c> or <c>SendAsync</c>.</param>
    /// <returns><see langword="true"/> unless the client's type overrides the member.</returns>
    internal static bool RunsHttpClientImplementation(HttpClient client, string member) =>
        ImplementedByHttpClient.GetOrAdd(
            (client.GetType(), member),
            static key => key.Type.GetMethod(key.Member, [typeof(HttpRequestMessage), typeof(CancellationToken)])!.DeclaringType == typeof(HttpClient));
    /// <summary>
    /// Requires an active simulation and a client whose requests a real timer does not bound. A null client fails with the
    /// <see cref="NullReferenceException"/> the BCL's instance call does.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="api">The sending API.</param>
    internal static void RequireInfiniteTimeoutFor(HttpClient client, string api)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(api);
        if (client.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new SimulationApiException(
                SimulationApiCategory.Timer,
                api,
                "HttpClient bounds every request with its Timeout on a real timer, so the request would be cancelled by " +
                "wall-clock time. Set HttpClient.Timeout to Timeout.InfiniteTimeSpan and bound the request with a " +
                "CancellationToken which a virtual delay cancels, such as one from a CancellationTokenSource constructed " +
                "with a timeout.");
        }
    }
}
