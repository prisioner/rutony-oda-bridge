using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RutonyOda;

public sealed record BridgeStatus(bool Configured, bool Authorized, bool Enabled, bool Connected, string Phase, string? Error, int Delivered, string[] Uncertain, string ClientId, string? BlockedMessage, string QueueId)
{
    public string HostMode { get; init; } = "rutony";
    public bool StorageFault { get; init; }
    public string[] SourceSystems { get; init; } = [];
    public IReadOnlyList<string> AvailableSources { get; init; } = DonationSources.Supported;
}
public sealed class BridgeService : IDisposable
{
    private sealed record Settings(bool Enabled, string InstallationId, string[]? SourceSystems = null);
    private readonly SemaphoreSlim controls = new(1);
    private readonly OAuthCoordinator oauth;
    private readonly StompConsumer consumer;
    private readonly DeliveryTransaction delivery;
    private readonly DeliveryLedger ledger;
    private readonly OdaApi api;
    private readonly string settingsPath;
    private readonly string installationId;
    private volatile string[] sourceSystems;
    private volatile bool enabled, connected;
    private volatile string phase = "Пауза";
    private volatile string? error;
    private CancellationTokenSource? workerStop;
    private Task? worker;
    private bool disposed;
    private volatile bool stopping;
    private readonly object lifecycle = new();
    public bool IsPreview { get; }
    public BridgeService(string directory, Action<Donation> publish, OdaEndpoints? endpoints = null, bool preview = false) : this(directory, publish, endpoints, preview, null) { }
    internal BridgeService(string directory, Action<Donation> publish, OdaEndpoints? endpoints, bool preview, Action<FileStream, byte[]>? append)
    {
        endpoints ??= OdaEndpoints.Production;
        IsPreview = preview;
        Directory.CreateDirectory(directory); settingsPath = Path.Combine(directory, "settings.json");
        var settings = File.Exists(settingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath)) ?? throw new InvalidDataException("Повреждены настройки.") : new Settings(false, Guid.NewGuid().ToString("N"));
        if (!Regex.IsMatch(settings.InstallationId, "^[a-f0-9]{32}$")) throw new InvalidDataException("Повреждён ID установки.");
        installationId = settings.InstallationId;
        sourceSystems = DonationSources.Validate(settings.SourceSystems ?? ["ODA"]);
        api = new OdaApi(endpoints);
        try { oauth = new OAuthCoordinator(directory, api, endpoints); ledger = new DeliveryLedger(directory, append); }
        catch { api.Dispose(); throw; }
        consumer = new StompConsumer(endpoints); delivery = new DeliveryTransaction(ledger, publish);
        enabled = settings.Enabled;
    }
    public void Resume() { controls.Wait(); try { ThrowIfStopping(); if (enabled && worker == null) StartWorker(); } finally { controls.Release(); } }
    private void SaveSettings(bool value, string[]? systems = null)
    {
        var temp = settingsPath + ".tmp"; using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { f.Write(JsonSerializer.SerializeToUtf8Bytes(new Settings(value, installationId, systems ?? sourceSystems))); f.Flush(true); }
        File.Move(temp, settingsPath, true);
    }
    public BridgeStatus Status() => new(oauth.Current != null, oauth.Current?.RefreshToken != null, enabled, connected, phase, error, ledger.Delivered, ledger.Uncertain, oauth.Current?.ClientId ?? "", delivery.BlockedMessage, QueueName()) { HostMode = IsPreview ? "standalone" : "rutony", StorageFault = ledger.IsFaulted, SourceSystems = sourceSystems.ToArray() };
    public async Task SetSources(string[] systems)
    {
        var selected = DonationSources.Validate(systems);
        await controls.WaitAsync(); try
        {
            ThrowIfStopping();
            await StopWorker(); enabled = false; SaveSettings(false, selected); sourceSystems = selected; error = null; phase = "Источники сохранены. Передача на паузе";
        }
        finally { controls.Release(); }
    }
    private string QueueName() => "rutony-oda-" + installationId + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oauth.Current?.ClientId ?? "")))[..16].ToLowerInvariant();
    public async Task Configure(string clientId, string clientSecret)
    {
        OAuthCoordinator.ValidateConfiguration(clientId, clientSecret);
        await controls.WaitAsync(); try
        {
            ThrowIfStopping();
            await StopWorker(); SaveSettings(false); enabled = false;
            oauth.Configure(clientId, clientSecret); error = null; phase = "Требуется вход ODA";
        }
        finally { controls.Release(); }
    }
    public string BeginAuthorization(string redirect)
    {
        controls.Wait(); try
        {
            ThrowIfStopping();
            return oauth.Begin(redirect);
        }
        finally { controls.Release(); }
    }
    public async Task FinishAuthorization(string state, string code, string? providerError = null)
    {
        await controls.WaitAsync();
        try
        {
            ThrowIfStopping();
            var auth = oauth.TakeAttempt(state, code, providerError);
            await StopWorker(); SaveSettings(false); enabled = false;
            await oauth.Complete(auth, code);
            if (!stopping) { error = null; phase = "Вход выполнен. Передача выключена"; }
        }
        catch (Exception e) when (!stopping)
        {
            error = e switch
            {
                OdaFault fault => PublicFault(fault),
                HttpRequestException => "Не удалось обратиться к ODA. Проверьте интернет и доступность сервиса.",
                OperationCanceledException => "Время ожидания операции истекло. Повторите вход.",
                CryptographicException => "Windows не смогла защитить или сохранить реквизиты входа.",
                IOException or UnauthorizedAccessException => "Ошибка доступа к данным входа. Проверьте папку данных и диск.",
                JsonException or KeyNotFoundException or InvalidOperationException or FormatException => "ODA вернула неожиданный формат ответа.",
                _ => "Не удалось завершить вход. Повторите попытку."
            };
            if (!enabled) phase = "Попытка входа не завершена";
            throw;
        }
        finally { controls.Release(); }
    }
    public async Task SetEnabled(bool value)
    {
        await controls.WaitAsync(); try
        {
            ThrowIfStopping();
            await StopWorker();
            if (value) ledger.EnsureHealthy();
            if (value && oauth.Current?.RefreshToken == null) throw new InvalidOperationException("Сначала войдите в ODA.");
            if (value && ledger.Uncertain.Length > 0) throw new InvalidOperationException("Сначала решите неопределённые доставки.");
            SaveSettings(value); enabled = value; error = null; phase = value ? "Подключение" : "Пауза"; if (value) StartWorker();
        }
        finally { controls.Release(); }
    }
    public async Task Resolve(string key, bool delivered)
    {
        await controls.WaitAsync(); try { ThrowIfStopping(); await StopWorker(); SaveSettings(false); enabled = false; ledger.Resolve(key, delivered); error = null; phase = "Решение сохранено. Можно включить передачу"; } finally { controls.Release(); }
    }
    public async Task SkipBlocked()
    {
        await controls.WaitAsync(); try { ThrowIfStopping(); await StopWorker(); SaveSettings(false); enabled = false; delivery.SkipBlocked(); error = null; } finally { controls.Release(); }
    }
    private void StartWorker() { workerStop = new CancellationTokenSource(); worker = Task.Run(() => Run(workerStop.Token)); }
    private async Task StopWorker() { if (workerStop == null) return; workerStop.Cancel(); if (worker != null) await worker; workerStop.Dispose(); workerStop = null; worker = null; connected = false; }
    private async Task Run(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (ledger.Uncertain.Length > 0) throw new OdaFault("Есть неопределённая доставка. Требуется решение оператора.");
                var auth = oauth.Current ?? throw new OdaFault("Сначала настройте приложение.");
                if (auth.Expires <= DateTimeOffset.UtcNow.AddSeconds(60)) { phase = "Обновление входа"; auth = await oauth.Refresh(auth, ct); }
                phase = "Проверка подписки ODA"; await api.EnsureSubscription(auth, ct);
                phase = "Подключение STOMP"; await consumer.Consume(auth, QueueName(), body => delivery.Deliver(body, sourceSystems), () => { connected = true; error = null; phase = "Передача включена"; }, ct); attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OdaFault e) when (!e.Retryable) { PauseForFault(PublicFault(e)); break; }
            catch (Exception e)
            {
                if (ledger.Uncertain.Length > 0) { PauseForFault("Native-доставка могла сработать. Повтор запрещён до решения оператора."); break; }
                if (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException) { PauseForFault("Повреждены данные, ответ сервиса или журнал. Передача остановлена; файлы сохранены."); break; }
                connected = false; error = e is OdaFault fault ? PublicFault(fault) : "Связь с ODA прервалась. Переподключение; токены и ответы скрыты."; phase = "Ожидание переподключения";
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt++, 5)))), ct); } catch (OperationCanceledException) { break; }
            }
            finally { connected = false; }
        }
    }
    private static string PublicFault(OdaFault fault) =>
        !fault.Message.StartsWith("Авторизация ODA", StringComparison.Ordinal) ? fault.Message
        : fault.Retryable ? "Сервис ODA временно недоступен. Повторите вход позже."
        : "ODA не подтвердила данные приложения. Проверьте Client ID и актуальный Client Secret, затем начните вход заново.";

    private void PauseForFault(string message) { enabled = false; error = message; phase = "Требуется действие"; try { SaveSettings(false); } catch { error = "Ошибка сохранения настроек. Передача остановлена, проверьте диск."; } }
    private void ThrowIfStopping() => ObjectDisposedException.ThrowIf(stopping, this);
    internal void RequestStop()
    {
        lock (lifecycle) { if (stopping) return; stopping = true; oauth.Cancel(); }
    }
    public void Dispose()
    {
        RequestStop();
        controls.Wait(); try { if (disposed) return; disposed = true; StopWorker().GetAwaiter().GetResult(); api.Dispose(); ledger.Dispose(); oauth.Dispose(); } finally { controls.Release(); }
    }
}
