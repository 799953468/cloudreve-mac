using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Cloudreve.Desktop;

internal sealed class DesktopLogin
{
    // Protocol values published by cloudreve/desktop in ui/src/utils/constants.ts.
    internal const string ClientId = "393a1839-f52e-498e-9972-e77cc2241eee";
    private const string PublicClientSecret = "8GaQIu3lOSdqYoDHi9cR8IZ4pvuMH8ya";
    private const string Redirect = "/callback/desktop";
    private const string Scope = "profile email openid offline_access UserInfo.Write Workflow.Write Files.Write Shares.Write";
    private readonly HttpClient http;
    private readonly Uri site;
    private readonly string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
    private readonly string state = Base64Url(RandomNumberGenerator.GetBytes(32));
    private readonly DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(10);
    private int consumed;

    internal DesktopLogin(HttpClient http, string address)
    {
        this.http = http;
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
        {
            throw new ArgumentException(UiText.Get("login.invalidSiteUrl"));
        }

        site = new Uri(uri.GetLeftPart(UriPartial.Authority));
    }

    internal async Task<Uri> BeginAsync(CancellationToken cancel)
    {
        using (HttpResponseMessage response = await http.GetAsync(new Uri(site, "/api/v4/site/ping"), cancel))
        {
            response.EnsureSuccessStatusCode();
            JsonElement data = Envelope(await response.Content.ReadFromJsonAsync(DriveApiJson.Default.JsonElement, cancel));
            string version = data.GetString() ?? "";
            if (version.EndsWith("-pro", StringComparison.Ordinal))
            {
                version = version[..^4];
            }

            if (!Version.TryParse(version, out Version? parsed) || parsed < new Version(4, 12, 0))
            {
                throw new InvalidDataException(UiText.Get("login.serverTooOld"));
            }
        }

        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new Uri(site,
            $"/session/authorize?response_type=code&client_id={ClientId}&scope={Uri.EscapeDataString(Scope)}&redirect_uri=%2Fcallback%2Fdesktop&code_challenge={challenge}&code_challenge_method=S256&state={state}");
    }

    internal bool MatchesCallback(Uri callback)
    {
        return callback.IsAbsoluteUri && callback.Scheme == "cloudreve" && callback.Host == "mount" &&
               callback.AbsolutePath.Trim('/').Length == 0 && callback.UserInfo.Length == 0 && callback.IsDefaultPort &&
               callback.Fragment.Length == 0 && HttpUtility.ParseQueryString(callback.Query)["state"] == state;
    }

    internal async Task<DriveSession> CompleteAsync(Uri callback, CancellationToken cancel)
    {
        var values = HttpUtility.ParseQueryString(callback.Query);
        if (!MatchesCallback(callback) || DateTimeOffset.UtcNow >= deadline)
        {
            throw new InvalidDataException(UiText.Get("login.callbackMismatchOrExpired"));
        }

        if (Interlocked.Exchange(ref consumed, 1) != 0)
        {
            throw new InvalidOperationException(UiText.Get("login.callbackAlreadyUsed"));
        }

        if (values["error"] != null || string.IsNullOrWhiteSpace(values["code"]))
        {
            throw new InvalidDataException(UiText.Get("login.notApproved"));
        }

        string root = values["path"] ?? "";
        if (!Uri.TryCreate(root, UriKind.Absolute, out var driveRoot) || driveRoot.Scheme != "cloudreve" || string.IsNullOrEmpty(driveRoot.Host))
        {
            throw new InvalidDataException(UiText.Get("login.callbackMissingFolder"));
        }

        using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["client_secret"] = PublicClientSecret,
                   ["code"] = values["code"]!, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier
               }))
        using (var response = await http.PostAsync(new Uri(site, "/api/v4/session/oauth/token"), form, cancel))
        {
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync(DriveApiJson.Default.JsonElement, cancel);
            var now = DateTimeOffset.UtcNow;
            return new DriveSession(site.AbsoluteUri, Guid.NewGuid().ToString(), values["name"] ?? site.Host, root, values["user_id"] ?? "",
                Required(json, "access_token"), Required(json, "refresh_token"), now.AddSeconds(json.GetProperty("expires_in").GetInt64()),
                now.AddSeconds(json.GetProperty("refresh_token_expires_in").GetInt64()));
        }
    }

    internal static JsonElement Envelope(JsonElement json)
    {
        int code = json.GetProperty("code").GetInt32();
        if (code != 0)
        {
            throw new IOException(UiText.Get("api.requestFailed", ("code", code), ("message", json.TryGetProperty("msg", out var msg) ? msg.GetString() : "")))
            {
                Data = { ["CloudreveCode"] = code }
            };
        }

        if (json.TryGetProperty("data", out var data))
        {
            return data;
        }

        return default;
    }

    internal static DriveSession VerifyBinding(DriveSession candidate, string serverUser, DriveSession? previous)
    {
        if (string.IsNullOrEmpty(serverUser) || (candidate.UserId.Length > 0 && candidate.UserId != serverUser))
        {
            throw new InvalidDataException(UiText.Get("login.callbackAccountMismatch"));
        }

        if (previous != null && (previous.UserId != serverUser || new Uri(previous.Site) != new Uri(candidate.Site) ||
                                 new Uri(previous.Root) != new Uri(candidate.Root)))
        {
            throw new InvalidDataException(UiText.Get("login.reauthorizeMustMatch"));
        }

        return candidate with { UserId = serverUser, DriveId = previous?.DriveId ?? candidate.DriveId };
    }

    internal static string Required(JsonElement json, string key)
    {
        string? value = json.GetProperty(key).GetString();
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidDataException(UiText.Get("api.responseMissingField", ("field", key)));
        }

        return value;
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

internal sealed record DriveSession(
    string Site,
    string DriveId,
    string Name,
    string Root,
    string UserId,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessExpires,
    DateTimeOffset RefreshExpires)
{
    public override string ToString()
    {
        return $"{Name} ({Site})";
    }
}
