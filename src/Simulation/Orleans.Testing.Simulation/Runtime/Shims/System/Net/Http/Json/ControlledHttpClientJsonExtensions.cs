using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Net.Http.Json;

/// <summary>
/// Controlled <see cref="HttpClientJsonExtensions"/> members. Each sends its request through the given
/// <see cref="HttpClient"/>, and the BCL bounds both the request and the reading of its response with the client's
/// <see cref="HttpClient.Timeout"/> on a real timer, so each shim requires an infinite timeout, as the
/// <see cref="ControlledHttpClient"/> shims do, and then calls the same BCL member.
/// </summary>
public static class ControlledHttpClientJsonExtensions
{
    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync(HttpClient, string?, Type, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<object?> DeleteFromJsonAsync(HttpClient client, string? requestUri, Type type, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync(client, requestUri, type, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync(HttpClient, string?, Type, JsonSerializerContext, CancellationToken)</c>.</summary>
    public static Task<object?> DeleteFromJsonAsync(HttpClient client, string? requestUri, Type type, JsonSerializerContext context, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync(client, requestUri, type, context, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync(HttpClient, string?, Type, CancellationToken)</c>.</summary>
    public static Task<object?> DeleteFromJsonAsync(HttpClient client, string? requestUri, Type type, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync(client, requestUri, type, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync(HttpClient, Uri?, Type, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<object?> DeleteFromJsonAsync(HttpClient client, Uri? requestUri, Type type, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync(client, requestUri, type, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync(HttpClient, Uri?, Type, JsonSerializerContext, CancellationToken)</c>.</summary>
    public static Task<object?> DeleteFromJsonAsync(HttpClient client, Uri? requestUri, Type type, JsonSerializerContext context, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync(client, requestUri, type, context, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync(HttpClient, Uri?, Type, CancellationToken)</c>.</summary>
    public static Task<object?> DeleteFromJsonAsync(HttpClient client, Uri? requestUri, Type type, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync(client, requestUri, type, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync&lt;TValue&gt;(HttpClient, string?, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<TValue?> DeleteFromJsonAsync<TValue>(HttpClient client, string? requestUri, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync<TValue>(client, requestUri, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync&lt;TValue&gt;(HttpClient, string?, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static Task<TValue?> DeleteFromJsonAsync<TValue>(HttpClient client, string? requestUri, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync<TValue>(client, requestUri, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync&lt;TValue&gt;(HttpClient, string?, CancellationToken)</c>.</summary>
    public static Task<TValue?> DeleteFromJsonAsync<TValue>(HttpClient client, string? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync<TValue>(client, requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync&lt;TValue&gt;(HttpClient, Uri?, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<TValue?> DeleteFromJsonAsync<TValue>(HttpClient client, Uri? requestUri, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync<TValue>(client, requestUri, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync&lt;TValue&gt;(HttpClient, Uri?, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static Task<TValue?> DeleteFromJsonAsync<TValue>(HttpClient client, Uri? requestUri, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync<TValue>(client, requestUri, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.DeleteFromJsonAsync&lt;TValue&gt;(HttpClient, Uri?, CancellationToken)</c>.</summary>
    public static Task<TValue?> DeleteFromJsonAsync<TValue>(HttpClient client, Uri? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "DeleteFromJsonAsync");
        return HttpClientJsonExtensions.DeleteFromJsonAsync<TValue>(client, requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable&lt;TValue&gt;(HttpClient, string?, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static IAsyncEnumerable<TValue?> GetFromJsonAsAsyncEnumerable<TValue>(HttpClient client, string? requestUri, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsAsyncEnumerable");
        return HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable<TValue>(client, requestUri, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable&lt;TValue&gt;(HttpClient, string?, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static IAsyncEnumerable<TValue?> GetFromJsonAsAsyncEnumerable<TValue>(HttpClient client, string? requestUri, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsAsyncEnumerable");
        return HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable<TValue>(client, requestUri, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable&lt;TValue&gt;(HttpClient, string?, CancellationToken)</c>.</summary>
    public static IAsyncEnumerable<TValue?> GetFromJsonAsAsyncEnumerable<TValue>(HttpClient client, string? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsAsyncEnumerable");
        return HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable<TValue>(client, requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable&lt;TValue&gt;(HttpClient, Uri?, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static IAsyncEnumerable<TValue?> GetFromJsonAsAsyncEnumerable<TValue>(HttpClient client, Uri? requestUri, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsAsyncEnumerable");
        return HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable<TValue>(client, requestUri, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable&lt;TValue&gt;(HttpClient, Uri?, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static IAsyncEnumerable<TValue?> GetFromJsonAsAsyncEnumerable<TValue>(HttpClient client, Uri? requestUri, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsAsyncEnumerable");
        return HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable<TValue>(client, requestUri, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable&lt;TValue&gt;(HttpClient, Uri?, CancellationToken)</c>.</summary>
    public static IAsyncEnumerable<TValue?> GetFromJsonAsAsyncEnumerable<TValue>(HttpClient client, Uri? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsAsyncEnumerable");
        return HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable<TValue>(client, requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync(HttpClient, string?, Type, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<object?> GetFromJsonAsync(HttpClient client, string? requestUri, Type type, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync(client, requestUri, type, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync(HttpClient, string?, Type, JsonSerializerContext, CancellationToken)</c>.</summary>
    public static Task<object?> GetFromJsonAsync(HttpClient client, string? requestUri, Type type, JsonSerializerContext context, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync(client, requestUri, type, context, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync(HttpClient, string?, Type, CancellationToken)</c>.</summary>
    public static Task<object?> GetFromJsonAsync(HttpClient client, string? requestUri, Type type, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync(client, requestUri, type, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync(HttpClient, Uri?, Type, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<object?> GetFromJsonAsync(HttpClient client, Uri? requestUri, Type type, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync(client, requestUri, type, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync(HttpClient, Uri?, Type, JsonSerializerContext, CancellationToken)</c>.</summary>
    public static Task<object?> GetFromJsonAsync(HttpClient client, Uri? requestUri, Type type, JsonSerializerContext context, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync(client, requestUri, type, context, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync(HttpClient, Uri?, Type, CancellationToken)</c>.</summary>
    public static Task<object?> GetFromJsonAsync(HttpClient client, Uri? requestUri, Type type, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync(client, requestUri, type, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync&lt;TValue&gt;(HttpClient, string?, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<TValue?> GetFromJsonAsync<TValue>(HttpClient client, string? requestUri, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync<TValue>(client, requestUri, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync&lt;TValue&gt;(HttpClient, string?, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static Task<TValue?> GetFromJsonAsync<TValue>(HttpClient client, string? requestUri, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync<TValue>(client, requestUri, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync&lt;TValue&gt;(HttpClient, string?, CancellationToken)</c>.</summary>
    public static Task<TValue?> GetFromJsonAsync<TValue>(HttpClient client, string? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync<TValue>(client, requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync&lt;TValue&gt;(HttpClient, Uri?, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<TValue?> GetFromJsonAsync<TValue>(HttpClient client, Uri? requestUri, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync<TValue>(client, requestUri, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync&lt;TValue&gt;(HttpClient, Uri?, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static Task<TValue?> GetFromJsonAsync<TValue>(HttpClient client, Uri? requestUri, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync<TValue>(client, requestUri, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.GetFromJsonAsync&lt;TValue&gt;(HttpClient, Uri?, CancellationToken)</c>.</summary>
    public static Task<TValue?> GetFromJsonAsync<TValue>(HttpClient client, Uri? requestUri, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "GetFromJsonAsync");
        return HttpClientJsonExtensions.GetFromJsonAsync<TValue>(client, requestUri, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PatchAsJsonAsync&lt;TValue&gt;(HttpClient, string?, TValue, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PatchAsJsonAsync<TValue>(HttpClient client, string? requestUri, TValue value, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PatchAsJsonAsync");
        return HttpClientJsonExtensions.PatchAsJsonAsync<TValue>(client, requestUri, value, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PatchAsJsonAsync&lt;TValue&gt;(HttpClient, string?, TValue, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PatchAsJsonAsync<TValue>(HttpClient client, string? requestUri, TValue value, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PatchAsJsonAsync");
        return HttpClientJsonExtensions.PatchAsJsonAsync<TValue>(client, requestUri, value, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PatchAsJsonAsync&lt;TValue&gt;(HttpClient, string?, TValue, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PatchAsJsonAsync<TValue>(HttpClient client, string? requestUri, TValue value, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PatchAsJsonAsync");
        return HttpClientJsonExtensions.PatchAsJsonAsync<TValue>(client, requestUri, value, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PatchAsJsonAsync&lt;TValue&gt;(HttpClient, Uri?, TValue, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PatchAsJsonAsync<TValue>(HttpClient client, Uri? requestUri, TValue value, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PatchAsJsonAsync");
        return HttpClientJsonExtensions.PatchAsJsonAsync<TValue>(client, requestUri, value, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PatchAsJsonAsync&lt;TValue&gt;(HttpClient, Uri?, TValue, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PatchAsJsonAsync<TValue>(HttpClient client, Uri? requestUri, TValue value, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PatchAsJsonAsync");
        return HttpClientJsonExtensions.PatchAsJsonAsync<TValue>(client, requestUri, value, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PatchAsJsonAsync&lt;TValue&gt;(HttpClient, Uri?, TValue, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PatchAsJsonAsync<TValue>(HttpClient client, Uri? requestUri, TValue value, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PatchAsJsonAsync");
        return HttpClientJsonExtensions.PatchAsJsonAsync<TValue>(client, requestUri, value, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PostAsJsonAsync&lt;TValue&gt;(HttpClient, string?, TValue, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PostAsJsonAsync<TValue>(HttpClient client, string? requestUri, TValue value, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PostAsJsonAsync");
        return HttpClientJsonExtensions.PostAsJsonAsync<TValue>(client, requestUri, value, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PostAsJsonAsync&lt;TValue&gt;(HttpClient, string?, TValue, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PostAsJsonAsync<TValue>(HttpClient client, string? requestUri, TValue value, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PostAsJsonAsync");
        return HttpClientJsonExtensions.PostAsJsonAsync<TValue>(client, requestUri, value, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PostAsJsonAsync&lt;TValue&gt;(HttpClient, string?, TValue, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PostAsJsonAsync<TValue>(HttpClient client, string? requestUri, TValue value, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PostAsJsonAsync");
        return HttpClientJsonExtensions.PostAsJsonAsync<TValue>(client, requestUri, value, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PostAsJsonAsync&lt;TValue&gt;(HttpClient, Uri?, TValue, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PostAsJsonAsync<TValue>(HttpClient client, Uri? requestUri, TValue value, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PostAsJsonAsync");
        return HttpClientJsonExtensions.PostAsJsonAsync<TValue>(client, requestUri, value, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PostAsJsonAsync&lt;TValue&gt;(HttpClient, Uri?, TValue, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PostAsJsonAsync<TValue>(HttpClient client, Uri? requestUri, TValue value, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PostAsJsonAsync");
        return HttpClientJsonExtensions.PostAsJsonAsync<TValue>(client, requestUri, value, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PostAsJsonAsync&lt;TValue&gt;(HttpClient, Uri?, TValue, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PostAsJsonAsync<TValue>(HttpClient client, Uri? requestUri, TValue value, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PostAsJsonAsync");
        return HttpClientJsonExtensions.PostAsJsonAsync<TValue>(client, requestUri, value, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PutAsJsonAsync&lt;TValue&gt;(HttpClient, string?, TValue, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PutAsJsonAsync<TValue>(HttpClient client, string? requestUri, TValue value, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PutAsJsonAsync");
        return HttpClientJsonExtensions.PutAsJsonAsync<TValue>(client, requestUri, value, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PutAsJsonAsync&lt;TValue&gt;(HttpClient, string?, TValue, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PutAsJsonAsync<TValue>(HttpClient client, string? requestUri, TValue value, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PutAsJsonAsync");
        return HttpClientJsonExtensions.PutAsJsonAsync<TValue>(client, requestUri, value, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PutAsJsonAsync&lt;TValue&gt;(HttpClient, string?, TValue, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PutAsJsonAsync<TValue>(HttpClient client, string? requestUri, TValue value, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PutAsJsonAsync");
        return HttpClientJsonExtensions.PutAsJsonAsync<TValue>(client, requestUri, value, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PutAsJsonAsync&lt;TValue&gt;(HttpClient, Uri?, TValue, JsonSerializerOptions?, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PutAsJsonAsync<TValue>(HttpClient client, Uri? requestUri, TValue value, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PutAsJsonAsync");
        return HttpClientJsonExtensions.PutAsJsonAsync<TValue>(client, requestUri, value, options, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PutAsJsonAsync&lt;TValue&gt;(HttpClient, Uri?, TValue, JsonTypeInfo&lt;TValue&gt;, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PutAsJsonAsync<TValue>(HttpClient client, Uri? requestUri, TValue value, JsonTypeInfo<TValue> jsonTypeInfo, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PutAsJsonAsync");
        return HttpClientJsonExtensions.PutAsJsonAsync<TValue>(client, requestUri, value, jsonTypeInfo, cancellationToken);
    }

    /// <summary>Controlled <c>HttpClientJsonExtensions.PutAsJsonAsync&lt;TValue&gt;(HttpClient, Uri?, TValue, CancellationToken)</c>.</summary>
    public static Task<HttpResponseMessage> PutAsJsonAsync<TValue>(HttpClient client, Uri? requestUri, TValue value, CancellationToken cancellationToken)
    {
        RequireInfiniteTimeout(client, "PutAsJsonAsync");
        return HttpClientJsonExtensions.PutAsJsonAsync<TValue>(client, requestUri, value, cancellationToken);
    }

    // The BCL validates the client before anything else.
    private static void RequireInfiniteTimeout(HttpClient client, string member)
    {
        string api = "System.Net.Http.Json.HttpClientJsonExtensions." + member;
        SimulationRuntimeDispatch.RequireActiveSimulation(api);
        ArgumentNullException.ThrowIfNull(client);
        ControlledHttpClient.RequireInfiniteTimeoutFor(client, api);
    }
}
