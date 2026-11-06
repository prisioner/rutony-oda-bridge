using System.Globalization;
using System.Text;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Xml.Linq;
using RutonyChat;
using RutonyOda;

await WorkshopChecks.Run();

var directory = Path.Combine(Path.GetTempPath(), "oda-native-contract-" + Guid.NewGuid().ToString("N"));
var originalCulture = CultureInfo.CurrentCulture;
var received = new List<(ProgramProps.SiteEnum Site, string Name, float Amount, string Currency, string Text)>();
void Receive(ProgramProps.SiteEnum site, string name, CurrencyControl.SumClass sum, string text, string chat)
    => received.Add((site, name, sum.sum, sum.currency.CharCode, text));
try
{
    ChatEventServer.OnDonate += Receive;
    var script = new Script();
    var method = typeof(Script).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic);
    var payload = Encoding.UTF8.GetBytes("""{"id":"id","recipientId":"recipient","type":"payment","system":"ODA","timestamp":"2026-11-02T00:00:00Z","nickname":"Synthetic Donor","message":"demo","amount":{"major":12,"minor":34,"currency":"EUR"}}""");
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
    void Publish(Donation donation) => method.Invoke(script, [donation.Name, donation.Amount.ToString("0.00", CultureInfo.InvariantCulture), donation.Currency, donation.Message, "oda:" + donation.Key]);
    using (var ledger = new DeliveryLedger(Path.Combine(directory, "oda")))
    {
        var transaction = new DeliveryTransaction(ledger, Publish);
        transaction.Deliver(payload, ["ODA"]); transaction.Deliver(payload, ["ODA"]);
        if (ledger.Delivered != 1 || ledger.Uncertain.Length != 0) throw new Exception("delivery transaction did not commit");
    }
    using (var reopened = new DeliveryLedger(Path.Combine(directory, "oda")))
        new DeliveryTransaction(reopened, Publish).Deliver(payload, ["ODA"]);
    if (received.Count != 1 || received[0].Site != ProgramProps.SiteEnum.other || received[0].Name != "Synthetic Donor" || received[0].Text != "demo" || received[0].Currency != "EUR" || Math.Abs(received[0].Amount - 12.34f) > 0.0001f || ChatEventServer.LastId != "oda:recipient/id") throw new Exception("native contract mismatch");
    RutonyBot.Messages.Clear();
    int customPort;
    using (var reservation = new TcpListener(IPAddress.Loopback, 0)) { reservation.Start(); customPort = ((IPEndPoint)reservation.LocalEndpoint).Port; }
    var initialized = new Script();
    try
    {
        initialized.InitParams(new()
        {
            ["PackageDirectory"] = AppContext.BaseDirectory,
            ["DataDirectory"] = Path.Combine(directory, "native-init"),
            ["Port"] = customPort.ToString(CultureInfo.InvariantCulture)
        });
        var rendered = XElement.Parse("<root>" + RutonyBot.Messages.Single() + "</root>");
        var link = rendered.Elements("a").Single();
        if (link.Value != "Открыть панель ODA" || link.Attribute("href")?.Value != "http://127.0.0.1:" + customPort.ToString(CultureInfo.InvariantCulture) + "/control"
            || link.Attribute("target")?.Value != "_blank" || link.Attribute("rel")?.Value != "noopener noreferrer"
            || rendered.Descendants("a").Count() != 1 || rendered.Elements().Count() != 1)
            throw new Exception("Native management link must be one safe explicit anchor with the configured port");
        using var client = new HttpClient();
        var state = await client.GetFromJsonAsync<BridgeStatus>("http://127.0.0.1:" + customPort + "/api/state");
        if (state.Authorized || state.Enabled || received.Count != 1) throw new Exception("Link initialization changed delivery behavior");
    }
    finally { initialized.Closing(); initialized.Closing(); }
    using (var reusable = new TcpListener(IPAddress.Loopback, customPort)) { reusable.Start(); }
    Console.WriteLine("PASS native InitParams explicit management anchor, custom port, inactive delivery and idempotent Closing");
    Console.WriteLine("PASS ODA -> thin native script -> general OnDonate subscriber (isolated fake host, ru-RU, EUR 12.34, name, text, message_id, persistent dedup)");
}
finally
{
    ChatEventServer.OnDonate -= Receive;
    CultureInfo.CurrentCulture = originalCulture;
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
}
