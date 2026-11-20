namespace PersonalLauncher.Core.Services;

public static class ScanHeuristics
{
    private static readonly (string Token, string Reason)[] HelperKeywords =
    {
        ("unins", "uninstaller"),
        ("uninstall", "uninstaller"),
        ("setup", "installer"),
        ("install", "installer"),
        ("updater", "updater"),
        ("update", "updater"),
        ("patcher", "updater"),
        ("crashhandler", "crash_reporter"),
        ("crashreport", "crash_reporter"),
        ("crashsender", "crash_reporter"),
        ("bugreport", "crash_reporter"),
        ("werfault", "crash_reporter"),
        ("vcredist", "redist"),
        ("redist", "redist"),
        ("dxsetup", "redist"),
        ("directx", "redist"),
        ("dotnetfx", "redist"),
        ("ndp4", "redist"),
        ("ue4prereq", "redist"),
        ("ueprereq", "redist"),
        ("oalinst", "redist"),
        ("physx", "redist"),
        ("easyanticheat_setup", "installer"),
        ("easyanticheat_eos_setup", "installer"),
        ("battleye", "helper"),
        ("beservice", "helper"),
        ("zfgamebrowser", "helper"),
        ("cefprocess", "helper"),
        ("webhelper", "helper"),
        ("qtwebengineprocess", "helper"),
        ("7za", "helper"),
        ("7z", "helper"),
        ("ffmpeg", "helper")
    };

    private static readonly string[] HelperFolderSegments =
    {
        "_commonredist",
        "redist",
        "redistributables",
        "directx",
        "vcredist",
        "prerequisites",
        "__installer",
        "installer"
    };

    public static (bool IsHelper, string? Reason) ClassifyExecutable(string exePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        string fullLower = exePath.ToLowerInvariant();

        foreach (string folder in HelperFolderSegments)
        {
            if (fullLower.Contains($"{Path.DirectorySeparatorChar}{folder}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                return (true, "redist_folder");
        }

        foreach (var (token, reason) in HelperKeywords)
        {
            if (fileName.Contains(token, StringComparison.Ordinal))
                return (true, reason);
        }

        return (false, null);
    }

    /// <summary>
    /// Scores executables within the same direct folder to pick the most likely primary entry point
    /// when a folder contains multiple EXEs.
    /// </summary>
    public static int ScorePrimaryCandidate(string exePath, string directParentFolderName)
    {
        var (isHelper, _) = ClassifyExecutable(exePath);
        if (isHelper)
            return -1000;

        string fileName = Path.GetFileNameWithoutExtension(exePath);
        string folderNorm = TextNormalization.NormalizeForSearch(directParentFolderName).Replace(" ", "");
        string fileNorm = TextNormalization.NormalizeForSearch(fileName).Replace(" ", "");

        int score = 0;
        if (!string.IsNullOrEmpty(folderNorm) && string.Equals(fileNorm, folderNorm, StringComparison.Ordinal))
            score += 500;
        else if (!string.IsNullOrEmpty(folderNorm) &&
                 (fileNorm.Contains(folderNorm, StringComparison.Ordinal) || folderNorm.Contains(fileNorm, StringComparison.Ordinal)))
            score += 250;

        if (fileName.Contains("shipping", StringComparison.OrdinalIgnoreCase))
            score += 120;
        if (fileName.Contains("launcher", StringComparison.OrdinalIgnoreCase))
            score += 40;
        if (fileName.Contains("config", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("settings", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("tool", StringComparison.OrdinalIgnoreCase))
            score -= 100;

        return score;
    }
}
