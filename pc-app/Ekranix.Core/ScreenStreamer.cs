using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;

namespace Ekranix.Core;

public sealed class ScreenStreamer
{
    private const byte FlagConfig = 1;
    private const byte FlagKey = 2;

    private TcpClient? _tcp;
    private Process? _ffmpeg;
    private CancellationTokenSource? _cts;
    private readonly Queue<string> _stderr = new();
    private int _stopped;

    public string Encoder { get; private set; } = "";

    /// <summary>Вика се при неочакван край (ffmpeg гръмна, мрежата падна). От фонова нишка.</summary>
    public event Action<string>? Failed;

    public async Task StartAsync(TvDevice tv, int monitorIndex = 0, int fps = 60, int bitrateMbps = 20)
    {
        try
        {
            var ffmpeg = Ffmpeg.FindExecutable()
                ?? throw new InvalidOperationException("Не намирам ffmpeg.exe. Инсталирай го с: winget install Gyan.FFmpeg");

            Encoder = await Ffmpeg.DetectEncoderAsync(ffmpeg);

            _tcp = new TcpClient { NoDelay = true, SendBufferSize = 256 * 1024 };
            await ConnectWithRetryAsync(_tcp, tv);

            var psi = new ProcessStartInfo(ffmpeg, Ffmpeg.BuildArgs(Encoder, monitorIndex, fps, bitrateMbps))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg не стартира.");
            try { _ffmpeg.PriorityClass = ProcessPriorityClass.High; } catch { }

            _ffmpeg.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_stderr)
                {
                    _stderr.Enqueue(e.Data);
                    while (_stderr.Count > 15) _stderr.Dequeue();
                }
            };
            _ffmpeg.BeginErrorReadLine();

            _cts = new CancellationTokenSource();
            var flv = _ffmpeg.StandardOutput.BaseStream;
            var net = _tcp.GetStream();
            _ = Task.Run(() => PumpAsync(flv, net, _cts.Token));
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        Cleanup();
    }

    private static async Task ConnectWithRetryAsync(TcpClient tcp, TvDevice tv)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await tcp.ConnectAsync(tv.Address, Protocol.VideoPort, cts.Token);
                return;
            }
            catch (Exception) when (attempt < 9)
            {
                await Task.Delay(200);
            }
        }
    }

    private async Task PumpAsync(Stream flv, NetworkStream net, CancellationToken ct)
    {
        try
        {
            // FLV хедър (9) + PreviousTagSize0 (4)
            await flv.ReadExactlyAsync(new byte[13], ct);

            var tagHeader = new byte[11];
            var prevSize = new byte[4];

            while (!ct.IsCancellationRequested)
            {
                await flv.ReadExactlyAsync(tagHeader, ct);
                int type = tagHeader[0] & 0x1F;
                int size = (tagHeader[1] << 16) | (tagHeader[2] << 8) | tagHeader[3];

                var data = new byte[size];
                await flv.ReadExactlyAsync(data, ct);
                await flv.ReadExactlyAsync(prevSize, ct);

                // Само видео (9), само H.264 (codec id 7)
                if (type != 9 || size < 5 || (data[0] & 0x0F) != 7) continue;

                bool key = (data[0] >> 4) == 1;
                byte avcPacketType = data[1];

                if (avcPacketType == 0)
                {
                    var cfg = FlvAvc.ConfigToAnnexB(data.AsSpan(5));
                    var msg = new byte[5 + cfg.Length];
                    WriteHeader(msg, cfg.Length, FlagConfig);
                    cfg.CopyTo(msg, 5);
                    await net.WriteAsync(msg, ct);
                }
                else if (avcPacketType == 1)
                {
                    // Първите 5 байта на FLV видео таг-а не ни трябват:
                    // презаписваме ги с нашия хедър и пращаме масива директно
                    FlvAvc.AvccToAnnexBInPlace(data.AsSpan(5));
                    WriteHeader(data, size - 5, key ? FlagKey : (byte)0);
                    await net.WriteAsync(data, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException)
        {
            try { if (_ffmpeg?.WaitForExit(1000) == true) _ffmpeg.WaitForExit(); } catch { }
            Fail("ffmpeg спря: " + LastStderr());
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException)
        {
            Fail("Предаването прекъсна: " + ex.Message);
        }
    }

    private static void WriteHeader(byte[] buf, int payloadLength, byte flags)
    {
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(0, 4), payloadLength);
        buf[4] = flags;
    }

    private string LastStderr()
    {
        lock (_stderr) return _stderr.Count == 0 ? "(без съобщение)" : string.Join("\n", _stderr);
    }

    private void Fail(string reason)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        Cleanup();
        Failed?.Invoke(reason);
    }

    private void Cleanup()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_ffmpeg is { HasExited: false }) _ffmpeg.Kill(true); } catch { }
        try { _ffmpeg?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }
}