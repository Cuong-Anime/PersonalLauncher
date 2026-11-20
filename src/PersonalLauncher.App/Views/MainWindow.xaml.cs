using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PersonalLauncher.App.Services;
using PersonalLauncher.App.ViewModels;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.App.Views;

public partial class MainWindow : Window
{
    public const double FixedWindowWidth = 1280;
    public const double FixedWindowHeight = 720;

    private readonly MainViewModel _vm;
    private readonly ISettingsService _settingsService;
    private readonly ISecretAccessService _secretAccess;
    private readonly ITrayService _trayService;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger? _logger;
    private readonly Func<bool> _isTrueExitRequested;
    private readonly Action _requestTrueExit;

    private string? _activeLiveVideoPath;

    public MainWindow(
        MainViewModel vm,
        ISettingsService settingsService,
        ISecretAccessService secretAccess,
        ITrayService trayService,
        ILocalizationService localization,
        Func<bool> isTrueExitRequested,
        Action requestTrueExit,
        IAppLogger? logger = null)
    {
        InitializeComponent();
        WindowCornerHelper.EnableRoundedCorners(this);

        _vm = vm;
        _settingsService = settingsService;
        _secretAccess = secretAccess;
        _trayService = trayService;
        _localization = localization;
        _logger = logger;
        _isTrueExitRequested = isTrueExitRequested;
        _requestTrueExit = requestTrueExit;

        DataContext = _vm;
        RestoreWindowPosition();

        _vm.WallpaperMediaChanged += OnWallpaperMediaChanged;
        _vm.SecretPinInputClearRequested += (_, _) => SecretBrowsePinInput.Clear();
        _vm.SecretPinInputFocusRequested += (_, _) => Dispatcher.InvokeAsync(() => SecretBrowsePinInput.FocusInput());

        WallpaperVideo.FirstFrameRendered += WallpaperVideo_FirstFrameRendered;
        WallpaperVideo.PlaybackError += WallpaperVideo_PlaybackError;

        StateChanged += OnWindowStateChanged;
        IsVisibleChanged += OnWindowIsVisibleChanged;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Closing += OnWindowClosing;
        Closed += (_, _) => WallpaperVideo.Dispose();
    }

    private void RestoreWindowPosition()
    {
        // Window size is strictly locked at 1280x720
        Width = FixedWindowWidth;
        Height = FixedWindowHeight;
        MinWidth = FixedWindowWidth;
        MaxWidth = FixedWindowWidth;
        MinHeight = FixedWindowHeight;
        MaxHeight = FixedWindowHeight;
        _vm.IsNarrowLayout = false;

        AppSettings s = _settingsService.Current;
        if (s.WindowLeft.HasValue && s.WindowTop.HasValue &&
            s.WindowLeft.Value >= SystemParameters.VirtualScreenLeft - 50 &&
            s.WindowTop.Value >= SystemParameters.VirtualScreenTop - 50 &&
            s.WindowLeft.Value <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            s.WindowTop.Value <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft.Value;
            Top = s.WindowTop.Value;
        }
    }

    public async Task PersistWindowBoundsAsync()
    {
        await _settingsService.UpdateAsync(s => s with
        {
            WindowWidth = FixedWindowWidth,
            WindowHeight = FixedWindowHeight,
            WindowLeft = Left,
            WindowTop = Top,
            IsMaximized = false
        });
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            return;
        }

