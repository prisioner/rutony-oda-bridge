using System.Net.WebSockets;

namespace RutonyOda;

// Owns protocol negotiation, socket lifetime, heartbeat and ACK after delivery returns.
internal sealed class StompConsumer(OdaEndpoints endpoints)
{
    public async Task Consume(Credentials auth, string queueName, Action<byte[]> deliver, Action ready, CancellationToken ct)
    {
        using var ws = new ClientWebSocket(); ws.Options.AddSubProtocol("v12.stomp");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(auth.Expires - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(45)); var token = lifetime.Token;
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token)) { connectTimeout.CancelAfter(TimeSpan.FromSeconds(25)); await ws.ConnectAsync(endpoints.Socket, connectTimeout.Token); }
        // A phase deadline is absolute: heartbeat or partial frames cannot renew it.
        using var phaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        phaseDeadline.CancelAfter(TimeSpan.FromSeconds(15));
        var transportToken = phaseDeadline.Token;
        using var sendGate = new SemaphoreSlim(1);
        async Task Send(byte[] bytes) { await sendGate.WaitAsync(transportToken); try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, transportToken); } finally { sendGate.Release(); } }
        // RabbitMQ's host header selects a virtual host, not the WebSocket DNS host.
        // ODA's own client omits it and uses the broker's configured default.
        await Send(StompCodec.Encode("CONNECT", new() { { "accept-version", "1.2" }, { "passcode", auth.AccessToken! }, { "heart-beat", "10000,10000" } }));
        var decoder = new StompDecoder(); var bytes = new byte[16384]; bool handshake = false; int watchdog = 15000;
        Task? heartbeat = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(transportToken); if (watchdog > 0) receiveTimeout.CancelAfter(watchdog);
                var packet = await ws.ReceiveAsync(bytes, receiveTimeout.Token); if (packet.MessageType == WebSocketMessageType.Close) throw new WebSocketException("ODA closed socket");
                if (packet.MessageType != WebSocketMessageType.Text) throw new OdaFault("ODA передала неожиданный бинарный STOMP пакет.");
                transportToken.ThrowIfCancellationRequested();
                foreach (var frame in decoder.Feed(bytes[..packet.Count]))
                {
                    if (frame.Command == "ERROR") throw new OdaFault("STOMP ODA отклонил подключение или подписку. Проверьте scope stomp и права приложения.");
                    if (frame.Command == "CONNECTED" && !handshake)
                    {
                        if (frame.Headers.GetValueOrDefault("version") != "1.2") throw new OdaFault("ODA не согласовала STOMP 1.2.");
                        var values = (frame.Headers.GetValueOrDefault("heart-beat") ?? "0,0").Split(',');
                        if (values.Length != 2 || !int.TryParse(values[0], out int outgoing) || !int.TryParse(values[1], out int incoming) || outgoing < 0 || incoming < 0 || outgoing > 120000 || incoming > 120000) throw new OdaFault("Некорректный heartbeat STOMP.");
                        watchdog = outgoing == 0 ? 0 : Math.Max(10000, outgoing) * 3;
                        if (incoming > 0) heartbeat = Task.Run(async () => { while (!token.IsCancellationRequested) { await Task.Delay(Math.Max(10000, incoming), token); await Send([10]); } }, token);
                        handshake = true;
                        phaseDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                        await Send(StompCodec.Encode("SUBSCRIBE", new() { { "id", "donations" }, { "destination", "/exchange/subscriptions/" + auth.ClientId }, { "ack", "client-individual" }, { "prefetch-count", "1" }, { "durable", "true" }, { "exclusive", "false" }, { "auto-delete", "false" }, { "x-queue-name", queueName }, { "x-message-ttl", "86400000" }, { "x-expires", "86400000" }, { "receipt", "ready" } }));
                    }
                    else if (frame.Command == "RECEIPT" && handshake && frame.Headers.GetValueOrDefault("receipt-id") == "ready") { phaseDeadline.CancelAfter(Timeout.InfiniteTimeSpan); ready(); }
                    else if (frame.Command == "MESSAGE")
                    {
                        if (!handshake || frame.Headers.GetValueOrDefault("subscription") != "donations" || !frame.Headers.TryGetValue("ack", out var ack) || ack.Length is < 1 or > 1000) throw new OdaFault("Некорректный STOMP MESSAGE/ack.");
                        // Some brokers deliver immediately before the SUBSCRIBE receipt.
                        phaseDeadline.CancelAfter(Timeout.InfiniteTimeSpan); ready();
                        deliver(frame.Body);
                        await Send(StompCodec.Encode("ACK", new() { { "id", ack } }));
                    }
                }
                if (heartbeat?.IsFaulted == true) await heartbeat;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && auth.Expires <= DateTimeOffset.UtcNow.AddSeconds(60)) { /* Refresh at the next iteration. */ }
        finally { lifetime.Cancel(); ws.Abort(); if (heartbeat != null) { try { await heartbeat; } catch (OperationCanceledException) { } catch (WebSocketException) { } } }
    }
}
