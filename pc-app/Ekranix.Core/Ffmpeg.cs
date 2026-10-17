using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Ekranix.Core;

public record MonitorInfo(int Index, int Width, int Height)
{
    public string Title => $"Монитор {Index + 1}";
    public string Resolution => $"{Width} × {Height}";
    public bool NeedsScaling => Width > 1920 || Height > 1080;
}

public record StreamOptions(int MonitorIndex, int Fps, int BitrateMbps, bool ShowCursor, bool ScaleTo1080);

public static class Ffmpeg
{
    private static string? _cachedEncoder;

    /// <summary>Търси ffmpeg.exe до програмата (папка ffmpeg\) или в PATH.</summary>
    public static string? FindExecutable()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(local)) return local;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Избира най-бързия наличен хардуерен енкодер: NVIDIA → AMD → Intel → процесор.</summary>
    public static async Task<string> DetectEncoderAsync(string ffmpeg)
    {
        if (_cachedEncoder != null) return _cachedEncoder;
        foreach (var enc in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
        {
            var (ok, _) = await RunAsync(ffmpeg,
                $"-hide_banner -loglevel error -f lavfi -i color=black:s=1280x720:d=0.1 -c:v {enc} -f null -");
            if (ok) return _cachedEncoder = enc;
        }
        return _cachedEncoder = "libx264";
    }

    /// <summary>
    /// Пита самия ddagrab кои монитори вижда. Така номерацията винаги съвпада
    /// с тази, която ползваме после при предаването.
    /// </summary>
    public static async Task<List<MonitorInfo>> ListMonitorsAsync(string ffmpeg)
    {
        var list = new List<MonitorInfo>();
        for (int i = 0; i < 8; i++)
        {
            var (ok, stderr) = await RunAsync(ffmpeg,
                $"-hide_banner -f lavfi -i ddagrab=output_idx={i}:framerate=1 -frames:v 1 -f null -");
            if (!ok) break;

            var m = Regex.Match(stderr, @"Video:.*?(\d{3,5})x(\d{3,5})");
            if (!m.Success) break;

            list.Add(new MonitorInfo(i, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        }
        return list;
    }

    public static string BuildArgs(string encoder, StreamOptions o)
    {
        var grab = $"ddagrab=output_idx={o.MonitorIndex}:framerate={o.Fps}:draw_mouse={(o.ShowCursor ? 1 : 0)}";
        var b = o.BitrateMbps;
        // Буфер на rate control-а ≈ 2 кадъра: енкодерът не "трупа" кадри
        var bufKbit = b * 1000 / o.Fps * 2;

        string filter;
        if (o.ScaleTo1080)
        {
            // Мониторът е над 1080p: смаляваме, за да е по силите на декодера на всеки TV
            filter = grab + ",hwdownload,format=bgra," +
                     "scale=w=1920:h=1080:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=fast_bilinear";
        }
        else
        {
            filter = encoder switch
            {
                "h264_qsv" => grab + ",hwmap=derive_device=qsv,format=qsv",
                "libx264" => grab + ",hwdownload,format=bgra",
                _ => grab, // NVENC и AMF взимат кадъра направо от GPU паметта
            };
        }

        var codecArgs = encoder switch
        {
            "h264_nvenc" =>
                $"-c:v h264_nvenc -preset p1 -tune ull -zerolatency 1 -delay 0 -rc cbr " +
                $"-b:v {b}M -maxrate {b}M -bufsize {bufKbit}k -bf 0 -g 600 -forced-idr 1 -profile:v high",

            "h264_amf" =>
                $"-c:v h264_amf -usage ultralowlatency -quality speed -rc cbr " +
                $"-b:v {b}M -maxrate {b}M -bufsize {bufKbit}k -bf 0 -g 600",

            "h264_qsv" =>
                $"-c:v h264_qsv -preset veryfast -low_delay_brc 1 -async_depth 1 " +
                $"-b:v {b}M -maxrate {b}M -bf 0 -g 600",

            _ =>
                $"-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p " +
                $"-b:v {b}M -maxrate {b}M -bufsize {bufKbit}k -g 600",
        };

        return $"-hide_banner -loglevel error -fflags nobuffer " +
               $"-filter_complex \"{filter}\" {codecArgs} -an " +
               $"-flush_packets 1 -flvflags no_duration_filesize -f flv pipe:1";
    }

    public static string FriendlyEncoderName(string encoder) => encoder switch
    {
        "h264_nvenc" => "NVIDIA NVENC",
        "h264_amf" => "AMD AMF",
        "h264_qsv" => "Intel Quick Sync",
        _ => "Процесор (x264)",
    };

    private static async Task<(bool Ok, string Stderr)> RunAsync(string ffmpeg, string args)
    {
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var p = Process.Start(psi);
        if (p is null) return (false, "");
        _ = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (false, "");
        }
        return (p.ExitCode == 0, await stderrTask);
    }
}