        // Rule 7.3 & 8.2: Minimizing locks Secret immediately & pauses live video to save GPU/CPU!
        if (WindowState == WindowState.Minimized)
        {
            if (_secretAccess.IsUnlockedForBrowse)
                _secretAccess.Lock("window_minimized");
            PauseLiveVideo();
        }
        else if (IsVisible)
        {
            ResumeLiveVideoIfActive();
        }
    }

    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            ResumeLiveVideoIfActive();
        }
        else
        {
            PauseLiveVideo();
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (!_vm.IsAllGamesPageActive)
                _vm.GoToAllGamesCommand.Execute(null);

            if (_vm.IsPublicTabActive)
            {
                PublicSearchBox.Focus();
                PublicSearchBox.SelectAll();
                e.Handled = true;
            }
            else if (_vm.ShowSecretLibraryContent)
            {
                SecretSearchBox.Focus();
                SecretSearchBox.SelectAll();
                e.Handled = true;
            }
        }
    }

    private void OnWallpaperMediaChanged(object? sender, (ImageSource? StaticImage, string? LiveVideoPath) media)
    {
        UpdateStaticWallpaper(media.StaticImage);

        if (!string.IsNullOrWhiteSpace(media.LiveVideoPath) && File.Exists(media.LiveVideoPath))
        {
            StartLiveVideo(media.LiveVideoPath);
        }
        else
        {
            StopAndClearLiveVideo();
        }
    }

    private void UpdateStaticWallpaper(ImageSource? nextWallpaper)
    {
        if (ReferenceEquals(WallpaperFront.Source, nextWallpaper))
            return;

        if (WallpaperFront.Source is null)
        {
            WallpaperFront.BeginAnimation(OpacityProperty, null);
            WallpaperFront.Source = nextWallpaper;
            WallpaperFront.Opacity = 1;
            WallpaperBack.Source = nextWallpaper;
            return;
        }

        WallpaperBack.Source = WallpaperFront.Source;
        WallpaperFront.BeginAnimation(OpacityProperty, null);
        WallpaperFront.Opacity = 0;
        WallpaperFront.Source = nextWallpaper;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        WallpaperFront.BeginAnimation(OpacityProperty, fade);
    }

    private void StartLiveVideo(string videoFilePath)
    {
        _activeLiveVideoPath = videoFilePath;

        // Defer actual playback until window is visible (handles InitializeAsync called before Show!)
        if (!IsVisible || WindowState == WindowState.Minimized)
            return;

        if (WallpaperVideo.IsPlayingPath(videoFilePath))
        {
            WallpaperVideo.Visibility = Visibility.Visible;
            if (WallpaperVideo.HasRenderedFrame)
            {
                WallpaperVideo.BeginAnimation(OpacityProperty, null);
                WallpaperVideo.Opacity = 1;
            }
            WallpaperVideo.Resume();
            return;
        }

        WallpaperVideo.BeginAnimation(OpacityProperty, null);
        WallpaperVideo.Opacity = 0;
        WallpaperVideo.Visibility = Visibility.Visible;
        if (!WallpaperVideo.Play(videoFilePath))
        {
            StopAndClearLiveVideo();
        }
    }

    private void StopAndClearLiveVideo()
    {
        _activeLiveVideoPath = null;
        WallpaperVideo.BeginAnimation(OpacityProperty, null);
        WallpaperVideo.Opacity = 0;
        WallpaperVideo.Visibility = Visibility.Collapsed;
        WallpaperVideo.StopAndClear();
    }

    private void PauseLiveVideo()
    {
        if (_activeLiveVideoPath is not null)
            WallpaperVideo.Pause();
    }

    private void ResumeLiveVideoIfActive()
    {
        if (_activeLiveVideoPath is not null)
        {
            if (WallpaperVideo.Visibility != Visibility.Visible)
                StartLiveVideo(_activeLiveVideoPath);
            else
                WallpaperVideo.Resume();
        }
    }

    private void WallpaperVideo_FirstFrameRendered(object? sender, EventArgs e)
    {
        if (_activeLiveVideoPath is null)
            return;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        WallpaperVideo.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void WallpaperVideo_PlaybackError(object? sender, string error)
    {
        _logger?.Warn("Video", $"Live wallpaper playback failed: {error}");
        StopAndClearLiveVideo();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_secretAccess.IsUnlockedForBrowse)
            _secretAccess.Lock("window_minimized");
        PauseLiveVideo();
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _secretAccess.Lock("window_closing");

        if (_isTrueExitRequested())
        {
            StopAndClearLiveVideo();
            _ = PersistWindowBoundsAsync();
            return;
        }

        if (_settingsService.Current.CloseAction == CloseAction.MinimizeToTray)
        {
            if (_trayService.IsAvailable)
            {
                e.Cancel = true;
                PauseLiveVideo();
                _ = PersistWindowBoundsAsync();
                Hide();
                return;
            }
            else
            {
                e.Cancel = true;
                _vm.ShowToast(_localization.GetString("Tray_ErrorFallback"));
                return;
            }
        }

        StopAndClearLiveVideo();
        _ = PersistWindowBoundsAsync();
        _requestTrueExit();
    }

    public void RestoreFromTrayOrActivation()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        ResumeLiveVideoIfActive();
    }

    private void GameMoreMenuButton_Click(object sender, RoutedEventArgs e)
    {
        GameMorePopup.IsOpen = !GameMorePopup.IsOpen;
    }

    private void PopupAction_Click(object sender, RoutedEventArgs e)
    {
        GameMorePopup.IsOpen = false;
    }

    private async void SecretBrowsePinInput_PinCompleted(object? sender, string pin)
    {
        await _vm.SubmitSecretBrowsePinAsync(pin);
    }

    private void SecretBrowsePinInput_InvalidInputAttempted(object? sender, EventArgs e)
    {
        _vm.HandleInvalidPinInput();
    }

    private void CardsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _vm.OpenActiveGameFromCardCommand.Execute(null);
    }

    private void CardsListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vm.OpenActiveGameFromCardCommand.Execute(null);
        }
    }

    private void ScrollPublicLeft_Click(object sender, RoutedEventArgs e) => ScrollListBoxHorizontal(PublicCardsListBox, -320);
    private void ScrollPublicRight_Click(object sender, RoutedEventArgs e) => ScrollListBoxHorizontal(PublicCardsListBox, 320);
    private void ScrollSecretLeft_Click(object sender, RoutedEventArgs e) => ScrollListBoxHorizontal(SecretCardsListBox, -320);
    private void ScrollSecretRight_Click(object sender, RoutedEventArgs e) => ScrollListBoxHorizontal(SecretCardsListBox, 320);

    private static void ScrollListBoxHorizontal(ListBox listBox, double delta)
    {
        ScrollViewer? sv = FindVisualChild<ScrollViewer>(listBox);
        if (sv is not null)
        {
            sv.ScrollToHorizontalOffset(Math.Max(0, sv.HorizontalOffset + delta));
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                return typed;
            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
                return nested;
        }
        return null;
    }
}
