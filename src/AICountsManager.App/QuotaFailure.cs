using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace AICountsManager;

// Classify the result of the existing request. No connectivity probes, interface watchers or raw exception text.
internal static class QuotaFailure
{
    public const string Connection = "Error de conexión · No se ha podido actualizar";
    public const string Authentication = "Error de autenticación · Inicia sesión de nuevo";
    public const string Service = "Servicio no disponible · No se ha podido actualizar";
    public const string Timeout = "Tiempo de espera agotado · No se ha podido actualizar";
    public const string Limited = "Límite de consultas · Inténtalo más tarde";
    public const string InvalidResponse = "Respuesta no válida · No se ha podido actualizar";
    public const string Unknown = "No se ha podido actualizar";

    public static string FromStatus(int? status) => status switch
    {
        401 or 403 => Authentication,
        429 => Limited,
        >= 500 and <= 599 => Service,
        408 => Timeout,
        >= 400 and <= 499 => "Consulta rechazada · No se ha podido actualizar",
        _ => InvalidResponse,
    };
    public static string FromException(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => FromStatus((int)status),
        HttpRequestException { HttpRequestError: HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError } => InvalidResponse,
        HttpRequestException or HttpIOException or SocketException => Connection,
        OperationCanceledException or TimeoutException => Timeout,
        JsonException => InvalidResponse,
        IOException { InnerException: { } inner } => FromException(inner),
        _ => Unknown,
    };
}
