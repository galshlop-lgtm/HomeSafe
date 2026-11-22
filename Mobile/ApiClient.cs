using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HomeSafe.Shared;

namespace HomeSafe.Mobile;

internal sealed class ApiClient
{
    private const string SessionKey = "homesafe.session";
    internal static readonly ApiClient Current = new();
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(7) };
    private string? token;

    public string Endpoint => Preferences.Default.Get("homesafe.server", "");
    public bool SignedIn => token is not null;

    public void SetEndpoint(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0
            || uri.Scheme != "https" && !DebugHttp(uri))
            throw new InvalidOperationException("יש להזין כתובת שרת HTTPS תקינה.");
        Preferences.Default.Set("homesafe.server", uri.AbsoluteUri.TrimEnd('/'));
    }

    private static bool DebugHttp(Uri uri)
    {
#if DEBUG
        return uri.Scheme == "http";
#else
        return false;
#endif
    }

    public async Task Restore()
    {
        if (!SignedIn && Endpoint.Length > 0)
            token = await SecureStorage.Default.GetAsync(SessionKey);
    }

    public async Task SignIn(Session session)
    {
        await SecureStorage.Default.SetAsync(SessionKey, session.Token);
        token = session.Token;
    }

    public void Forget()
    {
        SecureStorage.Default.Remove(SessionKey);
        token = null;
    }

    public Task<FamilyView> Family() => Get<FamilyView>("family");

    public Task<T> Get<T>(string route) => Send<T>(HttpMethod.Get, route);

    public async Task<T> Send<T>(HttpMethod method, string route, object? data = null)
    {
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
