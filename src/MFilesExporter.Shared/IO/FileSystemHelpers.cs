namespace MFilesExporter.Shared.IO;

public static class FileSystemHelpers
{
    public static void EnsureDirectoryFor(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public static long GetAvailableFreeSpaceGb(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            return long.MaxValue;
        }
        try
        {
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace / (1024L * 1024L * 1024L);
        }
        catch (ArgumentException)
        {
            return long.MaxValue;
        }
    }
}
