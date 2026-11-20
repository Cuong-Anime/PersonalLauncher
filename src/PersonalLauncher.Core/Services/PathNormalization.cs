namespace PersonalLauncher.Core.Services;

public static class PathNormalization
{
    /// <summary>
    /// Normalizes a Windows EXE path into a stable canonical deduplication key.
    /// </summary>
    public static string NormalizeExePathKey(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("EXE path cannot be empty.", nameof(exePath));

        string trimmed = exePath.Trim().Trim('"');
        string full = Path.GetFullPath(trimmed);
        // Strip extended-length prefix if present
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
            full = full[4..];

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .ToLowerInvariant();
    }

    /// <summary>
    /// Rule 5.2 #7: Auto-scanned game name MUST be the direct parent folder name of the selected EXE.
    /// E.g., D:\Games\MyGame\game.exe -> MyGame; D:\Games\MyGame\Binaries\Win64\game.exe -> Win64.
    /// </summary>
    public static string GetDirectParentFolderName(string exePath)
    {
        string full = Path.GetFullPath(exePath.Trim().Trim('"'));
        string? dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir))
            return Path.GetFileNameWithoutExtension(full);

        string trimmedDir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string folderName = Path.GetFileName(trimmedDir);
        if (!string.IsNullOrWhiteSpace(folderName))
            return folderName;

        // Fallback if EXE sits directly at drive root (e.g., D:\game.exe)
        return Path.GetFileNameWithoutExtension(full);
    }

    /// <summary>
    /// Verifies that targetPath resolves strictly inside allowedRootDirectory (prevents path traversal).
    /// </summary>
    public static bool IsPathInsideDirectory(string targetPath, string allowedRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(allowedRootDirectory))
            return false;

        string rootFull = Path.GetFullPath(allowedRootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string targetFull = Path.GetFullPath(targetPath);

        return targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }
}
