using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalLauncher.App.Services;

/// <summary>
/// Loads UI button assets into Application.Current.Resources.
/// Checks external Assets\buttons\*.png next to PersonalLauncher.exe first (so users can
/// edit button colors directly without rebuilding!), falling back to embedded pack:// resources.
/// </summary>
public static class UiAssetService
{
    private static readonly (string ResourceKey, string FileName)[] ButtonAssets =
    {
        ("ButtonMenuSource", "buttons_menu.png"),
        ("ButtonPlaySource", "buttons_play.png"),
        ("ButtonPlayBlankSource", "buttons_play_blank.png"),
        ("ButtonSettingSource", "buttons_setting.png"),
        ("ButtonActionCircleSource", "Somthing_Button.png")
    };

    public static void LoadButtonResources(Application? app)
    {
        if (app is null)
            return;

        string externalDir = Path.Combine(AppContext.BaseDirectory, "Assets", "buttons");

        foreach (var (key, fileName) in ButtonAssets)
        {
            ImageSource? loaded = TryLoadFromDisk(Path.Combine(externalDir, fileName));
            loaded ??= LoadEmbeddedPack($"pack://application:,,,/PersonalLauncher;component/Assets/buttons/{fileName}");

            if (loaded is not null)
                app.Resources[key] = loaded;
        }
    }

    private static ImageSource? TryLoadFromDisk(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadEmbeddedPack(string packUri)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(packUri, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
