using System.Diagnostics;

namespace Ekranix.Core;

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
            if (await TryEncoderAsync(ffmpeg, enc)) return _cachedEncoder = enc;
        }
        return _cachedEncoder = "libx264";
    }

    private static async Task<bool> TryEncoderAsync(string ffmpeg, string encoder)
    {
        var psi = new ProcessStartInfo(ffmpeg,
            $"-hide_banner -loglevel error -f lavfi -i color=black:s=1280x720:d=0.1 -c:v {encoder} -f null -")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var p = Process.Start(psi);
        if (p is null) return false;
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return false;
        }
        return p.ExitCode == 0;
    }

    public static string BuildArgs(string encoder, int monitorIndex, int fps, int bitrateMbps)
    {
        var grab = $"ddagrab=output_idx={monitorIndex}:framerate={fps}:draw_mouse=1";
        var b = bitrateMbps;
        // Буфер на rate control-а ≈ 1 кадър: енкодерът не "трупа" кадри
        var bufKbit = b * 1000 / fps * 2;

        var (filter, codecArgs) = encoder switch
        {
            "h264_nvenc" => (grab,
                $"-c:v h264_nvenc -preset p1 -tune ull -zerolatency 1 -delay 0 -rc cbr " +
                $"-b:v {b}M -maxrate {b}M -bufsize {bufKbit}k -bf 0 -g 600 -forced-idr 1 -profile:v high"),

            "h264_amf" => (grab,
                $"-c:v h264_amf -usage ultralowlatency -quality speed -rc cbr " +
                $"-b:v {b}M -maxrate {b}M -bufsize {bufKbit}k -bf 0 -g 600"),

            "h264_qsv" => (grab + ",hwmap=derive_device=qsv,format=qsv",
                $"-c:v h264_qsv -preset veryfast -low_delay_brc 1 -async_depth 1 " +
                $"-b:v {b}M -maxrate {b}M -bf 0 -g 600"),

            _ => (grab + ",hwdownload,format=bgra",
                $"-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p " +
                $"-b:v {b}M -maxrate {b}M -bufsize {bufKbit}k -g 600"),
        };

        return $"-hide_banner -loglevel error -fflags nobuffer " +
               $"-filter_complex \"{filter}\" {codecArgs} -an " +
               $"-flush_packets 1 -flvflags no_duration_filesize -f flv pipe:1";
    }
}