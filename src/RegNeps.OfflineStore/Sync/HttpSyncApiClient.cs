using System.Net;
using System.Text;
using System.Text.Json;
using RegNeps.OfflineStore.Sync.Contracts;

namespace RegNeps.OfflineStore.Sync;

/// <summary>Cliente HTTP real contra /api/sync/push y /pull.</summary>
public sealed class HttpSyncApiClient : ISyncApiClient
{
    private readonly HttpClient _http;

    public HttpSyncApiClient(HttpClient http) =>
        _http = http ?? throw new ArgumentNullException(nameof(http));

    public Task<ClientSyncPushResponse> PushAsync(
        string serverBaseUrl,
        string? cookieHeader,
        ClientSyncPushRequest request,
        CancellationToken ct = default) =>
        SendAsync<ClientSyncPushRequest, ClientSyncPushResponse>(
            serverBaseUrl, "/api/sync/push", cookieHeader, request, ct);

    public Task<ClientSyncPullResponse> PullAsync(
        string serverBaseUrl,
        string? cookieHeader,
        ClientSyncPullRequest request,
        CancellationToken ct = default) =>
        SendAsync<ClientSyncPullRequest, ClientSyncPullResponse>(
            serverBaseUrl, "/api/sync/pull", cookieHeader, request, ct);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string serverBaseUrl,
        string path,
        string? cookieHeader,
        TRequest body,
        CancellationToken ct)
    {
        var baseUrl = serverBaseUrl.TrimEnd('/');
        using var msg = new HttpRequestMessage(HttpMethod.Post, baseUrl + path);
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            msg.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        var json = JsonSerializer.Serialize(body, ClientSyncJson.Options);
        msg.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(msg, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SyncTransportException(SyncTransportFailureKind.Timeout, "Timeout al contactar el servidor.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncTransportException(SyncTransportFailureKind.Network, "Error de red al contactar el servidor.", inner: ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new SyncTransportException(
                    SyncTransportFailureKind.Unauthorized,
                    "Sesión de servidor no válida (401).",
                    (int)response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new SyncTransportException(
                    SyncTransportFailureKind.Forbidden,
                    "Acceso denegado (403).",
                    (int)response.StatusCode);
            }

            if ((int)response.StatusCode >= 500)
            {
                throw new SyncTransportException(
                    SyncTransportFailureKind.ServerError,
                    $"Error del servidor ({(int)response.StatusCode}).",
                    (int)response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SyncTransportException(
                    SyncTransportFailureKind.InvalidResponse,
                    $"Respuesta HTTP inesperada ({(int)response.StatusCode}).",
                    (int)response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            try
            {
                var parsed = await JsonSerializer.DeserializeAsync<TResponse>(stream, ClientSyncJson.Options, ct);
                if (parsed is null)
                {
                    throw new SyncTransportException(
                        SyncTransportFailureKind.InvalidResponse,
                        "JSON de sync vacío o inválido.");
                }

                return parsed;
            }
            catch (JsonException ex)
            {
                throw new SyncTransportException(
                    SyncTransportFailureKind.InvalidResponse,
                    "JSON de sync incompatible.",
                    inner: ex);
            }
        }
    }
}
