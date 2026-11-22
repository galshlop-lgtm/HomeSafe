using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using HomeSafe.Shared;

namespace HomeSafe.Server;

public enum DeliveryResult { Accepted, Retry, InvalidDevice }

public sealed class PushSender(HttpClient client, IConfiguration config, ILogger<PushSender> log)
{
    private readonly Lazy<Task<ITokenAccess>> google = new(async () =>
    {
        using var stream = File.OpenRead(config["Push:FcmKeyFile"]!);
        var credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, CancellationToken.None))
            .ToGoogleCredential().CreateScoped("https://www.googleapis.com/auth/firebase.messaging");
        return credential;
    });

    public bool Configured(string? platform) => platform switch
    {
        "android" => Has("FcmProject") && Has("FcmKeyFile"),
        "ios" => Has("ApnsKeyFile") && Has("ApnsKeyId") && Has("ApnsTeamId") && Has("ApnsTopic"),
        _ => Has("FcmProject") && Has("FcmKeyFile") || Has("ApnsKeyFile") && Has("ApnsTopic")
    };

    private bool Has(string name) => !string.IsNullOrWhiteSpace(config["Push:" + name]);

    public async Task<DeliveryResult> Send(DeviceInput device, AlertMessage alert, CancellationToken token)
    {
        if (!Configured(device.Platform)) return DeliveryResult.Retry;
        try
        {
            using var request = device.Platform == "android"
                ? await Fcm(device.Token, alert, token) : Apns(device.Token, alert);
            using var response = await client.SendAsync(request, token);
            if (response.IsSuccessStatusCode) return DeliveryResult.Accepted;
            string body = await response.Content.ReadAsStringAsync(token);
            if ((device.Platform == "android" && body.Contains("UNREGISTERED", StringComparison.Ordinal))
                || (device.Platform == "ios" && (response.StatusCode == HttpStatusCode.Gone
                    || body.Contains("BadDeviceToken", StringComparison.Ordinal))))
                return DeliveryResult.InvalidDevice;
            log.LogWarning("{Platform} push provider returned HTTP {Status}", device.Platform, (int)response.StatusCode);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            log.LogWarning("Push provider failed ({Error})", ex.GetType().Name);
        }
        return DeliveryResult.Retry;
    }

    private async Task<HttpRequestMessage> Fcm(string device, AlertMessage a, CancellationToken token)
    {
        var credential = await google.Value;
        string accessToken = await credential.GetAccessTokenForRequestAsync(cancellationToken: token);
        var project = Uri.EscapeDataString(config["Push:FcmProject"]!);
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{project}/messages:send");
        request.Headers.Authorization = new("Bearer", accessToken);
        int ttl = Math.Max(0, (int)(a.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        request.Content = JsonContent.Create(new
        {
            message = new
            {
                token = device,
                android = new { priority = "HIGH", ttl = $"{ttl}s" },
                data = new Dictionary<string, string>
                {
                    ["id"] = a.Id.ToString(), ["requestId"] = a.RequestId.ToString(), ["kind"] = a.Kind.ToString(),
                    ["title"] = a.Title, ["body"] = a.Body, ["urgent"] = a.Urgent ? "1" : "0",
                    ["expires"] = a.ExpiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            }
        });
        return request;
    }

    private HttpRequestMessage Apns(string device, AlertMessage a)
    {
        string host = config.GetValue<bool>("Push:ApnsSandbox") ? "api.sandbox.push.apple.com" : "api.push.apple.com";
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{Uri.EscapeDataString(device)}")
        {
            Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Authorization = new("Bearer", ApnsToken());
        request.Headers.Add("apns-topic", config["Push:ApnsTopic"]!);
        request.Headers.Add("apns-push-type", "alert");
        request.Headers.Add("apns-priority", "10");
        request.Headers.Add("apns-expiration", a.ExpiresAt.ToUnixTimeSeconds().ToString());
        request.Headers.Add("apns-id", a.Id.ToString());

        bool critical = a.Urgent && config.GetValue<bool>("Push:ApnsCritical");
        var aps = new Dictionary<string, object>
        {
            ["alert"] = new { title = a.Title, body = a.Body },
            ["interruption-level"] = critical ? "critical" : a.Urgent ? "time-sensitive" : "passive",
            ["content-available"] = 1,
            ["thread-id"] = a.RequestId.ToString()
        };
        if (a.Urgent) aps["sound"] = critical ? new { critical = 1, name = "alarm.wav", volume = 1.0 } : "alarm.wav";
        request.Content = JsonContent.Create(new
        {
            aps, requestId = a.RequestId, id = a.Id, kind = a.Kind.ToString(), title = a.Title,
            body = a.Body, urgent = a.Urgent ? "1" : "0"
        });
        return request;
    }

    private static readonly object apnsGate = new();
    private static (string Value, DateTimeOffset Issued)? apnsAuth;

    private string ApnsToken()
    {
        lock (apnsGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (apnsAuth is {} cached && now - cached.Issued < TimeSpan.FromMinutes(45))
                return cached.Value;
            string header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = config["Push:ApnsKeyId"] }));
            string claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = config["Push:ApnsTeamId"], iat = now.ToUnixTimeSeconds() }));
            string signing = header + "." + claims;
            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(config["Push:ApnsKeyFile"]!));
            string jwt = signing + "." + Encode(key.SignData(Encoding.UTF8.GetBytes(signing), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            apnsAuth = (jwt, now);
            return jwt;
        }
    }

    private static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
