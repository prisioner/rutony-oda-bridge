using System.Text;
using System.Text.Json;
using RutonyOda;

var cases = new List<(string, Action)> {
 ("amount, currency, provider filter", () => {
     var p = Event("one", 12, 34, "EUR");
     var e = OdaEventParser.Parse(p)!;
     Check(e.Amount == 12.34m && e.Currency == "EUR" && e.Key == "recipient/one");
     Check(OdaEventParser.Parse(p.Replace("ODA", "DA")) == null);
     Check(OdaEventParser.Parse(p.Replace("payment", "subscription")) == null);
     Throws(() => OdaEventParser.Parse(Event("x", 1, 100)));
     Throws(() => OdaEventParser.Parse(Event("x", 9000000, 1)));
     Check(OdaEventParser.Parse(Event("two", 1, 0))!.Key != e.Key);
 }),
 ("source selection and provider ID namespaces", () => {
     var oda=Event("same",1,1);var da=oda.Replace("ODA","DonationAlerts");
     Check(OdaEventParser.Parse(da)==null);
     var imported=OdaEventParser.Parse(da,["DonationAlerts"])!;
     Check(imported.Key!=OdaEventParser.Parse(oda)!.Key);
     Check(imported.Key==OdaEventParser.Parse(da,["ODA","DonationAlerts"])!.Key);
     Check(imported.Key!=OdaEventParser.Parse(oda.Replace("ODA","DonatePay"),["DonatePay"])!.Key);
     Check(OdaEventParser.Parse(oda,["DonationAlerts"])==null && OdaEventParser.Parse(oda,[])==null);
     Check(OdaEventParser.Parse(da.Replace("payment","subscription"),["DonationAlerts"])==null);
     var digest=imported.Key[9..];
     var legacy=oda.Replace("\"recipientId\":\"recipient\"","\"recipientId\":\"imported\"").Replace("\"originId\":\"same\"","\"originId\":\""+digest+"\"");
     Check(OdaEventParser.Parse(legacy)!.Key!=imported.Key);
 }),
 ("STOMP byte fragmentation, NUL and escapes", () => {
     var decoder = new StompDecoder();
     var encoded = StompCodec.Encode("MESSAGE", new Dictionary<string,string> { ["ack"]="a:b\nx", ["content-length"]="ignored" }, Encoding.UTF8.GetBytes("Я\0Z"));
     var frames = new List<StompFrame>();
     foreach (var b in encoded) frames.AddRange(decoder.Feed([b]));
     Check(frames.Count == 1 && frames[0].Headers["ack"] == "a:b\nx" && Encoding.UTF8.GetString(frames[0].Body) == "Я\0Z");
     Check(decoder.Feed(Encoding.UTF8.GetBytes("\r\n\nCONNECTED\r\nversion:1.2\r\n\r\n\0")).Single().Command == "CONNECTED");
     Throws(() => new StompDecoder().Feed(Encoding.UTF8.GetBytes("MESSAGE\ncontent-length:2000000\n\n")));
     Throws(() => new StompDecoder().Feed(Encoding.UTF8.GetBytes("MESSAGE\nbad:\\z\n\nx\0")));
 }),
 ("skip namespace and compatibility of previous skip records", () => WithDir(dir => {
     const string hash="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
     using(var ledger=new DeliveryLedger(dir)) {
         ledger.Skip("invalid:"+hash);
         var legacy=Event(hash,1,0).Replace("\"recipientId\":\"recipient\"","\"recipientId\":\"invalid\"");
         var e=OdaEventParser.Parse(legacy)!;Check(ledger.Prepare(e));ledger.Commit(e.Key);
         ledger.Skip("invalid/old-fingerprint");
     }
     using(var ledger=new DeliveryLedger(dir)) {Check(ledger.IsSkipped("invalid:"+hash));Check(ledger.IsSkipped("invalid:old-fingerprint"));}
 })),
 ("durable delivery, ambiguous outcome, explicit retry", () => WithDir(dir => {
     var e = OdaEventParser.Parse(Event("a", 1, 2))!;
     using (var ledger = new DeliveryLedger(dir)) {
         Check(ledger.Prepare(e));
         Throws(() => new DeliveryLedger(dir));
     }
     using (var ledger = new DeliveryLedger(dir)) {
         Check(ledger.Uncertain.Single() == e.Key);
         Throws(() => ledger.Prepare(e));
         ledger.Resolve(e.Key, false);
         Check(ledger.Prepare(e)); ledger.Commit(e.Key);
         Check(!ledger.Prepare(e));
     }
     var raw = File.ReadAllText(Path.Combine(dir,"deliveries.jsonl"));
     Check(!raw.Contains("Demo Name") && !raw.Contains("private message"));
     using (var ledger = new DeliveryLedger(dir)) Check(!ledger.Prepare(e));
     File.AppendAllText(Path.Combine(dir,"deliveries.jsonl"), "broken\n");
     Throws(() => new DeliveryLedger(dir));
 })),
 ("DPAPI credential vault", () => WithDir(dir => {
     var vault = new SecretVault(dir);
     vault.Save(new Credentials("client", "synthetic-secret", "refresh", "access", DateTimeOffset.UtcNow));
     Check(vault.Load()!.ClientSecret == "synthetic-secret");
     Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir,"credentials.bin"))).Contains("synthetic-secret"));
     File.WriteAllBytes(Path.Combine(dir,"credentials.bin"), [1,2,3]);
     Throws(() => vault.Load());
 })),
 ("failed flush latches journal before another prepare", () => WithDir(dir => {
     bool fail=true;
     using var ledger=new DeliveryLedger(dir,(file,bytes)=> {file.Write(bytes);if(fail) {fail=false;throw new IOException("synthetic flush failure");}file.Flush(true);});
     var e=OdaEventParser.Parse(Event("flush",1,2))!;
     Throws(()=>ledger.Prepare(e));
     Throws(()=>ledger.Prepare(e));
 })),
 ("failed commit cannot be followed by a duplicate resolution", () => WithDir(dir => {
     bool fail=false;
     using var ledger=new DeliveryLedger(dir,(file,bytes)=> {file.Write(bytes);if(fail) {fail=false;throw new IOException("synthetic flush failure");}file.Flush(true);});
     var e=OdaEventParser.Parse(Event("commit",1,2))!;
     Check(ledger.Prepare(e));fail=true;Throws(()=>ledger.Commit(e.Key));Throws(()=>ledger.Resolve(e.Key,true));
 })),
 ("missing and incomplete ledger cannot silently restart", () => WithDir(dir => {
     using(var ledger=new DeliveryLedger(dir)) {}
     File.Delete(Path.Combine(dir,"deliveries.jsonl"));Throws(()=>new DeliveryLedger(dir));
     File.WriteAllText(Path.Combine(dir,"deliveries.jsonl"),"{}");Throws(()=>new DeliveryLedger(dir));
     File.Delete(Path.Combine(dir,"ledger.version"));Throws(()=>new DeliveryLedger(dir));
 }))
};
if (args.Contains("--stomp-hardening")) { await StompHardeningChecks.Run(); return; }
if (args.Contains("--hardening")) { await HardeningChecks.Run(); return; }
int failed = 0;
foreach (var (name, run) in cases) { try { run(); Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
if (failed != 0) Environment.Exit(1);
try { await StompHardeningChecks.Run(); } catch (Exception e) { Console.WriteLine("FAIL STOMP hardening: " + e); Environment.Exit(1); }
try { await HardeningChecks.Run(); } catch (Exception e) { Console.WriteLine("FAIL hardening: " + e); Environment.Exit(1); }
try { await SourceSelectionChecks.Run(); Console.WriteLine("PASS source settings migration, HTTP guard, pause barrier, provider ID separation, persistence and credentials"); } catch (Exception e) { Console.WriteLine("FAIL source selection: " + e); Environment.Exit(1); }
try { await LifecycleChecks.Run(); Console.WriteLine("PASS UI lifecycle before login, active delivery, port reuse and durable dedup"); } catch (Exception e) { Console.WriteLine("FAIL lifecycle: " + e); Environment.Exit(1); }
try { await Integration.Run(); Console.WriteLine("PASS OAuth/HTTP guards, WS ACK/dedup, uncertain resolution, refresh/reconnect, pause/offline backlog, invalid skip, auth and I/O failures"); } catch (Exception e) { Console.WriteLine("FAIL integration: " + e); Environment.Exit(1); }

static string Event(string id, int major, int minor, string currency = "RUB") => JsonSerializer.Serialize(new { id, originId = id, recipientId = "recipient", type = "payment", system = "ODA", timestamp = DateTimeOffset.UtcNow.ToString("O"), nickname = "Demo Name", message = "private message", amount = new { major, minor, currency } });
static void Check(bool value) { if (!value) throw new Exception("assertion failed"); }
static void Throws(Action run) { try { run(); } catch { return; } throw new Exception("exception expected"); }
static void WithDir(Action<string> run) { var dir = Path.Combine(Path.GetTempPath(), "oda-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); try { run(dir); } finally { Directory.Delete(dir, true); } }
