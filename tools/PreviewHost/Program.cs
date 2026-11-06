using RutonyOda;

if(args.Length!=3) throw new ArgumentException("Usage: OdaPreviewHost <package-folder> <isolated-data-folder> <port>");
var data=Path.GetFullPath(args[1]);
var marker=Path.Combine(data,".preview-only");
if(Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any() && !File.Exists(marker)) throw new InvalidOperationException("Choose an empty preview data directory. Existing application data cannot be used.");
Directory.CreateDirectory(data);File.WriteAllText(marker,"isolated-preview");
await using var fake=await FakeOda.Start();
new SecretVault(data).Save(new Credentials("test-client","test-secret","synthetic-refresh","synthetic-access",DateTimeOffset.UtcNow.AddMinutes(5)));
fake.Pending["demo"]=System.Text.Json.JsonSerializer.Serialize(new {id="preview-"+Guid.NewGuid().ToString("N"),recipientId="preview",type="payment",system="ODA",timestamp=DateTimeOffset.UtcNow.ToString("O"),nickname="Synthetic Donor",message="Demo",amount=new {major=12,minor=34,currency="EUR"}});
using var service=new BridgeService(data,_=>Console.WriteLine("Synthetic donation received by isolated preview receiver."),fake.Endpoints,preview:true);
using var server=BridgeServer.Start(args[0],service,int.Parse(args[2],System.Globalization.CultureInfo.InvariantCulture));
Console.WriteLine("ISOLATED PREVIEW: http://127.0.0.1:"+server.Port+"/control");
Console.WriteLine("Enable forwarding to deliver one synthetic donation. Ctrl+C stops the preview.");
var stop=new TaskCompletionSource();Console.CancelKeyPress+=(_,e)=> {e.Cancel=true;stop.TrySetResult();};await stop.Task;
