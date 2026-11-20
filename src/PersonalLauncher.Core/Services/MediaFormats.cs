namespace PersonalLauncher.Core.Services;

public static class MediaFormats
{
    public static readonly HashSet<string> StaticImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg"
    };

    public static readonly HashSet<string> IconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".ico"
    };

    public static readonly HashSet<string> VideoWallpaperExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".wmv", ".m4v", ".avi", ".mkv"
    };

    public static bool IsVideoPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        string ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && VideoWallpaperExtensions.Contains(ext);
    }

    public static bool IsSupportedWallpaperExtension(string ext)
        => StaticImageExtensions.Contains(ext) || VideoWallpaperExtensions.Contains(ext);
}
