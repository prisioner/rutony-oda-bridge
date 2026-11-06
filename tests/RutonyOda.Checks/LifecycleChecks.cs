using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Globalization;
using System.Text.Json;
using RutonyOda;

internal static class LifecycleChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "oda-lifecycle-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(Directory.GetCurrentDirectory(), "src", "RutonyOda");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        RuntimeHost? host = null;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        await using var fake = await FakeOda.Start();
        int published = 0;
        RuntimeHost Own(BridgeService service, BridgeServer server) =>
            (RuntimeHost)Activator.CreateInstance(typeof(RuntimeHost), BindingFlags.NonPublic | BindingFlags.Instance,
                null, new object[] { service, server }, CultureInfo.InvariantCulture)!;
        try
        {
            await UiThreadCheck.Run(() => host = RuntimeHost.Start(package, directory, port, (_,_,_,_,_) => { }), "ODA start before login");
            await UiThreadCheck.Run(() => host!.Dispose(), "ODA shutdown before login"); host = null;
            new SecretVault(directory).Save(new Credentials("test-client", "test-secret", "synthetic-refresh", "synthetic-access", DateTimeOffset.UtcNow.AddMinutes(5)));
            var service = new BridgeService(directory, _ => { Interlocked.Increment(ref published); entered.Set(); release.Wait(TimeSpan.FromSeconds(8)); }, fake.Endpoints);
            host = Own(service, BridgeServer.Start(package, service, port));
            fake.Pending["lifecycle"] = JsonSerializer.Serialize(new {
                id = "lifecycle", originId = "lifecycle", recipientId = "recipient", type = "payment", system = "ODA",
                timestamp = DateTimeOffset.UtcNow.ToString("O"), nickname = "Lifecycle sample", message = "",
                amount = new { major = 1, minor = 0, currency = "RUB" }
            });
            await service.SetEnabled(true);
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Synthetic delivery barrier not reached");
            var closing = UiThreadCheck.Run(() => host.Dispose(), "ODA shutdown with active worker");
            await Task.Delay(100); release.Set();
            await closing; host = null;

            var next = new BridgeService(directory, _ => Interlocked.Increment(ref published), fake.Endpoints);
            host = Own(next, BridgeServer.Start(package, next, port));
            if (next.Status().Delivered != 1 || next.Status().Uncertain.Length != 0) throw new Exception("Shutdown did not finish the delivery journal");
            await next.SetEnabled(true);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (fake.Acks < 1 && DateTime.UtcNow < until) await Task.Delay(10);
            if (fake.Acks < 1 || published != 1) throw new Exception("Restart replayed native delivery or failed to ACK committed event");
            await UiThreadCheck.Run(() => host.Dispose(), "ODA repeated shutdown"); host = null;
            var finalService = new BridgeService(directory, _ => Interlocked.Increment(ref published), fake.Endpoints);
            using var final = Own(finalService, BridgeServer.Start(package, finalService, port));
        }
        finally { release.Set(); host?.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
