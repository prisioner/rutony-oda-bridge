using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RutonyOda;

public sealed record Donation(string Key, string Name, decimal Amount, string Currency, string Message, DateTimeOffset Timestamp);

public static partial class OdaEventParser
{
    [GeneratedRegex("^[A-Za-z0-9_.:@-]{1,180}$")]
    private static partial Regex Identifier();
    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();

    public static Donation? Parse(string json, IReadOnlyCollection<string>? sourceSystems = null)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 20 });
        var p = doc.RootElement;
        if (p.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Ожидался объект события.");
        var system = Text(p, "system");
        if (Text(p, "type") != "payment" || !DonationSources.Supported.Contains(system, StringComparer.Ordinal) || !(sourceSystems ?? ["ODA"]).Contains(system, StringComparer.Ordinal)) return null;
        var id = Text(p, "originId"); if (string.IsNullOrWhiteSpace(id)) id = Text(p, "id");
        var recipient = Text(p, "recipientId");
        if (!Identifier().IsMatch(id) || !Identifier().IsMatch(recipient)) throw new InvalidDataException("Некорректный ID события.");
        var amount = p.GetProperty("amount");
        var major = amount.GetProperty("major").GetInt32(); var minor = amount.GetProperty("minor").GetInt32();
        if (major < 0 || minor is < 0 or > 99) throw new InvalidDataException("Некорректная сумма.");
        decimal value = major + minor / 100m;
        if (value <= 0 || decimal.Round((decimal)(double)(float)value, 2, MidpointRounding.AwayFromZero) != value)
            throw new InvalidDataException("Сумма не представима в RutonyChat с точностью до копейки.");
        var currency = Text(amount, "currency").ToUpperInvariant();
        if (!CurrencyCode().IsMatch(currency)) throw new InvalidDataException("Некорректная валюта.");
        var name = Text(p, "nickname"); if (string.IsNullOrWhiteSpace(name)) name = "Аноним";
        var message = Text(p, "message");
        if (name.Length > 200 || name.Any(char.IsControl) || message.Length > 10000 || message.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
            throw new InvalidDataException("Некорректный текст события.");
        if (!DateTimeOffset.TryParse(Text(p, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            throw new InvalidDataException("Некорректное время события.");
        var key = system == "ODA" ? recipient + "/" + id : "imported:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { system, recipient, id }))).ToLowerInvariant();
        return new Donation(key, name, value, currency, message, timestamp);
    }
    private static string Text(JsonElement p, string key) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
