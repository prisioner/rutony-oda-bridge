using System.Security.Cryptography;
using System.Reflection;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using RutonyOda;

internal static class HardeningChecks
{
    public static async Task Run()
    {
        var failures = new List<string>();
        foreach (var (name, check) in new (string, Func<Task>)[] {
            ("successful HTTP bodies obey full deadline", HttpBodies),
            ("shutdown cancels login before waiting for controls", () => ShutdownLogin(false)),
            ("native RuntimeHost shutdown cancels active HTTP callback", () => ShutdownLogin(true)),
            ("disposed bridge rejects mutation", DisposedCalls)
        })
        {
            try { await check(); Console.WriteLine("PASS " + name); }
            catch (Exception e) { failures.Add(name + ": " + e.Message); Console.WriteLine("FAIL " + failures[^1]); }
        }
        if (failures.Count != 0) throw new Exception(string.Join("; ", failures));
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task HttpBodies()
    {
        await using var fake = await FakeOda.Start();
        fake.TokenBodyRelease = Gate(); fake.SubscriptionBodyRelease = Gate();
        fake.ExpectedChallenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes("verifier"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var api = new OdaApi(fake.Endpoints);
        var credentials = new Credentials("test-client", "test-secret", "synthetic-refresh", "synthetic-access", DateTimeOffset.UtcNow.AddMinutes(5));
        var operations = new Task[] { api.Exchange(credentials, "code", "verifier", "http://127.0.0.1:12345/oauth/callback"), api.Refresh(credentials, CancellationToken.None), api.EnsureSubscription(credentials, CancellationToken.None) };
        try
        {
            await Task.WhenAll(fake.TokenBodyStarted.Task, fake.SubscriptionBodyStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            var all = Task.WhenAll(operations);
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(23)));
            Check(operations.All(t => t.IsCanceled), "successful body escaped the 20-second HTTP deadline");
        }
        finally
        {
            fake.TokenBodyRelease.TrySetResult(); fake.SubscriptionBodyRelease.TrySetResult();
            foreach (var task in operations) { try { await task; } catch (OperationCanceledException) { } }
        }
    }
    private static async Task ShutdownLogin(bool runtime)
    {
        var dir = NewDirectory();
        await using var fake = await FakeOda.Start();
        fake.TokenBodyRelease = Gate();
        var service = new BridgeService(dir, _ => { }, fake.Endpoints);
        Task? login = null, pause = null, stop = null;
        RuntimeHost? host = null;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        try
        {
            await service.Configure("test-client", "test-secret");
            var before = File.ReadAllBytes(Path.Combine(dir, "credentials.bin"));
            var redirect = "http://127.0.0.1:12345/oauth/callback";
            if (runtime)
            {
                var server = BridgeServer.Start(Path.Combine(Directory.GetCurrentDirectory(), "src", "RutonyOda"), service, 0);
                host = (RuntimeHost)Activator.CreateInstance(typeof(RuntimeHost), BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new object[] { service, server }, CultureInfo.InvariantCulture)!;
                redirect = "http://127.0.0.1:" + server.Port + "/oauth/callback";
            }
            var query = QueryHelpers.ParseQuery(new Uri(service.BeginAuthorization(redirect)).Query);
            fake.ExpectedChallenge = query["code_challenge"].ToString();
            async Task Callback()
            {
                using var response = await http.GetAsync(redirect + "?state=" + query["state"] + "&code=synthetic-code");
                Check(response.Headers.Location?.OriginalString == "/control?login=failed", "shutdown callback reported login success");
            }
            login = runtime ? Callback() : service.FinishAuthorization(query["state"].ToString(), "synthetic-code");
            await fake.TokenBodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pause = service.SetEnabled(false);
            stop = runtime ? UiThreadCheck.Run(() => host!.Dispose(), "active OAuth shutdown") : Task.Run(service.Dispose);
            await Task.WhenAny(stop, Task.Delay(2000));
            Check(stop.IsCompletedSuccessfully, "Dispose waits for the in-flight login instead of cancelling it");
            try { await login; } catch (OperationCanceledException) { }
            Check(runtime || login.IsCanceled, "shutdown must cancel the active login");
            try { await pause; } catch (ObjectDisposedException) { }
            Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "credentials.bin"))), "late login saved credentials after shutdown");
        }
        finally
        {
            fake.TokenBodyRelease.TrySetResult();
            foreach (var task in new[] { login, pause, stop }) if (task != null) { try { await task; } catch { } }
            host?.Dispose(); service.Dispose(); Directory.Delete(dir, true);
        }
    }
    private static async Task DisposedCalls()
    {
        var dir = NewDirectory();
        try
        {
            var service = new BridgeService(dir, _ => { });
            await service.Configure("test-client", "test-secret"); service.Dispose();
            foreach (var call in new Func<Task>[] {
                () => service.Configure("test-client", "new-secret"), () => service.SetSources(["ODA"]),
                () => service.SetEnabled(false), () => service.Resolve("missing", false), () => service.SkipBlocked(),
                () => { service.BeginAuthorization("http://127.0.0.1:12345/oauth/callback"); return Task.CompletedTask; },
                () => service.FinishAuthorization("missing", "code"), () => { service.Resume(); return Task.CompletedTask; }
            })
            {
                try { await call(); } catch (ObjectDisposedException) { continue; }
                throw new Exception("disposed bridge accepted a mutation");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
    private static string NewDirectory() { var path = Path.Combine(Path.GetTempPath(), "oda-hardening-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
