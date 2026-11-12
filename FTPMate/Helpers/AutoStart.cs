using System.IO;
using Microsoft.Win32;

namespace FTPMate.Helpers;

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FTPMate";
    private const string AutoStartArgument = "--autostart";

    private static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FTPMate.exe");

    private static string Command => $"\"{ExecutablePath}\" {AutoStartArgument}";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value &&
                   value.Trim().Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        key.SetValue(ValueName, Command);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void RepairIfBroken()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string value)
                return;

            string path = value.Trim();
            if (path.StartsWith('"'))
            {
                int end = path.IndexOf('"', 1);
                path = end > 1 ? path[1..end] : path;
            }
            else
            {
                int index = path.IndexOf(' ' + AutoStartArgument, StringComparison.OrdinalIgnoreCase);
                if (index > 0)
                    path = path[..index];
            }

            if (!File.Exists(path))
                Enable();
        }
        catch { }
    }
}
