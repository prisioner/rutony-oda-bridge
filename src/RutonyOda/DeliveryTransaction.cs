using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RutonyOda;

// Returning successfully permits the transport to ACK. Any publish/commit fault
// leaves prepared state intact, so reconnect cannot silently repeat native work.
internal sealed class DeliveryTransaction(DeliveryLedger ledger, Action<Donation> publish)
{
    private volatile string? blockedMessage;
    public string? BlockedMessage => blockedMessage;
    public void Deliver(byte[] body, string[] sourceSystems)
    {
        var fingerprint = "invalid:" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        if (ledger.IsSkipped(fingerprint)) return;
        Donation? donation;
        try { donation = OdaEventParser.Parse(new UTF8Encoding(false, true).GetString(body), sourceSystems); }
        catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException or FormatException or InvalidOperationException or DecoderFallbackException or OverflowException)
        {
            blockedMessage = fingerprint;
            throw new OdaFault("Некорректное событие ODA. Передача остановлена; можно явно пропустить сообщение.");
        }
        if (donation != null && ledger.Prepare(donation)) { publish(donation); ledger.Commit(donation.Key); }
    }
    public void SkipBlocked()
    {
        if (blockedMessage == null) throw new InvalidOperationException("Нет проблемного сообщения.");
        ledger.Skip(blockedMessage); blockedMessage = null;
    }
}
