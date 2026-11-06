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

internal sealed class FakeOda : IAsyncDisposable
{
    private readonly WebApplication app;
    public OdaEndpoints Endpoints { get; private set; } = null!;
    public string ExpectedChallenge = "";
    public bool ValidTokenExchange, ValidSubscribe;
    private bool correctVhost;
    public int Subscriptions, Acks, Refreshes, Connections;
    public int ActiveConsumers;
    public int TokenStatus = 200, SubscriptionStatus = 200, TokenRequests, SubscriptionRequests;
    public string? TokenErrorBody { get; set; }
    public int TokenErrorDelay { get; set; }
    public TaskCompletionSource? TokenBodyRelease { get; set; }
    public TaskCompletionSource? SubscriptionBodyRelease { get; set; }
    public TaskCompletionSource TokenBodyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously), SubscriptionBodyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool HeartbeatBeforeConnected { get; set; }
    public bool HeartbeatBeforeReceipt { get; set; }
    public TaskCompletionSource HeartbeatStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool queueExists;
    public ConcurrentDictionary<string, byte> QueueNames = new();
    public bool Publish(string id, string body) { if (!queueExists) return false; Pending[id] = body; return true; }
    public bool CloseOnce;
    public ConcurrentDictionary<string, string> Pending = new();
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private FakeOda(WebApplication app) { this.app = app; }
    public static async Task<FakeOda> Start()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var app = builder.Build(); var self = new FakeOda(app); app.UseWebSockets();
        app.MapPost("/token", async (HttpContext c) =>
        {
            Interlocked.Increment(ref self.TokenRequests);
            if (self.TokenStatus != 200) { c.Response.StatusCode = self.TokenStatus; if (self.TokenErrorDelay > 0) { await c.Response.StartAsync(); await c.Response.Body.FlushAsync(); await Task.Delay(self.TokenErrorDelay, c.RequestAborted); } if (self.TokenErrorBody != null) await c.Response.WriteAsync(self.TokenErrorBody); else await c.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "sensitive-provider-error" }); return; }
            var f = await c.Request.ReadFormAsync();
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(f["code_verifier"].ToString()))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            bool refresh = f["grant_type"] == "refresh_token" && f["refresh_token"] == "synthetic-refresh";
            self.ValidTokenExchange = f["client_id"] == "test-client" && f["client_secret"] == "test-secret" && ((challenge == self.ExpectedChallenge && f["grant_type"] == "authorization_code") || refresh);
            if (refresh) Interlocked.Increment(ref self.Refreshes);
            if (!self.ValidTokenExchange) { c.Response.StatusCode = 400; return; }
            if (self.TokenBodyRelease != null) { await c.Response.StartAsync(); await c.Response.Body.FlushAsync(); self.TokenBodyStarted.TrySetResult(); await self.TokenBodyRelease.Task.WaitAsync(c.RequestAborted); }
            await c.Response.WriteAsync(JsonSerializer.Serialize(new { access_token = "synthetic-access", refresh_token = "synthetic-refresh", expires_in = 300 }));
        });
        app.MapGet("/subscriptions", async (HttpContext c) =>
        {
            Interlocked.Increment(ref self.SubscriptionRequests);
            if (self.SubscriptionStatus != 200) return Results.StatusCode(self.SubscriptionStatus);
            Check(c.Request.Headers.Authorization == "Bearer synthetic-access", "access token header");
            if (self.SubscriptionBodyRelease != null) { await c.Response.StartAsync(); await c.Response.Body.FlushAsync(); self.SubscriptionBodyStarted.TrySetResult(); await self.SubscriptionBodyRelease.Task.WaitAsync(c.RequestAborted); await c.Response.WriteAsync("[]"); return Results.Empty; }
            return self.Subscriptions == 0 ? Results.Json(Array.Empty<object>()) : Results.Json(new[] { new { subscriberId = "test-client", events = new[] { "HistoryItemEvent" } } });
        });
        app.MapPost("/subscriptions/commands/add-queue-subscription", async (HttpContext c) =>
        {
            var body = await JsonDocument.ParseAsync(c.Request.Body); Check(body.RootElement.GetProperty("events")[0].GetString() == "HistoryItemEvent", "event selection"); Interlocked.Increment(ref self.Subscriptions); c.Response.StatusCode = 200;
        });
        app.Map("/ws", async c =>
        {
            using var ws = await c.WebSockets.AcceptWebSocketAsync("v12.stomp");
            Interlocked.Increment(ref self.Connections);
            Interlocked.Increment(ref self.ActiveConsumers);
            var decoder = new StompDecoder(); var bytes = new byte[8192];
            bool created = false, exclusive = true, autoDelete = true;
            string? inFlight = null;
            async Task Heartbeats()
            {
                self.HeartbeatStarted.TrySetResult();
                while (!c.RequestAborted.IsCancellationRequested) { await ws.SendAsync(new byte[] { 10 }, WebSocketMessageType.Text, true, c.RequestAborted); await Task.Delay(25, c.RequestAborted); }
            }
            async Task SendNext()
            {
                var next = self.Pending.OrderBy(x => x.Key).FirstOrDefault();
                if (next.Key == null) { inFlight = null; return; }
                inFlight = next.Key; await Send(ws, "MESSAGE", new() { { "ack", next.Key }, { "subscription", "donations" } }, next.Value);
            }
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(bytes, c.RequestAborted); if (result.MessageType == WebSocketMessageType.Close) break;
                    foreach (var frame in decoder.Feed(bytes[..result.Count]))
                    {
                        if (frame.Command == "CONNECT")
                        {
                            self.correctVhost = !frame.Headers.ContainsKey("host");
                            if (self.CloseOnce) { self.CloseOnce = false; await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "synthetic reconnect", c.RequestAborted); return; }
                            Check(frame.Headers["passcode"] == "synthetic-access", "STOMP passcode");
                            if (self.HeartbeatBeforeConnected) { await Heartbeats(); return; }
                            await Send(ws, "CONNECTED", new() { { "version", "1.2" }, { "heart-beat", "0,0" } });
                        }
                        else if (frame.Command == "SUBSCRIBE")
                        {
                            created = true; self.queueExists = true; self.QueueNames.TryAdd(frame.Headers["x-queue-name"], 0);
                            exclusive = frame.Headers.GetValueOrDefault("exclusive") != "false";
                            autoDelete = frame.Headers.GetValueOrDefault("auto-delete") != "false";
                            self.ValidSubscribe = self.correctVhost && frame.Headers["ack"] == "client-individual" && frame.Headers["destination"] == "/exchange/subscriptions/test-client" && frame.Headers["x-queue-name"].StartsWith("rutony-oda-", StringComparison.Ordinal);
                            if (self.HeartbeatBeforeReceipt) { await Heartbeats(); return; }
                            await Send(ws, "RECEIPT", new() { { "receipt-id", frame.Headers["receipt"] } });
                            await SendNext();
                        }
                        else if (frame.Command == "ACK") { if (frame.Headers["id"] == inFlight && self.Pending.TryRemove(inFlight, out _)) Interlocked.Increment(ref self.Acks); await SendNext(); }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally { if (created && (exclusive || autoDelete)) { self.Pending.Clear(); self.queueExists = false; } Interlocked.Decrement(ref self.ActiveConsumers); }
        });
        await app.StartAsync(); var root = app.Urls.Single(); self.Endpoints = OdaEndpoints.ForTests(root); return self;
    }
    private static Task Send(WebSocket ws, string command, Dictionary<string, string> headers, string body = "") => ws.SendAsync(StompCodec.Encode(command, headers, Encoding.UTF8.GetBytes(body)), WebSocketMessageType.Text, true, CancellationToken.None);
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}
