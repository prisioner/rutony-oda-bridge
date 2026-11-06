using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RutonyOda;
public sealed class BridgeServer : IDisposable
{
    private sealed record Config(string? ClientId, string? ClientSecret);
    private sealed record Toggle(bool Enabled);
    private sealed record Sources(string[]? Systems);
    private sealed record Resolution(string? Key, string? Decision);
    private readonly WebApplication app;
    private sealed class EmbeddedLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
    public int Port { get; private set; }
    public string ControlToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private BridgeServer(WebApplication app) { this.app = app; }
    public static BridgeServer Start(string packageDirectory, BridgeService service, int port)
    {
        var web = Path.Combine(Path.GetFullPath(packageDirectory), "web");
        foreach (var file in new[] { "control.html", "control.js", "control.css" }) if (!File.Exists(Path.Combine(web, file))) throw new FileNotFoundException("Нет файлов панели ODA.");
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(BridgeServer).Assembly.GetName().Name, ContentRootPath = web }); builder.Logging.ClearProviders();
        builder.Services.AddRouting(); builder.Services.AddSingleton<IHostLifetime, EmbeddedLifetime>();
        builder.WebHost.UseKestrel(o => { o.AddServerHeader = false; o.Listen(IPAddress.Loopback, port); o.Limits.MaxRequestBodySize = 16384; });
        var app = builder.Build(); var self = new BridgeServer(app);
        app.Use(async (c, next) =>
        {
            c.Response.Headers.CacheControl = "no-store"; c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            c.Response.Headers["Referrer-Policy"] = "no-referrer";
            c.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            var expected = "127.0.0.1:" + self.Port;
            if (c.Request.Host.Value != expected || c.Connection.RemoteIpAddress == null || !IPAddress.IsLoopback(c.Connection.RemoteIpAddress)) { c.Response.StatusCode = 403; return; }
            if (c.Request.Method == "POST")
            {
                if (service.IsPreview && c.Request.Path is var path && (path == "/api/configure" || path == "/api/authorize")) { c.Response.StatusCode = 403; return; }
                var origin = c.Request.Headers.Origin.ToString(); var token = c.Request.Headers["X-Bridge-Token"].ToString();
                if ((origin != "" && origin != "http://" + expected) || c.Request.Headers["Sec-Fetch-Site"] == "cross-site" || token.Length != self.ControlToken.Length || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(self.ControlToken)) || !c.Request.HasJsonContentType()) { c.Response.StatusCode = 403; return; }
            }
            try { await next(c); }
            catch (Exception e) when (!c.Response.HasStarted)
            {
                c.Response.StatusCode = 400;
                string message = e is OdaFault or ArgumentException or InvalidOperationException ? e.Message : "Операция не выполнена. Проверьте соединение, настройки и доступ к данным.";
                await c.Response.WriteAsJsonAsync(new { error = message });
            }
        });
        app.MapGet("/", () => Results.Redirect("/control"));
        app.MapGet("/control", () => Results.Content(File.ReadAllText(Path.Combine(web, "control.html")).Replace("__TOKEN__", self.ControlToken), "text/html; charset=utf-8"));
        app.MapGet("/control.js", () => Results.File(Path.Combine(web, "control.js"), "text/javascript"));
        app.MapGet("/control.css", () => Results.File(Path.Combine(web, "control.css"), "text/css"));
        app.MapGet("/api/state", () => Results.Json(service.Status()));
        app.MapPost("/api/sources", async (HttpContext c) => { var body = await c.Request.ReadFromJsonAsync<Sources>() ?? throw new ArgumentException("Нет настроек источников."); await service.SetSources(body.Systems ?? throw new ArgumentException("Выберите источники.")); return Results.Ok(); });
        app.MapPost("/api/configure", async (HttpContext c) => { var body = await c.Request.ReadFromJsonAsync<Config>() ?? throw new ArgumentException("Нет настроек."); await service.Configure(body.ClientId ?? "", body.ClientSecret ?? ""); return Results.Ok(); });
        app.MapPost("/api/authorize", () => Results.Json(new { url = service.BeginAuthorization("http://127.0.0.1:" + self.Port + "/oauth/callback") }));
        app.MapGet("/oauth/callback", async (HttpContext c) =>
        {
            try { await service.FinishAuthorization(c.Request.Query["state"].ToString(), c.Request.Query["code"].ToString(), c.Request.Query["error"].ToString()); return Results.Redirect("/control?login=ok"); }
            catch { return Results.Redirect("/control?login=failed"); }
        });
        app.MapPost("/api/enabled", async (HttpContext c) => { var body = await c.Request.ReadFromJsonAsync<Toggle>() ?? throw new ArgumentException("Нет команды."); await service.SetEnabled(body.Enabled); return Results.Ok(); });
        app.MapPost("/api/resolve", async (HttpContext c) => { var body = await c.Request.ReadFromJsonAsync<Resolution>() ?? throw new ArgumentException("Нет решения."); if (body.Decision is not ("delivered" or "retry")) throw new ArgumentException("Выберите решение."); await service.Resolve(body.Key ?? "", body.Decision == "delivered"); return Results.Ok(); });
        app.MapPost("/api/skip", async () => { await service.SkipBlocked(); return Results.Ok(); });
        try { app.StartAsync().GetAwaiter().GetResult(); self.Port = new Uri(app.Urls.Single()).Port; return self; } catch { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }
    public void Dispose() { app.StopAsync().GetAwaiter().GetResult(); app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}
