using System.Globalization;

namespace RutonyOda;
public sealed class RuntimeHost : IDisposable
{
    private readonly BridgeService service;
    private readonly BridgeServer server;
    private RuntimeHost(BridgeService service, BridgeServer server) { this.service = service; this.server = server; }
    public static RuntimeHost Start(string packageDirectory, string dataDirectory, int port, Action<string, string, string, string, string> publish) =>
        Task.Run(() => StartCore(packageDirectory, dataDirectory, port, publish)).GetAwaiter().GetResult();
    private static RuntimeHost StartCore(string packageDirectory, string dataDirectory, int port, Action<string, string, string, string, string> publish)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var service = new BridgeService(dataDirectory, d => publish(d.Name, d.Amount.ToString("0.00", CultureInfo.InvariantCulture), d.Currency, d.Message, "oda:" + d.Key));
        try { var server = BridgeServer.Start(packageDirectory, service, port); try { service.Resume(); return new RuntimeHost(service, server); } catch { server.Dispose(); throw; } } catch { service.Dispose(); throw; }
    }
    // The native script's synchronous Closing must not capture RutonyChat's UI
    // context in server/worker shutdown, and returns only after cleanup completes.
    public void Dispose() => Task.Run(() =>
    {
        service.RequestStop();
        try { server.Dispose(); }
        finally { service.Dispose(); }
    }).GetAwaiter().GetResult();
}
