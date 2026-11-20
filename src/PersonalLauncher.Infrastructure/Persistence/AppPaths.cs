using PersonalLauncher.Core.Contracts;

namespace PersonalLauncher.Infrastructure.Persistence;

public sealed class AppPaths : IAppPaths
{
    public string DataRootDirectory { get; }
    public string DatabaseFilePath => Path.Combine(DataRootDirectory, "library.db");
    public string SettingsFilePath => Path.Combine(DataRootDirectory, "settings.json");
    public string AssetsRootDirectory => Path.Combine(DataRootDirectory, "Assets");
    public string GameAssetsRootDirectory => Path.Combine(AssetsRootDirectory, "games");
    public string ThumbnailsCacheDirectory => Path.Combine(DataRootDirectory, "Cache", "thumbnails");
    public string SecurityDirectory => Path.Combine(DataRootDirectory, "Security");
    public string PasscodeFilePath => Path.Combine(SecurityDirectory, "passcode");
    public string PasscodeBackupFilePath => Path.Combine(SecurityDirectory, "passcode.bak");
    public string MailCredentialsFilePath => Path.Combine(SecurityDirectory, "mail-credentials.dat");
    public string LogsDirectory => Path.Combine(DataRootDirectory, "Logs");

    public string DefaultAppLogoPath { get; }
    public string DefaultGameIconPath { get; }
    public string DefaultWallpaperPath { get; }

    public AppPaths(string? customDataRoot = null, string? defaultsDirectory = null)
    {
        DataRootDirectory = string.IsNullOrWhiteSpace(customDataRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PersonalLauncher")
            : Path.GetFullPath(customDataRoot);

        string resolvedDefaults = !string.IsNullOrWhiteSpace(defaultsDirectory)
            ? Path.GetFullPath(defaultsDirectory)
            : ResolveBundledDefaultsDirectory();

        DefaultAppLogoPath = Path.Combine(resolvedDefaults, "app_logo.png");
        DefaultGameIconPath = Path.Combine(resolvedDefaults, "logo_default.png");
        DefaultWallpaperPath = Path.Combine(resolvedDefaults, "wallpaper_default.jpg");
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataRootDirectory);
        Directory.CreateDirectory(AssetsRootDirectory);
        Directory.CreateDirectory(GameAssetsRootDirectory);
        Directory.CreateDirectory(ThumbnailsCacheDirectory);
        Directory.CreateDirectory(SecurityDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static string ResolveBundledDefaultsDirectory()
    {
        string baseDir = AppContext.BaseDirectory;
        string candidate1 = Path.Combine(baseDir, "Assets", "Defaults");
        if (Directory.Exists(candidate1))
            return candidate1;

        string candidate2 = Path.Combine(baseDir, "assets", "defaults");
        if (Directory.Exists(candidate2))
            return candidate2;

        return candidate1;
    }
}
