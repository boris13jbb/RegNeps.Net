using System.Net.Sockets;
using Microsoft.Maui.Networking;

namespace RegNeps.Mobile.Local;

public enum ServerAvailabilityKind
{
    NoNetwork,
    ServerUnreachable,
    Online
}

public sealed record ServerAvailability(ServerAvailabilityKind Kind, string Message);

/// <summary>
/// Distingue ausencia de red vs red presente con servidor inaccesible.
/// NetworkAccess != None no implica que el servidor responda.
/// </summary>
public static class ServerAvailabilityProbe
{
    public static async Task<ServerAvailability> ProbeAsync(
        string? serverBaseUrl,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var access = Connectivity.Current.NetworkAccess;
        if (access is NetworkAccess.None or NetworkAccess.Unknown)
        {
            return new ServerAvailability(
                ServerAvailabilityKind.NoNetwork,
                "Sin conexión de red en el dispositivo.");
        }

        if (string.IsNullOrWhiteSpace(serverBaseUrl)
            || !Uri.TryCreate(serverBaseUrl, UriKind.Absolute, out var baseUri))
        {
            return new ServerAvailability(
                ServerAvailabilityKind.ServerUnreachable,
                "URL del servidor no configurada o inválida.");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
            using var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(3) };
            // HEAD/GET ligero al origen; 401/403/404 cuentan como «servidor alcanzable».
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
            return new ServerAvailability(ServerAvailabilityKind.Online, "Servidor alcanzable.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            return new ServerAvailability(
                ServerAvailabilityKind.ServerUnreachable,
                "Hay red, pero el servidor RegNeps no responde.");
        }
        catch
        {
            return new ServerAvailability(
                ServerAvailabilityKind.ServerUnreachable,
                "No se pudo verificar el servidor.");
        }
    }
}
