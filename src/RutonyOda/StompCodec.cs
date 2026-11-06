using System.Globalization;
using System.Text;

namespace RutonyOda;

public sealed record StompFrame(string Command, Dictionary<string, string> Headers, byte[] Body);
public static class StompCodec
{
    public static byte[] Encode(string command, Dictionary<string, string> headers, byte[]? body = null)
    {
        body ??= [];
        string Escape(string value) => command is "CONNECT" or "CONNECTED" ? value : value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace(":", "\\c");
        var head = command + "\n" + string.Concat(headers.Where(p => p.Key != "content-length").Select(p => Escape(p.Key) + ":" + Escape(p.Value) + "\n")) + "content-length:" + body.Length.ToString(CultureInfo.InvariantCulture) + "\n\n";
        return [.. Encoding.UTF8.GetBytes(head), .. body, 0];
    }
}
public sealed class StompDecoder
{
    private enum Phase { Command, Headers, Body, Terminator }
    private const int MaxHeaders = 16384, MaxBody = 1048576;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly MemoryStream line = new(), body = new();
    private Phase phase;
    private string command = "";
    private Dictionary<string, string> headers = new(StringComparer.Ordinal);
    private int headerBytes, headerCount, bodyLength = -1;

    // Each input byte is consumed once. Only the current line/body is retained;
    // incomplete feeds never reparse headers or rescan previously consumed body.
    public IReadOnlyList<StompFrame> Feed(byte[] bytes)
    {
        var result = new List<StompFrame>();
        foreach (byte value in bytes)
        {
            if (phase is Phase.Command or Phase.Headers)
            {
                if (phase == Phase.Command && line.Length == 0 && value is 10 or 13) continue;
                if (++headerBytes > MaxHeaders) throw new InvalidDataException("Слишком большой заголовок STOMP.");
                if (value == 0) throw new InvalidDataException("Некорректный заголовок STOMP.");
                if (value != 10) { line.WriteByte(value); continue; }
                var text = Utf8.GetString(line.GetBuffer(), 0, (int)line.Length);
                line.SetLength(0);
                if (text.EndsWith('\r')) text = text[..^1];
                if (text.Contains('\r')) throw new InvalidDataException("Некорректный заголовок STOMP.");
                if (phase == Phase.Command) { command = text; phase = Phase.Headers; continue; }
                if (text.Length == 0)
                {
                    bodyLength = -1;
                    if (headers.TryGetValue("content-length", out var length) &&
                        (!int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out bodyLength) || bodyLength > MaxBody))
                        throw new InvalidDataException("Недопустимый размер STOMP.");
                    phase = bodyLength == 0 ? Phase.Terminator : Phase.Body;
                    continue;
                }
                if (++headerCount > 64) throw new InvalidDataException("Слишком много заголовков STOMP.");
                int colon = text.IndexOf(':');
                if (colon <= 0) throw new InvalidDataException("Некорректный заголовок STOMP.");
                headers.TryAdd(Unescape(text[..colon]), Unescape(text[(colon + 1)..]));
                continue;
            }
            if (phase == Phase.Terminator)
            {
                if (value != 0) throw new InvalidDataException("Нет терминатора STOMP.");
                Finish(result); continue;
            }
            if (bodyLength < 0 && value == 0) { Finish(result); continue; }
            if (body.Length >= MaxBody) throw new InvalidDataException("Слишком большой STOMP body.");
            body.WriteByte(value);
            if (body.Length == bodyLength) phase = Phase.Terminator;
        }
        return result;
    }
    private string Unescape(string text)
    {
        if (command is "CONNECT" or "CONNECTED") return text;
        var value = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { value.Append(text[i]); continue; }
            if (++i >= text.Length) throw new InvalidDataException("Некорректный escape STOMP.");
            value.Append(text[i] switch { 'n' => '\n', 'r' => '\r', 'c' => ':', '\\' => '\\', _ => throw new InvalidDataException("Некорректный escape STOMP.") });
        }
        return value.ToString();
    }
    private void Finish(List<StompFrame> result)
    {
        result.Add(new StompFrame(command, headers, body.ToArray()));
        body.SetLength(0); headers = new(StringComparer.Ordinal);
        command = ""; headerBytes = headerCount = 0; bodyLength = -1; phase = Phase.Command;
    }
}
