using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RutonyOda;

internal static class Integration
{
    public static async Task Run()
    {
        await OAuthCallbackErrors();
        await OfflineBacklog();
        var dir = Path.Combine(Path.GetTempPath(), "oda-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await using var fake = await FakeOda.Start();
        var seen = new List<Donation>();
        try
        {
            using var bridge = new BridgeService(dir, d => { lock (seen) { seen.Add(d); if (d.Key.EndsWith("/ambiguous", StringComparison.Ordinal)) throw new Exception("private native failure"); } }, fake.Endpoints);
            await bridge.Configure("test-client", "test-secret");
            using var server = BridgeServer.Start(Path.Combine(Directory.GetCurrentDirectory(), "src", "RutonyOda"), bridge, 0);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri("http://127.0.0.1:" + server.Port) };
            var html = await http.GetStringAsync("/control");
            var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"bridge-token\" content=\"([A-F0-9]+)\"").Groups[1].Value;
            Check(token.Length == 64, "HTML control token");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/state")) { request.Headers.Host = "evil.invalid"; using var response = await http.SendAsync(request); Check(response.StatusCode == HttpStatusCode.Forbidden, "Host guard"); }
            using (var response = await http.PostAsync("/api/enabled", new StringContent("{\"enabled\":false}", Encoding.UTF8, "application/json"))) Check(response.StatusCode == HttpStatusCode.Forbidden, "missing CSRF token");
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/enabled") { Content = new StringContent("{}", Encoding.UTF8, "application/json") }) { request.Headers.Add("X-Bridge-Token", token); request.Headers.Add("Origin", "https://evil.invalid"); using var response = await http.SendAsync(request); Check(response.StatusCode == HttpStatusCode.Forbidden, "Origin guard"); }
            http.DefaultRequestHeaders.Add("X-Bridge-Token", token);
            using var authorize = await http.PostAsync("/api/authorize", new StringContent("{}", Encoding.UTF8, "application/json"));
            using var authorizeBody = JsonDocument.Parse(await authorize.Content.ReadAsStringAsync());
            var url = authorizeBody.RootElement.GetProperty("url").GetString()!;
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            fake.ExpectedChallenge = query["code_challenge"]!;
            await ExpectThrows(() => bridge.FinishAuthorization("bad", "code"));
            using (var callback = await http.GetAsync("/oauth/callback?state=" + query["state"] + "&code=code")) Check(callback.StatusCode == HttpStatusCode.Redirect && callback.Headers.Location?.OriginalString == "/control?login=ok", "OAuth callback");
            Check((await http.GetAsync("/credentials.bin")).StatusCode == HttpStatusCode.NotFound, "no file traversal / credentials endpoint");
            await ExpectThrows(() => bridge.FinishAuthorization(query["state"]!, "code"));
            fake.Pending["1"] = Event("one"); fake.Pending["2"] = Event("one"); fake.Pending["3"] = Event("ambiguous"); fake.Pending["4"] = Event("after");
            await bridge.SetEnabled(true);
            await Until(() => bridge.Status().Uncertain.Length == 1 && !bridge.Status().Enabled);
            Check(seen.Count == 2 && fake.Acks == 2 && fake.Subscriptions == 1, "dedup / ACK / uncertain");
            Check(!JsonSerializer.Serialize(bridge.Status()).Contains("test-secret"), "secret in status");
            await bridge.Resolve("recipient/ambiguous", true);
            await bridge.SetEnabled(true);
            await Until(() => fake.Acks == 4);
            await bridge.SetEnabled(false);
            Check(seen.Count == 3 && seen.Last().Key == "recipient/after" && fake.Subscriptions == 1, "resolution / subscription reuse");
            Check(fake.ValidTokenExchange && fake.ValidSubscribe, "PKCE / provider default vhost / client individual ACK");
        }
        finally { Directory.Delete(dir, true); }
        await RefreshAndReconnect();
        await PauseBarrier();
        await MalformedMessage();
        await AuthFailure(false);
        await AuthFailure(true);
        await StorageFailure(false);
        await StorageFailure(true);
    }
    private static async Task OAuthCallbackErrors()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oda-callback-" + Guid.NewGuid().ToString("N"));
        await using var fake = await FakeOda.Start();
        try
        {
            using var bridge = new BridgeService(dir, _ => throw new Exception("OAuth must not publish"), fake.Endpoints);
            await bridge.Configure("test-client", "test-secret");
            using var server = BridgeServer.Start(Path.Combine(Directory.GetCurrentDirectory(), "src", "RutonyOda"), bridge, 0);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri("http://127.0.0.1:" + server.Port) };
            string state = "";
            async Task Begin()
            {
                var url = bridge.BeginAuthorization(http.BaseAddress + "oauth/callback");
                var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
                state = query["state"]!; fake.ExpectedChallenge = query["code_challenge"]!;
                await Task.CompletedTask;
            }
            async Task Callback(string query, bool success = false)
            {
                using var response = await http.GetAsync("/oauth/callback?" + query);
                Check(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.OriginalString == (success ? "/control?login=ok" : "/control?login=failed"), "callback redirect");
            }
            async Task Failure()
            {
                var json = await http.GetStringAsync("/api/state");
                using var doc = JsonDocument.Parse(json); var status = doc.RootElement;
                var error = status.GetProperty("error").GetString();
                Check(!status.GetProperty("authorized").GetBoolean() && !status.GetProperty("enabled").GetBoolean(), "failed callback must not authorize or enable");
                Check(!string.IsNullOrWhiteSpace(error), "Callback must retain an actionable error");
                foreach (var debug in new[] { "HTTP ", "invalid_grant", "access_denied", "state", "Обмен кода на токены", "Проверка ответа входа" })
                    Check(!error!.Contains(debug, StringComparison.OrdinalIgnoreCase), "Temporary OAuth diagnostics visible in normal status");
                foreach (var secret in new[] { "test-secret", "synthetic-access", "synthetic-refresh", "sensitive-provider-error", "sensitive-callback-code", state })
                    Check(!json.Contains(secret, StringComparison.Ordinal), "callback status disclosed sensitive data");
            }
            fake.TokenStatus = 400;
            await Begin();
            await Callback("state=" + state + "&code=sensitive-callback-code");
            await Failure(); await Failure();

            await Begin(); int requests = fake.TokenRequests;
            await Callback("state=" + state + "&error=access_denied&error_description=sensitive-provider-error");
            await Failure();
            Check(fake.TokenRequests == requests, "provider denial must not exchange code");

            await Begin();
            await Callback("state=" + state + "&error=sensitive-provider-error");
            await Failure();
            Check(fake.TokenRequests == requests, "unknown provider denial must not exchange code");

            fake.TokenStatus = 502; fake.TokenErrorBody = "<html>sensitive-provider-error test-secret</html>";
            await Begin();
            await Callback("state=" + state + "&code=sensitive-callback-code");
            await Failure();

            fake.TokenStatus = 503; fake.TokenErrorDelay = 6000;
            await Begin();
            var wait = System.Diagnostics.Stopwatch.StartNew();
            await Callback("state=" + state + "&code=sensitive-callback-code");
            await Failure();
            Check(wait.Elapsed < TimeSpan.FromSeconds(5), "OAuth diagnostics must not wait indefinitely for an error response body");

            fake.TokenStatus = 200; fake.TokenErrorBody = null; fake.TokenErrorDelay = 0;
            await Begin();
            await Callback("state=wrong&code=sensitive-callback-code");
            await Failure();
            await Callback("state=" + state + "&code=sensitive-callback-code", true);
            Check(bridge.Status().Authorized && bridge.Status().Error == null && !bridge.Status().Enabled, "successful retry clears callback error and keeps reception off");

            var stored = File.ReadAllBytes(Path.Combine(dir, "credentials.bin"));
            requests = fake.TokenRequests;
            await Callback("state=wrong&code=sensitive-callback-code");
            Check(bridge.Status().Authorized && fake.TokenRequests == requests && stored.SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "credentials.bin"))), "invalid callback must preserve an existing login");
        }
        finally { Directory.Delete(dir, true); }
    }
    private static async Task OfflineBacklog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oda-offline-" + Guid.NewGuid().ToString("N"));
        await using var fake = await FakeOda.Start();
        try
        {
            new SecretVault(dir).Save(new Credentials("test-client", "test-secret", "synthetic-refresh", "synthetic-access", DateTimeOffset.UtcNow.AddMinutes(5)));
            int count = 0;
            using var bridge = new BridgeService(dir, _ => Interlocked.Increment(ref count), fake.Endpoints);
            fake.Pending["1"] = Event("before-pause");
            await bridge.SetEnabled(true); await Until(() => fake.Acks == 1); await bridge.SetEnabled(false); await Until(() => fake.ActiveConsumers == 0);
            Check(fake.Publish("2", Event("during-pause")), "durable queue disappeared during pause");
            await bridge.SetEnabled(true); await Until(() => fake.Acks == 2); await bridge.SetEnabled(false);
            Check(count == 2 && fake.QueueNames.Count == 1, "offline backlog and stable queue");
        }
        finally { Directory.Delete(dir, true); }
    }
    private static async Task AuthFailure(bool subscription)
    {
        var dir = Path.Combine(Path.GetTempPath(), "oda-auth-fail-" + Guid.NewGuid().ToString("N"));
        await using var fake = await FakeOda.Start();
        try
        {
            fake.TokenStatus = subscription ? 200 : 400; fake.SubscriptionStatus = subscription ? 401 : 200;
            new SecretVault(dir).Save(new Credentials("test-client", "test-secret", "synthetic-refresh", "synthetic-access", DateTimeOffset.UtcNow.AddMinutes(subscription ? 5 : -1)));
            int count = 0; using var bridge = new BridgeService(dir, _ => Interlocked.Increment(ref count), fake.Endpoints);
            fake.Pending["1"] = Event("not-delivered"); await bridge.SetEnabled(true); await Until(() => !bridge.Status().Enabled && bridge.Status().Error != null);
            await Task.Delay(1100);
            Check(count == 0 && fake.Acks == 0 && fake.Connections == 0 && (subscription ? fake.SubscriptionRequests : fake.TokenRequests) == 1, "auth failure must stop without retry/native/ACK");
            var status = JsonSerializer.Serialize(bridge.Status());
            Check(!string.IsNullOrWhiteSpace(bridge.Status().Error) && (subscription ? status.Contains("401") : !status.Contains("HTTP")) && !status.Contains("sensitive-provider-error"),
                "OAuth refresh must retain a normal error without temporary diagnostic codes");
        }
        finally { Directory.Delete(dir, true); }
    }
    private static async Task StorageFailure(bool afterNative)
    {
        var dir = Path.Combine(Path.GetTempPath(), "oda-io-fail-" + Guid.NewGuid().ToString("N"));
        await using var fake = await FakeOda.Start();
        try
        {
            new SecretVault(dir).Save(new Credentials("test-client", "test-secret", "synthetic-refresh", "synthetic-access", DateTimeOffset.UtcNow.AddMinutes(5)));
            int count = 0; bool fail = !afterNative;
            using (var bridge = new BridgeService(dir, _ => { Interlocked.Increment(ref count); fail = true; }, fake.Endpoints, false, (file, bytes) =>
            {
                if (fail) { fail = false; if (!afterNative) file.Write(bytes); throw new IOException("synthetic write/flush failure"); }
                file.Write(bytes); file.Flush(true);
            }))
            {
                fake.Pending["1"] = Event("io"); await bridge.SetEnabled(true); await Until(() => !bridge.Status().Enabled && bridge.Status().StorageFault);
                await ExpectThrows(() => bridge.SetEnabled(true)); await ExpectThrows(() => bridge.Resolve("recipient/io", true));
                Check(count == (afterNative ? 1 : 0) && fake.Acks == 0, "I/O window must stop without ACK or replay");
            }
            using (var reopened = new BridgeService(dir, _ => Interlocked.Increment(ref count), fake.Endpoints))
            {
                Check(reopened.Status().Uncertain.Single() == "recipient/io" && !reopened.Status().Enabled, "restart must expose uncertain prepared");
                await ExpectThrows(() => reopened.SetEnabled(true));
                await reopened.Resolve("recipient/io", afterNative); await reopened.SetEnabled(true); await Until(() => fake.Acks == 1); await reopened.SetEnabled(false);
                Check(count == 1, "operator resolution must not duplicate native call");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
    private static async Task RefreshAndReconnect()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oda-refresh-" + Guid.NewGuid().ToString("N"));
        await using var fake = await FakeOda.Start();
        try
        {
            new SecretVault(dir).Save(new Credentials("test-client", "test-secret", "synthetic-refresh", "expired", DateTimeOffset.UtcNow.AddMinutes(-1)));
            int count = 0;
            using var bridge = new BridgeService(dir, _ => Interlocked.Increment(ref count), fake.Endpoints);
            fake.Pending["1"] = Event("refreshed"); fake.CloseOnce = true;
            await bridge.SetEnabled(true); await Until(() => fake.Acks == 1); await bridge.SetEnabled(false);
            Check(count == 1 && fake.Refreshes == 1 && fake.Connections >= 2 && fake.Subscriptions == 1, "refresh and reconnect");
        }
        finally { Directory.Delete(dir, true); }
    }
    private static async Task PauseBarrier()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oda-pause-" + Guid.NewGuid().ToString("N"));
        await using var fake = await FakeOda.Start(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            new SecretVault(dir).Save(new Credentials("test-client", "test-secret", "synthetic-refresh", "synthetic-access", DateTimeOffset.UtcNow.AddMinutes(5)));
            using var bridge = new BridgeService(dir, _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }, fake.Endpoints);
            fake.Pending["1"] = Event("slow"); await bridge.SetEnabled(true); await Until(() => entered.IsSet);
            var stop = bridge.SetEnabled(false); await Task.Delay(80); Check(!stop.IsCompleted, "pause must wait for native delivery"); release.Set(); await stop;
            Check(bridge.Status().Delivered == 1 && !bridge.Status().Enabled, "pause commit barrier");
        }
        finally { release.Set(); Directory.Delete(dir, true); }
    }
    private static async Task MalformedMessage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oda-invalid-" + Guid.NewGuid().ToString("N"));
        await using var fake = await FakeOda.Start();
        try
        {
            new SecretVault(dir).Save(new Credentials("test-client", "test-secret", "synthetic-refresh", "synthetic-access", DateTimeOffset.UtcNow.AddMinutes(5)));
            int count = 0;
            using (var bridge = new BridgeService(dir, _ => Interlocked.Increment(ref count), fake.Endpoints))
            {
                fake.Pending["1"] = "{broken private body"; fake.Pending["2"] = Event("valid");
                await bridge.SetEnabled(true); await Until(() => !bridge.Status().Enabled && bridge.Status().BlockedMessage != null);
                Check(fake.Acks == 0 && count == 0, "malformed should pause before ACK");
                await bridge.SkipBlocked(); await bridge.SetEnabled(true); await Until(() => fake.Acks == 2); await bridge.SetEnabled(false);
                Check(count == 1, "explicit skip");
            }
            Check(!File.ReadAllText(Path.Combine(dir, "deliveries.jsonl")).Contains("private body"), "skip without payload storage");
        }
        finally { Directory.Delete(dir, true); }
    }
    internal static string Event(string id) => JsonSerializer.Serialize(new { id, originId = id, recipientId = "recipient", type = "payment", system = "ODA", timestamp = "2026-11-02T00:00:00Z", nickname = "Synthetic", message = "demo", amount = new { major = 12, minor = 34, currency = "EUR" } });
    internal static void Check(bool b, string message) { if (!b) throw new Exception(message); }
    internal static async Task Until(Func<bool> check) { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12)); while (!check()) await Task.Delay(30, cts.Token); }
    private static async Task ExpectThrows(Func<Task> run) { try { await run(); } catch { return; } throw new Exception("exception expected"); }
}
