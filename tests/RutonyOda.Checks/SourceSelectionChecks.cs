using System.Net;
using System.Text;
using System.Text.Json;
using RutonyOda;

internal static class SourceSelectionChecks
{
    public static async Task Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oda-sources-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        await using var fake = await FakeOda.Start(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Enabled\":false,\"InstallationId\":\"11111111111111111111111111111111\"}");
            new SecretVault(dir).Save(new Credentials("test-client", "test-secret", "synthetic-refresh", "synthetic-access", DateTimeOffset.UtcNow.AddMinutes(5)));
            var seen = new List<Donation>();
            using (var bridge = new BridgeService(dir, d => { seen.Add(d); entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }, fake.Endpoints))
            {
                Integration.Check(bridge.Status().SourceSystems.SequenceEqual(["ODA"]), "legacy settings default");
                var snapshot = bridge.Status(); snapshot.SourceSystems[0] = "DonationAlerts"; Integration.Check(bridge.Status().SourceSystems.SequenceEqual(["ODA"]), "status cannot mutate live selection");
                using var server = BridgeServer.Start(Path.Combine(Directory.GetCurrentDirectory(), "src", "RutonyOda"), bridge, 0);
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + server.Port) };
                using (var denied = await http.PostAsync("/api/sources", Body(["DonationAlerts"]))) Integration.Check(denied.StatusCode == HttpStatusCode.Forbidden, "source config CSRF guard");
                http.DefaultRequestHeaders.Add("X-Bridge-Token", server.ControlToken);
                using (var invalid = await http.PostAsync("/api/sources", Body(["unknown"]))) Integration.Check(invalid.StatusCode == HttpStatusCode.BadRequest, "unknown source rejected");
                fake.Pending["1"] = Integration.Event("same"); await bridge.SetEnabled(true); await Integration.Until(() => entered.IsSet);
                var change = http.PostAsync("/api/sources", Body(["DonationAlerts", "DonatePay"])); await Task.Delay(80); Integration.Check(!change.IsCompleted, "source change must wait for native delivery"); release.Set(); using (var result = await change) Integration.Check(result.IsSuccessStatusCode, "source selection saved");
                Integration.Check(!bridge.Status().Enabled && bridge.Status().Authorized && bridge.Status().SourceSystems.SequenceEqual(["DonationAlerts", "DonatePay"]), "paused, authorized, sources retained");
            }
            await Integration.Until(() => fake.ActiveConsumers == 0); fake.Pending.Clear();
            using (var bridge = new BridgeService(dir, d => seen.Add(d), fake.Endpoints))
            {
                Integration.Check(bridge.Status().SourceSystems.SequenceEqual(["DonationAlerts", "DonatePay"]) && bridge.Status().Authorized, "restart persists selection and authorization");
                fake.Pending["1"] = Integration.Event("same"); fake.Pending["2"] = Integration.Event("same").Replace("ODA", "DonationAlerts"); fake.Pending["3"] = fake.Pending["2"]; fake.Pending["4"] = Integration.Event("same").Replace("ODA", "DonatePay");
                int ackBefore = fake.Acks; await bridge.SetEnabled(true); await Integration.Until(() => fake.Acks >= ackBefore + 4); await bridge.SetEnabled(false);
                Integration.Check(seen.Count == 3 && seen.Select(d => d.Key).Distinct().Count() == 3, "different systems distinct; imported duplicate suppressed; ODA filtered");
                await bridge.SetSources([]); Integration.Check(bridge.Status().SourceSystems.Length == 0 && bridge.Status().Authorized, "empty selection without credential reset");
            }
        }
        finally { release.Set(); Directory.Delete(dir, true); }
    }
    private static StringContent Body(string[] systems) => new(JsonSerializer.Serialize(new { systems }), Encoding.UTF8, "application/json");
}
