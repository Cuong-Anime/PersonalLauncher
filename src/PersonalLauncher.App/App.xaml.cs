using System.IO;
using System.Windows;
using Microsoft.Win32;
using PersonalLauncher.App.Services;
using PersonalLauncher.App.ViewModels;
using PersonalLauncher.App.Views;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;
using PersonalLauncher.Core.Services;
using PersonalLauncher.Infrastructure.Assets;
using PersonalLauncher.Infrastructure.Games;
using PersonalLauncher.Infrastructure.Mail;
using PersonalLauncher.Infrastructure.Persistence;
using PersonalLauncher.Infrastructure.Security;
using PersonalLauncher.Infrastructure.Windows;

namespace PersonalLauncher.App;

public partial class App : Application
{
    private readonly SingleInstanceManager _singleInstance;
    private readonly bool _startInBackground;
    private bool _isTrueExitRequested;

    private IAppPaths? _paths;
    private IAppLogger? _logger;
    private ISettingsService? _settingsService;
    private ILocalizationService? _localization;
    private ISecretAccessService? _secretAccess;
    private IAssetService? _assetService;
    private IGameLibraryService? _gameLibrary;
    private IGameScanService? _scanService;
    private IGameLaunchService? _launchService;
    private MailOutboxService? _mailOutbox;
    private IStartupService? _startupService;
    private ITrayService? _trayService;
    private ImageCacheService? _imageCache;
    private MainViewModel? _mainVm;
    private MainWindow? _mainWindow;

    public App(SingleInstanceManager singleInstance, bool startInBackground)
    {
        _singleInstance = singleInstance;
        _startInBackground = startInBackground;
        InitializeComponent();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            await InitializeServicesAndShowAsync();
        }
        catch (Exception ex)
        {
            _logger?.Error("Startup", "Fatal error during launcher startup.", ex);
            MessageBox.Show(
                $"PersonalLauncher failed to start:\n\n{ex.Message}",
                "PersonalLauncher Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task InitializeServicesAndShowAsync()
    {
        _paths = new AppPaths();
        _paths.EnsureDirectoriesExist();
        _logger = new RollingFileLogger(_paths.LogsDirectory);
        _logger.Info("Startup", $"Starting PersonalLauncher (background={_startInBackground}).");

        var db = new SqliteDatabase(_paths, _logger);
        await db.InitializeAsync();

        var gameRepo = new SqliteGameRepository(db);
        var assetRepo = new SqliteAssetRepository(db);
        var securityRepo = new SqliteSecurityRepository(db);
        var mailRepo = new SqliteMailRepository(db);

        _settingsService = new JsonSettingsService(_paths, _logger);
        AppSettings settings = await _settingsService.LoadAsync();

        _localization = new LocalizationService();
        _localization.SetLanguage(settings.Language);
        UiAssetService.LoadButtonResources(this);

        var protector = new DpapiDataProtector();
        var passcodeStore = new PasscodeStore(_paths, protector, _logger);
        var mailCredStore = new MailCredentialStore(_paths, protector);
        var mailTransport = new MailKitSmtpTransport();
        _mailOutbox = new MailOutboxService(mailRepo, mailCredStore, mailTransport, _logger);

        var attemptService = new SecurityAttemptService(securityRepo, protector, _logger);
        _secretAccess = new SecretAccessService(
            securityRepo,
            gameRepo,
            passcodeStore,
            attemptService,
            _logger,
            onAlertQueued: () => _mailOutbox.SignalWorker());

        var fileIdentity = new WindowsFileIdentityProvider();
        _assetService = new AssetService(_paths, assetRepo, _logger);
        _gameLibrary = new GameLibraryService(gameRepo, _assetService, _secretAccess, _settingsService, fileIdentity, _logger);
        var searchService = new SearchService(_gameLibrary);
        _scanService = new GameScanService(gameRepo, fileIdentity, _logger);
        _launchService = new GameLaunchService(_gameLibrary, _secretAccess, _logger);
        _startupService = new StartupService(_logger);
        _imageCache = new ImageCacheService(maxEntries: 140);

        string trayIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
        _trayService = new TrayService(_logger, trayIconPath);
        bool trayOk = _trayService.Initialize();
        UpdateTrayLabels();

        _trayService.OpenRequested += (_, _) => Dispatcher.Invoke(() => _mainWindow?.RestoreFromTrayOrActivation());
        _trayService.LockSecretRequested += (_, _) => Dispatcher.Invoke(() => _secretAccess.Lock("tray_menu"));
        _trayService.ExitRequested += (_, _) => Dispatcher.Invoke(PerformTrueExit);

        _localization.LanguageChanged += (_, _) => UpdateTrayLabels();
        _secretAccess.ScopeChanged += (_, _) => Dispatcher.InvokeAsync(UpdateTrayLabels);

        SystemEvents.SessionSwitch += OnSystemSessionSwitch;
        SessionEnding += OnWindowsSessionEnding;

        _mainVm = new MainViewModel(
            _gameLibrary,
            searchService,
            _assetService,
            _launchService,
            _secretAccess,
            _settingsService,
            _localization,
            _imageCache,
            _logger);

        WireDialogHandlers(_mainVm);

        _mainWindow = new MainWindow(
            _mainVm,
            _settingsService,
            _secretAccess,
            _trayService,
            _localization,
            () => _isTrueExitRequested,
            PerformTrueExit,
            _logger);
        MainWindow = _mainWindow;

        _singleInstance.ActivationRequested += (_, _) =>
            Dispatcher.Invoke(() => _mainWindow?.RestoreFromTrayOrActivation());
        _singleInstance.StartActivationListener();

        await _mainVm.InitializeAsync();
        _mailOutbox.StartBackgroundWorker();

        if (_startInBackground && trayOk)
        {
            // Keep hidden in tray on Windows sign-in background startup
            _logger.Info("Startup", "Started in background tray mode.");
        }
        else
        {
            _mainWindow.Show();
            if (!trayOk)
            {
                _mainVm.ShowToast(_localization.GetString("Tray_ErrorFallback"));
            }
        }
    }

    private void WireDialogHandlers(MainViewModel vm)
    {
        vm.OpenAddGameRequested += (_, _) => OpenAddGameDialog(GameVisibility.Public, _mainWindow);

        vm.OpenEditLocationRequested += (_, gameVm) =>
        {
            var editVm = new EditLocationDialogViewModel(gameVm.Model, _gameLibrary!, _localization!);
            var dlg = new EditLocationDialog(editVm) { Owner = _mainWindow };
            if (dlg.ShowDialog() == true)
                _mainVm!.RefreshActiveDisplay();
        };

        vm.OpenEditLogoRequested += (_, gameVm) =>
        {
            var editVm = new EditLogoDialogViewModel(gameVm.Model, _gameLibrary!, _assetService!, _paths!, _imageCache!);
            var dlg = new EditLogoDialog(editVm) { Owner = _mainWindow };
            if (dlg.ShowDialog() == true)
                _mainVm!.RefreshActiveDisplay();
        };

        vm.OpenEditWallpaperRequested += (_, gameVm) =>
        {
            var editVm = new EditWallpaperDialogViewModel(gameVm.Model, _gameLibrary!, _assetService!, _paths!, _imageCache!);
            var dlg = new EditWallpaperDialog(editVm) { Owner = _mainWindow };
            if (dlg.ShowDialog() == true)
                _mainVm!.RefreshActiveDisplay();
        };

        vm.ConfirmDeleteGameRequested += async (_, gameVm) =>
        {
            string msg = _localization!.Format("Delete_Message", gameVm.DisplayName);
            var dlg = new ConfirmDeleteDialog(msg) { Owner = _mainWindow };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    await _gameLibrary!.DeleteGameFromLibraryAsync(gameVm.Id);
                }
                catch (Exception ex)
                {
                    _mainVm!.ShowToast(ex.Message);
                }
            }
        };

        vm.RequestSecretManageElevationAsync += () =>
        {
            var prompt = new ManagePinPromptDialog(_secretAccess!, _localization!) { Owner = _mainWindow };
            bool granted = prompt.ShowDialog() == true;
            return Task.FromResult(granted);
        };

        vm.OpenSettingsRequested += (_, _) => OpenSettingsDialog(SettingsSection.General);
        vm.OpenSettingsSecretRequested += (_, _) => OpenSettingsDialog(SettingsSection.Secret);
    }

