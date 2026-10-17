using System.IO;
using System.Text.Json;

namespace Ekranix.App;

/// <summary>Запомня последния избор в %AppData%\Ekranix\settings.json</summary>
public sealed class AppSettings
{
    public int QualityIndex { get; set; } = 1;
    public int MonitorIndex { get; set; }
    public bool ShowCursor { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ekranix", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}