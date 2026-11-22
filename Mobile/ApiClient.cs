using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HomeSafe.Shared;
using Microsoft.Maui.Storage;

namespace HomeSafe.Mobile;

internal sealed class ApiClient
{
    private const string SessionKey = "homesafe.https.session";
    private const string EndpointKey = "homesafe.https.endpoint";
    private const string ConnectionError = "לא ניתן להתחבר לשרת. בדוק את הכתובת ונסה שוב.";
    internal static readonly ApiClient Current = new();
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(7) };
    private string? token;

    public string Endpoint { get; private set; } = "";
    public bool SignedIn => token is not null;
    private string? verified;

    private static bool Trusted(string endpoint)
    {
        var configured = typeof(ApiClient).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(x => x.Key == "HomeSafeTrustedServers")?.Value ?? "";
        return ServerAddress.IsTrusted(endpoint, configured);
    }

    public async Task ValidateEndpoint(string address)
    {
        verified = null;
        string endpoint = ServerAddress.Normalize(address)
            ?? throw new InvalidOperationException("יש להזין כתובת שרת HTTPS תקינה.");
        if (!Trusted(endpoint))
            throw new InvalidOperationException("כתובת השרת אינה מאושרת בגרסה זו של HomeSafe.");
        try
        {
            using var response = await client.GetAsync(endpoint + "/health");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ConnectionError);
            var health = await response.Content.ReadFromJsonAsync<HealthStatus>();
            if (health is not { Status: "ok", Service: "HomeSafe", Protocol: 2 })
                throw new InvalidOperationException(ConnectionError);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            throw new InvalidOperationException(ConnectionError, ex);
        }

        if (Endpoint != endpoint) Forget();
        await SecureStorage.Default.SetAsync(EndpointKey, endpoint);
        Endpoint = verified = endpoint;
    }

    public async Task Restore()
    {
        if (SignedIn) return;
        var saved = await SecureStorage.Default.GetAsync(EndpointKey);
        if (saved is null || !Trusted(saved)) return;
        Endpoint = saved;
        var value = await SecureStorage.Default.GetAsync(SessionKey);
        if (value is not null && JsonSerializer.Deserialize<SavedSession>(value) is {} session && session.Endpoint == saved)
            token = session.Token;
    }

    public async Task SignIn(Session session)
    {
        await SecureStorage.Default.SetAsync(SessionKey, JsonSerializer.Serialize(new SavedSession(Endpoint, session.Token)));
        token = session.Token;
    }

    public void Forget()
    {
        SecureStorage.Default.Remove(SessionKey);
        token = null;
    }

    private sealed record SavedSession(string Endpoint, string Token);

    public Task<FamilyView> Family() => Get<FamilyView>("family");

    public Task<T> Get<T>(string route) => Send<T>(HttpMethod.Get, route);

    public async Task<T> Send<T>(HttpMethod method, string route, object? data = null)
    {
        if (Endpoint.Length == 0 || (!SignedIn && verified != Endpoint))
            throw new InvalidOperationException("יש לבדוק תחילה את החיבור לשרת HomeSafe.");
        using var request = new HttpRequestMessage(method, Endpoint + "/api/" + route);
        if (token is {} secret) request.Headers.Authorization = new("Bearer", secret);
        if (data is not null) request.Content = JsonContent.Create(data);
        try
        {
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string? detail = null;
                if (response.Content.Headers.ContentType?.MediaType == "application/json")
                    detail = (await response.Content.ReadFromJsonAsync<ApiFailure>().ConfigureAwait(false))?.Error;
                throw new RequestFailure(response.StatusCode, detail ?? "לא ניתן להשלים את הפעולה.");
            }
            if (response.StatusCode == HttpStatusCode.NoContent) return default!;
            return (await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false))!;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException("לא ניתן להתחבר לשרת. יש לבדוק את החיבור ולנסות שוב.", ex);
        }
    }

    private sealed record ApiFailure(string Error);
}

internal sealed class RequestFailure(HttpStatusCode code, string message) : Exception(message)
{
    public HttpStatusCode Code { get; } = code;
}
