using System.Diagnostics;
using System.Text;
using RutonyOda;

internal static class StompHardeningChecks
{
    public static async Task Run()
    {
        var failures = new List<string>();
        foreach (var (name, check) in new (string, Func<Task>)[] {
            ("streaming STOMP mixed EOL, fragmentation and coalescing", () => { Frames(); return Task.CompletedTask; }),
            ("streaming STOMP strict headers and frame bounds", () => { Bounds(); return Task.CompletedTask; }),
            ("CONNECT and SUBSCRIBE deadlines survive heartbeat traffic", Handshakes)
        })
        {
            try { await check(); Console.WriteLine("PASS " + name); }
            catch (Exception e) { failures.Add(name + ": " + e.Message); Console.WriteLine("FAIL " + failures[^1]); }
        }
        if (failures.Count != 0) throw new Exception(string.Join("; ", failures));
    }
    private static void Frames()
    {
        // STOMP EOL is LF with an optional preceding CR, independently for each line.
        var wire = Encoding.UTF8.GetBytes("\r\nMESSAGE\nack:a\\cb\r\ncontent-length:4\n\r\nЯ\0Z\0\nRECEIPT\r\nreceipt-id:ready\n\n\0\n");
        for (int chunk = 1; chunk <= wire.Length; chunk++)
        {
            var decoder = new StompDecoder(); var frames = new List<StompFrame>();
            for (int offset = 0; offset < wire.Length; offset += chunk) frames.AddRange(decoder.Feed(wire[offset..Math.Min(wire.Length, offset + chunk)]));
            Check(frames.Count == 2, "mixed legal EOL lost a frame at chunk " + chunk);
            Check(frames[0].Headers["ack"] == "a:b" && frames[0].Body.SequenceEqual(new byte[] { 0xd0, 0xaf, 0, 90 }), "UTF8/content-length or escapes changed");
            Check(frames[1].Command == "RECEIPT" && frames[1].Headers["receipt-id"] == "ready", "coalesced receipt was lost");
        }
        // A realistic fragmented large body must make bounded progress. This catches
        // the previous repeated scan with a deliberately generous regression watchdog.
        var body = Enumerable.Repeat((byte)'a', 262144).ToArray();
        foreach (var header in new[] { "MESSAGE\n\n", "MESSAGE\ncontent-length:262144\n\n" })
        {
            var decoder = new StompDecoder(); decoder.Feed(Encoding.UTF8.GetBytes(header));
            var clock = Stopwatch.StartNew();
            foreach (var value in body) Check(decoder.Feed([value]).Count == 0, "frame emitted before terminator");
            Check(decoder.Feed([0]).Single().Body.SequenceEqual(body), "fragmented body changed");
            Check(clock.Elapsed < TimeSpan.FromSeconds(10), "fragmented frame decoding repeatedly scans buffered bytes");
        }
        var many = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("\nRECEIPT\nreceipt-id:x\n\n\0", 10000)));
        Check(new StompDecoder().Feed(many).Count == 10000, "coalesced frames were truncated");
    }
    private static void Bounds()
    {
        Reject([.. Encoding.ASCII.GetBytes("MESSAGE\nx:"), 0xff, 10, 10, 0]);
        Reject(Encoding.ASCII.GetBytes("MESSAGE\nx:\\z\n\n\0"));
        Reject(Encoding.ASCII.GetBytes("MESSAGE\ncontent-length:-1\n\n"));
        Reject(Encoding.ASCII.GetBytes("MESSAGE\ncontent-length:1048577\n\n"));
        Reject(Encoding.ASCII.GetBytes("MESSAGE\ncontent-length:1\n\nxy\0"));
        Reject(Encoding.ASCII.GetBytes("MESSAGE\n" + string.Concat(Enumerable.Repeat("x:y\n", 65)) + "\n\0"));
        Reject(Encoding.ASCII.GetBytes("MESSAGE\nx:" + new string('x', 16384)));
        Reject([.. Encoding.ASCII.GetBytes("MESSAGE\n\n"), .. new byte[1048577].Select(_ => (byte)'a')]);
        var maximum = StompCodec.Encode("MESSAGE", new(), new byte[1048576]);
        Check(new StompDecoder().Feed(maximum).Single().Body.Length == 1048576, "maximum legal length rejected");
        var firstHeaderWins = new StompDecoder().Feed(Encoding.ASCII.GetBytes("MESSAGE\nack:first\nack:second\n\nx\0")).Single();
        Check(firstHeaderWins.Headers["ack"] == "first", "first header precedence changed");
    }
    private static async Task Handshakes()
    {
        await Task.WhenAll(Handshake(false), Handshake(true));
    }
    private static async Task Handshake(bool subscribed)
    {
        await using var fake = await FakeOda.Start();
        fake.HeartbeatBeforeConnected = !subscribed; fake.HeartbeatBeforeReceipt = subscribed;
        using var rescue = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var auth = new Credentials("test-client", "test-secret", "synthetic-refresh", "synthetic-access", DateTimeOffset.UtcNow.AddMinutes(5));
        bool ready = false, delivered = false;
        var operation = new StompConsumer(fake.Endpoints).Consume(auth, "rutony-oda-test", _ => delivered = true, () => ready = true, rescue.Token);
        try
        {
            await fake.HeartbeatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(18)));
            Check(operation.IsCompleted, (subscribed ? "SUBSCRIBE" : "CONNECT") + " deadline was extended by heartbeats");
            try { await operation; throw new Exception("incomplete handshake returned success"); } catch (OperationCanceledException) { }
            Check(!ready && !delivered, "incomplete handshake enabled delivery");
        }
        finally { rescue.Cancel(); try { await operation; } catch (OperationCanceledException) { } }
    }
    private static void Reject(byte[] wire) { try { new StompDecoder().Feed(wire); } catch (Exception e) when (e is InvalidDataException or DecoderFallbackException) { return; } throw new Exception("invalid STOMP accepted"); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
