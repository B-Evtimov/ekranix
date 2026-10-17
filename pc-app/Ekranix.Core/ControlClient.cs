using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Ekranix.Core;

public record PairResult(bool Success, ControlClient? Client, string? Error)
{
    public static PairResult Ok(ControlClient c) => new(true, c, null);
    public static PairResult Fail(string error) => new(false, null, error);
}

public sealed class ControlClient
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(3);

    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private long _lastSeen = Environment.TickCount64;
    private int _closed;

    public TvDevice Tv { get; }

    /// <summary>Вика се веднъж, с причината. Идва от фонова нишка.</summary>
    public event Action<string>? Disconnected;

    private ControlClient(TcpClient tcp, TvDevice tv)
    {
        _tcp = tcp;
        Tv = tv;
        var stream = tcp.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
    }

    public static async Task<PairResult> PairAsync(TvDevice tv, string code, string pcName, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var c = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                c.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(tv.Address, tv.ControlPort, c.Token).ConfigureAwait(false);
            }

            var client = new ControlClient(tcp, tv);
            await client.SendAsync(new { type = "pair", code, pcName }).ConfigureAwait(false);

            string? line;
            using (var c = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                c.CancelAfter(TimeSpan.FromSeconds(5));
                line = await client._reader.ReadLineAsync(c.Token).ConfigureAwait(false);
            }

            if (line is null)
            {
                tcp.Dispose();
                return PairResult.Fail("Телевизорът затвори връзката.");
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "pair_ok":
                    return PairResult.Ok(client);

                case "pair_fail":
                    tcp.Dispose();
                    var left = root.TryGetProperty("attemptsLeft", out var a) ? a.GetInt32() : 0;
                    return PairResult.Fail(left > 0
                        ? $"Грешен код. Остават {left} опита."
                        : "Твърде много грешни опити. Телевизорът показва нов код.");

                case "busy":
                    tcp.Dispose();
                    return PairResult.Fail("Телевизорът вече е свързан с друг компютър.");

                default:
                    tcp.Dispose();
                    return PairResult.Fail("Неочакван отговор от телевизора.");
            }
        }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            return PairResult.Fail("Телевизорът не отговаря.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            tcp.Dispose();
            return PairResult.Fail($"Няма връзка с телевизора: {ex.Message}");
        }
    }

    /// <summary>Пуска четенето и heartbeat-а. Абонирай се за Disconnected ПРЕДИ това.</summary>
    public void Start()
    {
        Interlocked.Exchange(ref _lastSeen, Environment.TickCount64);
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(PingLoopAsync);
    }

    public async Task StopAsync()
    {
        try { await SendAsync(new { type = "stop" }).ConfigureAwait(false); }
        catch { /* вече е мъртва: няма значение */ }
        Close("Спряно от компютъра.");
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null) { Close("Телевизорът прекъсна връзката."); return; }

                Interlocked.Exchange(ref _lastSeen, Environment.TickCount64);

                string? type;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    type = doc.RootElement.GetProperty("type").GetString();
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    continue;
                }

                if (type == "stop") { Close("Телевизорът спря връзката."); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { Close("Връзката с телевизора прекъсна."); }
    }

    private async Task PingLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, _cts.Token).ConfigureAwait(false);

                var silentMs = Environment.TickCount64 - Interlocked.Read(ref _lastSeen);
                if (silentMs > PeerTimeout.TotalMilliseconds) { Close("Телевизорът не отговаря."); return; }

                await SendAsync(new { type = "ping" }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close("Връзката с телевизора прекъсна.");
        }
    }

    private async Task SendAsync(object msg)
    {
        var json = JsonSerializer.Serialize(msg);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await _writer.WriteLineAsync(json).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return; // само веднъж
        _cts.Cancel();
        _tcp.Dispose();
        Disconnected?.Invoke(reason);
    }
}