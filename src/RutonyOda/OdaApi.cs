using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RutonyOda;

public sealed class OdaFault(string message, bool retryable = false) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}
public sealed record OdaEndpoints
{
    public Uri Authorization { get; private init; } = new("https://auth.oda.digital/realms/ODA/protocol/openid-connect/auth");
    public Uri Token { get; private init; } = new("https://auth.oda.digital/realms/ODA/protocol/openid-connect/token");
    public Uri Api { get; private init; } = new("https://api.oda.digital/");
    public Uri Socket { get; private init; } = new("wss://api.oda.digital/ws");
    private OdaEndpoints() { }
    public static OdaEndpoints Production { get; } = new();
    public static OdaEndpoints ForTests(string root)
    {
        var uri = new Uri(root);
        if (uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.UserInfo != "" || uri.Query != "" || uri.AbsolutePath != "/") throw new ArgumentException("Тестовый сервер должен быть IPv4 loopback.");
        return new OdaEndpoints { Authorization = new Uri(uri, "authorize"), Token = new Uri(uri, "token"), Api = uri, Socket = new UriBuilder(uri) { Scheme = "ws", Path = "/ws" }.Uri };
    }
}
public sealed class OdaApi : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly OdaEndpoints endpoints;
    public OdaApi(OdaEndpoints endpoints) { this.endpoints = endpoints; }
    public async Task<Credentials> Exchange(Credentials credentials, string code, string verifier, string redirect, CancellationToken ct = default)
        => await Token(credentials, new() { { "grant_type", "authorization_code" }, { "code", code }, { "code_verifier", verifier }, { "redirect_uri", redirect } }, ct);
    public async Task<Credentials> Refresh(Credentials credentials, CancellationToken ct)
        => await Token(credentials, new() { { "grant_type", "refresh_token" }, { "refresh_token", credentials.RefreshToken ?? throw new OdaFault("Требуется повторный вход ODA.") } }, ct);
    private async Task<Credentials> Token(Credentials credentials, Dictionary<string, string> form, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20)); ct = deadline.Token;
        form["client_id"] = credentials.ClientId; form["client_secret"] = credentials.ClientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.Token) { Content = new FormUrlEncodedContent(form) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        string stage = "Авторизация ODA";
        if (!response.IsSuccessStatusCode)
        {
            using var diagnosticTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            diagnosticTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var body = await ReadJson(response, diagnosticTimeout.Token);
                if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("error", out var item) && item.ValueKind == JsonValueKind.String && SafeOAuthError(item.GetString()) is { } code)
                    stage += " (" + code + ")";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* The HTTP status remains useful without a complete error body. */ }
            catch (Exception e) when (e is JsonException or OdaFault or IOException or HttpRequestException) { /* Preserve HTTP status, never echo response bodies. */ }
        }
        Ensure(response, stage);
        using var doc = await ReadJson(response, ct); var p = doc.RootElement;
        var access = p.GetProperty("access_token").GetString();
        var refresh = p.TryGetProperty("refresh_token", out var r) ? r.GetString() : credentials.RefreshToken;
        var expires = p.GetProperty("expires_in").GetInt32();
        if (string.IsNullOrWhiteSpace(access) || access.Length > 32000 || string.IsNullOrWhiteSpace(refresh) || refresh.Length > 32000 || expires < 90 || expires > 864000)
            throw new OdaFault("Некорректный ответ авторизации ODA.");
        return credentials with { AccessToken = access, RefreshToken = refresh, Expires = DateTimeOffset.UtcNow.AddSeconds(expires) };
    }
    public async Task EnsureSubscription(Credentials credentials, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20)); ct = deadline.Token;
        using var get = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoints.Api, "subscriptions")); get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        using var response = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct); Ensure(response, "Подписка ODA");
        using var doc = await ReadJson(response, ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new OdaFault("Неизвестный формат подписок ODA.");
        if (doc.RootElement.EnumerateArray().Any(p => p.GetProperty("subscriberId").GetString() == credentials.ClientId && p.GetProperty("events").EnumerateArray().Any(v => v.GetString() == "HistoryItemEvent"))) return;
        using var post = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoints.Api, "subscriptions/commands/add-queue-subscription")) { Content = new StringContent("{\"events\":[\"HistoryItemEvent\"]}", System.Text.Encoding.UTF8, "application/json") }; post.Headers.Authorization = get.Headers.Authorization;
        using var added = await http.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, ct); Ensure(added, "Создание подписки ODA");
    }
    internal static string? SafeOAuthError(string? code) => code switch
    {
        "invalid_request" or "invalid_client" or "invalid_grant" or "unauthorized_client" or
        "unsupported_grant_type" or "invalid_scope" or "access_denied" or "server_error" or
        "temporarily_unavailable" or "login_required" or "consent_required" or "interaction_required" => code,
        _ => null
    };
    private static void Ensure(HttpResponseMessage response, string stage)
    {
        if (response.IsSuccessStatusCode) return;
        bool retry = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests;
        throw new OdaFault(stage + ": HTTP " + (int)response.StatusCode + ". " + (retry ? "Повторяем подключение." : "Проверьте приложение и повторите вход."), retry);
    }
    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > 2097152) throw new OdaFault("Слишком большой ответ ODA.");
        await using var source = await response.Content.ReadAsStreamAsync(ct); using var target = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0) { if (target.Length + count > 2097152) throw new OdaFault("Слишком большой ответ ODA."); target.Write(buffer, 0, count); }
        return JsonDocument.Parse(target.ToArray(), new JsonDocumentOptions { MaxDepth = 20 });
    }
    public void Dispose() => http.Dispose();
}
