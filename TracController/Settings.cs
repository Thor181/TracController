using System.Text.Json;

namespace TracController;

internal sealed class Settings
{
    /// <summary>Явно заданный путь к trac.exe; null — искать автоматически.</summary>
    public string? TracPath { get; set; }

    /// <summary>Подключаться через "trac connectgui" (окно ввода пароля/MFA), а не "trac connect".</summary>
    public bool UseConnectGui { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TracController", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // битый файл настроек — начинаем с чистых
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
