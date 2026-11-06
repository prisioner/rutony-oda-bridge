using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RutonyOda;

// Owns single-use PKCE attempts and the cancellation/credential commit boundary.
// The service serializes controls and drains the consumer before changing accounts.
internal sealed partial class OAuthCoordinator : IDisposable
{
    internal sealed record Attempt(string State, string Verifier, string Redirect, DateTimeOffset Expires);
    private readonly SecretVault vault;
    private readonly OdaApi api;
    private readonly OdaEndpoints endpoints;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool stopped;
    private Attempt? pending;
    private volatile Credentials? current;
    public Credentials? Current => current;
    public OAuthCoordinator(string directory, OdaApi api, OdaEndpoints endpoints)
    {
        vault = new SecretVault(directory); current = vault.Load();
        this.api = api; this.endpoints = endpoints;
    }
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,180}$")]
    private static partial Regex ClientIdPattern();
    public static void ValidateConfiguration(string clientId, string clientSecret)
    {
        if (!ClientIdPattern().IsMatch(clientId) || clientSecret.Length is < 1 or > 2048 || clientSecret.Any(char.IsControl))
            throw new ArgumentException("Проверьте Client ID и Secret приложения.");
    }
    public void Configure(string clientId, string clientSecret)
    {
        lock (gate)
        {
            lifetime.Token.ThrowIfCancellationRequested(); pending = null;
            Save(new Credentials(clientId, clientSecret, null, null, DateTimeOffset.MinValue));
        }
    }
    public string Begin(string redirect)
    {
        lock (gate)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (current == null) throw new InvalidOperationException("Сначала сохраните Client ID и Secret.");
            var uri = new Uri(redirect);
            if (uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/oauth/callback" || uri.Query != "" || uri.UserInfo != "") throw new ArgumentException("Некорректный callback.");
            string Random() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var auth = new Attempt(Random(), Random(), redirect, DateTimeOffset.UtcNow.AddMinutes(10)); pending = auth;
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(auth.Verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var values = new Dictionary<string, string> { { "client_id", current.ClientId }, { "response_type", "code" }, { "redirect_uri", redirect }, { "scope", "stomp offline_access" }, { "state", auth.State }, { "code_challenge", challenge }, { "code_challenge_method", "S256" } };
            return endpoints.Authorization + "?" + string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        }
    }
    public Attempt TakeAttempt(string state, string code, string? providerError)
    {
        lock (gate)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var auth = pending;
            if (auth == null || auth.Expires < DateTimeOffset.UtcNow || state.Length != auth.State.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(auth.State))) throw new OdaFault("Попытка входа устарела. Нажмите «Войти» ещё раз.");
            pending = null;
            if (!string.IsNullOrEmpty(providerError)) throw new OdaFault(providerError == "access_denied"
                ? "Вход в ODA отменён. Начните новую попытку, когда будете готовы."
                : "ODA не разрешила вход. Повторите попытку.");
            if (code.Length is < 1 or > 4096) throw new OdaFault("Не удалось получить подтверждение входа от ODA. Повторите попытку.");
            return auth;
        }
    }
    public async Task Complete(Attempt auth, string code)
    {
        var next = await api.Exchange(current!, code, auth.Verifier, auth.Redirect, lifetime.Token);
        lock (gate) { lifetime.Token.ThrowIfCancellationRequested(); Save(next); }
    }
    public async Task<Credentials> Refresh(Credentials credentials, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        var next = await api.Refresh(credentials, cancellation.Token);
        lock (gate) { cancellation.Token.ThrowIfCancellationRequested(); Save(next); }
        return next;
    }
    private void Save(Credentials next) { vault.Save(next); current = next; }
    public void Cancel()
    {
        lock (gate) { if (stopped) return; stopped = true; pending = null; lifetime.Cancel(); }
    }
    public void Dispose() { Cancel(); lifetime.Dispose(); }
}
