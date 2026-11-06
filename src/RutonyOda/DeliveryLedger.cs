using System.Text;
using System.Text.Json;

namespace RutonyOda;

public sealed class DeliveryLedger : IDisposable
{
    private sealed record Entry(string Key, string Status, DateTimeOffset At);
    private readonly Dictionary<string, string> states = new(StringComparer.Ordinal);
    private readonly FileStream stream;
    private readonly object gate = new();
    private readonly Action<FileStream, byte[]> append;
    private bool faulted;
    public bool IsFaulted { get { lock (gate) return faulted; } }
    internal void EnsureHealthy() { lock (gate) if (faulted) throw new IOException("Ошибка записи журнала: продолжение запрещено до переоткрытия и проверки файлов."); }
    public DeliveryLedger(string directory) : this(directory, null) { }
    internal DeliveryLedger(string directory, Action<FileStream, byte[]>? append)
    {
        this.append = append ?? ((file, bytes) => { file.Write(bytes); file.Flush(true); });
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deliveries.jsonl");
        var manifest = Path.Combine(directory, "ledger.version");
        if (!File.Exists(manifest))
        {
            if (File.Exists(path)) throw new InvalidDataException("Нет версии журнала. Данные сохранены; требуется восстановление.");
            File.WriteAllText(manifest, "1");
        }
        if (File.ReadAllText(manifest) != "1") throw new InvalidDataException("Неизвестная версия журнала.");
        // A persistent empty journal is part of initial creation. Missing later is not a new session.
        var initialized = Path.Combine(directory, "ledger.initialized");
        if (File.Exists(initialized) && !File.Exists(path)) throw new InvalidDataException("Журнал исчез; передача заблокирована.");
        stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (stream.Length > 0) { stream.Seek(-1, SeekOrigin.End); if (stream.ReadByte() != 10) throw new InvalidDataException("Незавершённая запись журнала; требуется восстановление."); stream.Seek(0, SeekOrigin.Begin); }
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > 2048) throw new InvalidDataException("Повреждён журнал.");
                    var entry = JsonSerializer.Deserialize<Entry>(line) ?? throw new InvalidDataException("Повреждён журнал.");
                    ValidateTransition(entry);
                    states[entry.Key] = entry.Status;
                }
            }
            stream.Seek(0, SeekOrigin.End);
            File.WriteAllText(initialized, "1");
        }
        catch { stream.Dispose(); throw; }
    }
    public string[] Uncertain { get { lock (gate) return states.Where(p => p.Value == "prepared").Select(p => p.Key).ToArray(); } }
    public int Delivered { get { lock (gate) return states.Count(p => p.Value == "committed"); } }
    public bool IsSkipped(string key) { lock (gate) return states.GetValueOrDefault(key) == "skipped" || (key.StartsWith("invalid:",StringComparison.Ordinal) && states.GetValueOrDefault("invalid/"+key[8..])=="skipped"); }
    public void Skip(string key) { lock (gate) { EnsureHealthy(); if (!IsSkipped(key)) Append(new Entry(key, "skipped", DateTimeOffset.UtcNow)); } }
    public bool Prepare(Donation e)
    {
        lock (gate)
        {
            EnsureHealthy();
            if (states.GetValueOrDefault(e.Key) == "committed") return false;
            if (states.GetValueOrDefault(e.Key) == "prepared") throw new InvalidOperationException("Неопределённая доставка требует решения.");
            Append(new Entry(e.Key, "prepared", DateTimeOffset.UtcNow)); return true;
        }
    }
    public void Commit(string key) { lock (gate) Append(new Entry(key, "committed", DateTimeOffset.UtcNow)); }
    public void Resolve(string key, bool delivered)
    {
        lock (gate)
        {
            EnsureHealthy();
            if (states.GetValueOrDefault(key) != "prepared") throw new InvalidOperationException("ID не ожидает решения.");
            Append(new Entry(key, delivered ? "committed" : "retry", DateTimeOffset.UtcNow));
        }
    }
    private void ValidateTransition(Entry e)
    {
        if (string.IsNullOrWhiteSpace(e.Key) || e.Key.Length > 361) throw new InvalidDataException("Некорректный ID журнала.");
        var prev = states.GetValueOrDefault(e.Key);
        if (!((e.Status == "prepared" && (prev == null || prev == "retry")) || ((e.Status == "committed" || e.Status == "retry") && prev == "prepared") || (e.Status == "skipped" && prev == null && (e.Key.StartsWith("invalid/", StringComparison.Ordinal) || e.Key.StartsWith("invalid:",StringComparison.Ordinal)))))
            throw new InvalidDataException("Нарушена последовательность журнала.");
    }
    private void Append(Entry e)
    {
        EnsureHealthy();
        ValidateTransition(e);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e) + "\n");
        try { append(stream, bytes); } catch { faulted = true; throw; }
        states[e.Key] = e.Status;
    }
    public void Dispose() { lock (gate) stream.Dispose(); }
}
