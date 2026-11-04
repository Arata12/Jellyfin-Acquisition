using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Acquisition.Messages;

/// <summary>
/// Turns technical failures (HTTP codes, exceptions) into plain Spanish text for the people using the plugin.
/// The raw detail belongs in the logs; these messages say what happened and what to do next.
/// </summary>
public static class FriendlyErrors
{
    /// <summary>A failed HTTP call to a configured service (Sonarr, Radarr, Prowlarr, qBittorrent).</summary>
    public static string FromStatus(HttpStatusCode status, string service)
    {
        var code = (int)status;
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"{service} rechazó la clave de API o el acceso (HTTP {code}). Revísalo en la configuración del plugin.",
            HttpStatusCode.NotFound =>
                $"{service} no encontró lo pedido (HTTP 404). Revisa que la URL configurada sea la correcta.",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                $"{service} tardó demasiado en responder. Se reintentará más tarde.",
            (HttpStatusCode)429 =>
                $"{service} pide esperar antes de aceptar más consultas (HTTP 429). Se reintentará más tarde.",
            _ when code >= 500 =>
                $"{service} falló internamente (HTTP {code}). Se reintentará más tarde.",
            _ => $"{service} respondió con un error inesperado (HTTP {code})."
        };
    }

    /// <summary>An exception raised while talking to a configured service.</summary>
    public static string FromException(Exception ex, string service)
    {
        if (ex is HttpRequestException { StatusCode: { } status })
        {
            return FromStatus(status, service);
        }

        if (ex is TaskCanceledException or TimeoutException || ex.InnerException is TimeoutException)
        {
            return $"{service} tardó demasiado en responder. Se reintentará más tarde.";
        }

        var socket = ex as SocketException ?? ex.InnerException as SocketException;
        if (socket != null)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused =>
                    $"No se pudo conectar con {service}: conexión rechazada. Comprueba que esté en marcha y que la URL y el puerto sean correctos.",
                SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData =>
                    $"No se encuentra el servidor de {service}. Revisa la URL configurada.",
                _ => $"No se pudo conectar con {service}. Comprueba que esté en marcha y accesible desde Jellyfin."
            };
        }

        if (ex is HttpRequestException)
        {
            return $"No se pudo conectar con {service}. Comprueba que esté en marcha y accesible desde Jellyfin.";
        }

        return $"No se pudo comunicar con {service}. Se reintentará más tarde.";
    }

    /// <summary>Failure downloading a torrent file through the indexer.</summary>
    public static string TorrentDownload(HttpStatusCode status, double? retryAfterSeconds = null)
    {
        var code = (int)status;
        return status switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone =>
                "Ese torrent ya no existe en el indexador; se buscará otro.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"El indexador denegó la descarga del torrent (HTTP {code}). Revisa tu acceso o cookie en Prowlarr.",
            (HttpStatusCode)429 =>
                retryAfterSeconds.HasValue
                    ? $"El indexador pide esperar {retryAfterSeconds.Value:0} s antes de descargar más torrents (HTTP 429). Se reintentará en unos minutos."
                    : "El indexador pide esperar antes de descargar más torrents (HTTP 429). Se reintentará en unos minutos.",
            _ when code >= 500 =>
                $"El indexador falló al entregar el torrent (HTTP {code}). Se reintentará en unos minutos.",
            _ => $"No se pudo descargar el torrent del indexador (HTTP {code})."
        };
    }

    /// <summary>Whether the HTTP status means the indexer will never serve this torrent again.</summary>
    public static bool IsGone(HttpStatusCode status) => status is HttpStatusCode.NotFound or HttpStatusCode.Gone;
}
