using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PersonalLauncher.App.Services;
using PersonalLauncher.App.ViewModels;
using PersonalLauncher.App.Views;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;
using PersonalLauncher.Infrastructure.Games;
using PersonalLauncher.Infrastructure.Windows;
using Xunit;

namespace PersonalLauncher.Infrastructure.Tests;

public sealed class WpfUiSmokeTests
{
    private sealed class NoOpTrayService : ITrayService
    {
        public bool IsAvailable => true;
        public string? LastError => null;
        public event EventHandler? OpenRequested { add { } remove { } }
        public event EventHandler? LockSecretRequested { add { } remove { } }
        public event EventHandler? ExitRequested { add { } remove { } }
        public bool Initialize() => true;
        public void UpdateState(bool secretUnlocked, string openLabel, string lockSecretLabel, string exitLabel, string tooltip) { }
        public void Hide() { }
        public void Dispose() { }
    }

    [Fact]
    public void T01_T02_T11_WpfViewsInstantiateBindAndRenderCleanly()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunOnSta();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(threadEx);
    }

    private static void RunOnSta()
    {
        // Ensure WPF Application and bundled resources exist for pack://application:,,, URIs
        string defaultsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "defaults"));
        using var env = new TestEnvironment();
        // Copy real bundled default images into env.DefaultsRoot so screenshots look realistic
        if (Directory.Exists(defaultsDir))
        {
            foreach (string f in Directory.GetFiles(defaultsDir))
                File.Copy(f, Path.Combine(env.DefaultsRoot, Path.GetFileName(f)), overwrite: true);
        }

        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(
                System.Windows.Threading.Dispatcher.CurrentDispatcher));

        if (Application.Current is null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PersonalLauncher;component/Resources/Themes/DarkTheme.xaml", UriKind.Absolute)
            });
        }

        var loc = new LocalizationService();
        loc.SetLanguage("vi-VN");
        var cache = new ImageCacheService();
        var launcher = new GameLaunchService(env.Library, env.SecretAccess, env.Logger);

        // Add 3 sample games BEFORE binding STA SynchronizationContext
        string exe1 = env.CreateDummyExe(@"Genshin Impact\GenshinImpact.exe");
        string exe2 = env.CreateDummyExe(@"Honkai Star Rail\StarRail.exe");
        string exe3 = env.CreateDummyExe(@"Zenless Zone Zero\ZenlessZoneZero.exe");

        AwaitOnSta(env.Library.AddManualGameAsync(new GameCreateRequest { DisplayName = "Genshin Impact", ExePath = exe1 }));
        AwaitOnSta(env.Library.AddManualGameAsync(new GameCreateRequest { DisplayName = "Honkai: Star Rail", ExePath = exe2 }));
        AwaitOnSta(env.Library.AddManualGameAsync(new GameCreateRequest { DisplayName = "Zenless Zone Zero", ExePath = exe3 }));

        using var vm = new MainViewModel(env.Library, env.Search, env.AssetService, launcher, env.SecretAccess, env.Settings, loc, cache, env.Logger);
        var tray = new NoOpTrayService();
        var window = new MainWindow(vm, env.Settings, env.SecretAccess, tray, loc, () => true, () => { });

        // Verify window size is strictly locked at 1280x720
        Assert.Equal(ResizeMode.CanMinimize, window.ResizeMode);
        Assert.Equal(1280, window.MinWidth);
        Assert.Equal(1280, window.MaxWidth);
        Assert.Equal(720, window.MinHeight);
        Assert.Equal(720, window.MaxHeight);

        AwaitOnSta(vm.InitializeAsync());
        // Wait briefly for async image loads on dispatcher
        PumpDispatcher(450);

        string outDir = Environment.GetEnvironmentVariable("PL_UI_SHOT_DIR") ?? Path.Combine(env.TempRoot, "shots");
        Directory.CreateDirectory(outDir);

        // 1. Render Main Game Page (vi-VN)
        RenderElement(window, 1280, 720, Path.Combine(outDir, "01_main_game_vi.png"));

        // 2. Render All Games Page
        vm.GoToAllGamesCommand.Execute(null);
        PumpDispatcher(300);
        RenderElement(window, 1280, 720, Path.Combine(outDir, "02_all_games_vi.png"));

        // 3. Switch to Japanese and verify live localization & play button overlay!
        loc.SetLanguage("ja-JP");
        vm.GoToMainGamePageCommand.Execute(null);
        PumpDispatcher(250);
        Assert.Equal("ゲーム開始", vm.PlayButtonOverlayText);
        Assert.False(vm.UseOriginalVietnamesePlayAsset);
        RenderElement(window, 1280, 720, Path.Combine(outDir, "03_main_game_ja.png"));

        // 4. Switch to Vietnamese back and render Settings dialog
        loc.SetLanguage("vi-VN");
        using var settingsVm = new SettingsDialogViewModel(
            env.Settings, loc, new StartupService(env.Logger), env.AssetService,
            env.Paths, env.Library, env.SecretAccess, env.MailOutbox, cache);
        var settingsDlg = new SettingsDialog(settingsVm);
        PumpDispatcher(200);
        RenderElement(settingsDlg, 860, 580, Path.Combine(outDir, "04_settings_vi.png"));

        Assert.True(new FileInfo(Path.Combine(outDir, "01_main_game_vi.png")).Length > 20_000);
        Assert.True(new FileInfo(Path.Combine(outDir, "02_all_games_vi.png")).Length > 20_000);
        Assert.True(new FileInfo(Path.Combine(outDir, "03_main_game_ja.png")).Length > 20_000);
        Assert.True(new FileInfo(Path.Combine(outDir, "04_settings_vi.png")).Length > 10_000);

        // Optional real-video LibVLC decode smoke test when PL_REAL_MP4_TEST points to a real MP4
        string? realMp4 = Environment.GetEnvironmentVariable("PL_REAL_MP4_TEST");
        if (!string.IsNullOrWhiteSpace(realMp4) && File.Exists(realMp4))
        {
            using var surface = new PersonalLauncher.App.Controls.LiveVideoSurface
            {
                MaxDecodeWidth = 1280,
                MaxDecodeHeight = 720,
                Width = 1280,
                Height = 720
            };
            bool rendered = false;
            string? playErr = null;
            surface.FirstFrameRendered += (_, _) => rendered = true;
            surface.PlaybackError += (_, e) => playErr = e;

            Assert.True(surface.Play(realMp4), "LiveVideoSurface.Play returned false");
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (!rendered && playErr is null && DateTime.UtcNow < deadline)
                PumpDispatcher(100);

            Assert.Null(playErr);
            Assert.True(rendered, "LibVLC did not render a video frame within 12 seconds.");

            // Snapshot the live decoded video frame to verify non-black real video output!
            surface.Measure(new Size(1280, 720));
            surface.Arrange(new Rect(0, 0, 1280, 720));
            surface.UpdateLayout();
            var rtb = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(surface);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string framePng = Path.Combine(outDir, "05_live_video_frame.png");
            using (var fs = File.Create(framePng))
                enc.Save(fs);
            Assert.True(new FileInfo(framePng).Length > 20_000);
            surface.StopAndClear();
        }
    }

    private static void AwaitOnSta(Task task)
    {
        while (!task.IsCompleted)
            PumpDispatcher(25);
        task.GetAwaiter().GetResult();
    }

    private static void PumpDispatcher(int ms)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(25);
        }
    }

    private static void RenderElement(Window window, double width, double height, string pngPath)
    {
        var content = (FrameworkElement)window.Content;
        // Detach from hidden Window so Measure/Arrange executes full WPF layout without requiring HwndSource
        object? dc = window.DataContext;
        window.Content = null;
        content.DataContext = dc;
        content.Width = width;
        content.Height = height;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        PumpDispatcher(180);

        var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(pngPath))
        {
            enc.Save(fs);
        }

        // Re-attach so further view switches on the same window work cleanly
        window.Content = content;
    }
}
