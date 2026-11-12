using System.IO;
using System.Text.Json;

namespace FTPMate.Helpers;

public sealed class AppSettings
{
    public string Root { get; set; } = "";
    public string ShareMode { get; set; } = "folder";
    public string Partition { get; set; } = "";
    public int Port { get; set; } = 2121;
    public string Username { get; set; } = "ftp";
    public string PasswordProtected { get; set; } = "";
}

public static class SettingsStore
{
    private static string FolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FTPMate");

    private static string FilePath => Path.Combine(FolderPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
