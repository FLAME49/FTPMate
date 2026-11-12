using System.IO;

namespace FTPMate.Helpers;

public static class Partitions
{
    public static List<DriveInfo> Ready() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string Letter(DriveInfo drive) => drive.Name.TrimEnd('\\', '/').TrimEnd(':');

    public static string Label(DriveInfo drive)
    {
        try { return drive.VolumeLabel; }
        catch { return ""; }
    }
}