    private void OpenAddGameDialog(GameVisibility visibility, Window? owner)
    {
        var addVm = new AddGameDialogViewModel(_gameLibrary!, _scanService!, _paths!, _localization!, _imageCache!, visibility);
        var dlg = new AddGameDialog(addVm) { Owner = owner ?? _mainWindow };
        if (dlg.ShowDialog() == true && addVm.CreatedGame is not null && visibility == GameVisibility.Public)
        {
            _ = _mainVm!.ReloadPublicGamesAsync(selectGameId: addVm.CreatedGame.Id);
        }
    }

    private void OpenSettingsDialog(SettingsSection initialSection)
    {
        var settingsVm = new SettingsDialogViewModel(
            _settingsService!,
            _localization!,
            _startupService!,
            _assetService!,
            _paths!,
            _gameLibrary!,
            _secretAccess!,
            _mailOutbox!,
            _imageCache!,
            initialSection);

        var dlg = new SettingsDialog(settingsVm) { Owner = _mainWindow };
        settingsVm.OpenAddSecretGameRequested += (_, _) =>
        {
            OpenAddGameDialog(GameVisibility.Secret, dlg);
            _ = settingsVm.ReloadManageGameListsAsync();
        };

        dlg.ShowDialog();
    }

    private void UpdateTrayLabels()
    {
        if (_trayService is null || _localization is null || _secretAccess is null)
            return;

        _trayService.UpdateState(
            secretUnlocked: _secretAccess.IsUnlockedForBrowse,
            openLabel: _localization.GetString("Tray_Open"),
            lockSecretLabel: _localization.GetString("Tray_LockSecret"),
            exitLabel: _localization.GetString("Tray_Exit"),
            tooltip: _localization.GetString("App_Name"));
    }

    private void OnSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Rule 8.2: Windows lock or logoff immediately locks Secret!
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect)
        {
            _secretAccess?.Lock($"windows_{e.Reason}");
        }
    }

    private void OnWindowsSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // Rule 7.3: Never block Windows shutdown/logoff with MinimizeToTray!
        _isTrueExitRequested = true;
        _secretAccess?.Lock("windows_session_ending");
        CleanupResources();
    }

    private void PerformTrueExit()
    {
        if (_isTrueExitRequested)
            return;

        _isTrueExitRequested = true;
        _secretAccess?.Lock("app_exit");
        CleanupResources();
        _mainWindow?.Close();
        Shutdown(0);
    }

    private void CleanupResources()
    {
        try { SystemEvents.SessionSwitch -= OnSystemSessionSwitch; } catch { }
        try { _mainVm?.Dispose(); } catch { }
        try { _trayService?.Dispose(); } catch { }
        try { _mailOutbox?.Dispose(); } catch { }
        try { PersonalLauncher.App.Controls.LiveVideoEngine.Shutdown(); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CleanupResources();
        base.OnExit(e);
    }
}
