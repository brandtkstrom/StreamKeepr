using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace StreamRecorder;

public static class HttpExtensions
{
    private const int _maxErrorBodyLength = 2_048;

    public static async Task<ApiResult<TData>> GetAsync<TData>(this HttpClient httpClient,
                                                               string requestUri,
                                                               JsonTypeInfo<TData> typeInfo,
                                                               CancellationToken cancelToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        using HttpResponseMessage response = await httpClient.SendAsync(request,
                                                                        HttpCompletionOption.ResponseHeadersRead,
                                                                        cancelToken);
        return await MapResult(response, typeInfo, cancelToken);
    }

    public static async Task<ApiResult<TData>> PostAsync<TData>(this HttpClient httpClient,
                                                                string requestUri,
                                                                JsonTypeInfo<TData> typeInfo,
                                                                HttpContent? content = null,
                                                                CancellationToken cancelToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsync(requestUri,
                                                                        content,
                                                                        cancelToken);
        return await MapResult(response, typeInfo, cancelToken);
    }

    private static async Task<ApiResult<TData>> MapResult<TData>(HttpResponseMessage response,
                                                                 JsonTypeInfo<TData> typeInfo,
                                                                 CancellationToken cancelToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancelToken);
            if (body.Length > _maxErrorBodyLength)
            {
                body = body[.._maxErrorBodyLength];
            }

            return ApiResult<TData>.Failure(response.StatusCode, body);
        }

        TData? data = await response.Content.ReadFromJsonAsync(typeInfo, cancelToken);

        return data is null
                   ? ApiResult<TData>.Failure(response.StatusCode, "Response body was empty or JSON 'null'.")
                   : ApiResult<TData>.Success(response.StatusCode, data);
    }
}

public sealed record ApiResult<TData>
{
    public TData? Data { get; private init; }
    public HttpStatusCode StatusCode { get; private init; }
    public string? Error { get; private init; }

    [MemberNotNullWhen(true, nameof(Data))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; private init; }

    public static ApiResult<TData> Success(HttpStatusCode status, TData data) =>
        new()
        {
            IsSuccess = true,
            StatusCode = status,
            Data = data
        };

    public static ApiResult<TData> Failure(HttpStatusCode status, string error) =>
        new()
        {
            IsSuccess = false,
            StatusCode = status,
            Error = error
        };
}