using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PersonalLauncher.App.Services;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;
using PersonalLauncher.Core.Services;

namespace PersonalLauncher.App.ViewModels;

public enum ActiveShellView
{
    GamePage,
    AllGamesPage
}

public enum AllGamesTab
{
    Public,
    Secret
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGameLibraryService _gameLibrary;
    private readonly ISearchService _searchService;
    private readonly IAssetService _assetService;
    private readonly IGameLaunchService _launchService;
    private readonly ISecretAccessService _secretAccess;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localization;
    private readonly ImageCacheService _imageCache;
    private readonly IAppLogger _logger;
    private readonly DispatcherTimer _inactivityTimer;
    private readonly DispatcherTimer _cooldownTimer;

    private CancellationTokenSource? _wallpaperCts;
    private CancellationTokenSource? _publicSearchDebounceCts;
    private CancellationTokenSource? _secretSearchDebounceCts;
    private readonly List<GameItemViewModel> _allPublicCache = new();
    private readonly List<GameItemViewModel> _allSecretCache = new();

    private ActiveShellView _activeView = ActiveShellView.GamePage;
    private AllGamesTab _activeAllGamesTab = AllGamesTab.Public;
    private GameItemViewModel? _selectedPublicGame;
    private GameItemViewModel? _selectedSecretGame;
    private bool _viewingSecretGamePage;

    private ImageSource? _personalLogoImage;
    private ImageSource? _currentWallpaper;
    private ImageSource? _activeTitleLogo;
    private string _activeTitleText = string.Empty;

    private string _publicSearchQuery = string.Empty;
    private string _secretSearchQuery = string.Empty;
    private bool _isNarrowLayout;

    // Secret PIN Gate State
    private bool _isSecretUnlocked;
    private bool _isPinSetupMode;
    private bool _isPasscodeCorrupted;
    private bool _isPinBusy;
    private string _pinErrorMessage = string.Empty;
    private string _setupPin = string.Empty;
    private string _setupConfirmPin = string.Empty;
    private DateTimeOffset? _cooldownUntilUtc;
    private string _statusToast = string.Empty;

    public ObservableCollection<GameItemViewModel> SidebarGames { get; } = new();
    public ObservableCollection<GameItemViewModel> FilteredPublicGames { get; } = new();
    public ObservableCollection<GameItemViewModel> FilteredSecretGames { get; } = new();

    public event EventHandler<(ImageSource? StaticImage, string? LiveVideoPath)>? WallpaperMediaChanged;
    public event EventHandler? SecretPinInputClearRequested;
    public event EventHandler? SecretPinInputFocusRequested;
    public event EventHandler? OpenAddGameRequested;
    public event EventHandler<GameItemViewModel>? OpenEditLocationRequested;
    public event EventHandler<GameItemViewModel>? OpenEditLogoRequested;
    public event EventHandler<GameItemViewModel>? OpenEditWallpaperRequested;
    public event EventHandler<GameItemViewModel>? ConfirmDeleteGameRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? OpenSettingsSecretRequested;
    public event Func<Task<bool>>? RequestSecretManageElevationAsync;

    public ActiveShellView ActiveView
    {
        get => _activeView;
        set
        {
            if (SetProperty(ref _activeView, value))
            {
                OnPropertyChanged(nameof(IsGamePageActive));
                OnPropertyChanged(nameof(IsAllGamesPageActive));
                OnPropertyChanged(nameof(ActiveGame));
                OnPropertyChanged(nameof(HasActiveGame));
                OnPropertyChanged(nameof(ShowEmptyGameState));
                RefreshActiveDisplay();
            }
        }
    }

    public bool IsGamePageActive => ActiveView == ActiveShellView.GamePage;
    public bool IsAllGamesPageActive => ActiveView == ActiveShellView.AllGamesPage;

    public AllGamesTab ActiveAllGamesTab
    {
        get => _activeAllGamesTab;
        set
        {
            if (SetProperty(ref _activeAllGamesTab, value))
            {
                OnPropertyChanged(nameof(IsPublicTabActive));
                OnPropertyChanged(nameof(IsSecretTabActive));
                OnPropertyChanged(nameof(ShowSecretPinGate));
                OnPropertyChanged(nameof(ShowSecretLibraryContent));
                OnPropertyChanged(nameof(ActiveGame));
                OnPropertyChanged(nameof(ShowSearchEmptyPublic));
                OnPropertyChanged(nameof(ShowSearchEmptySecret));

                // Rule 8.2: Leaving Secret view locks Secret immediately!
                if (value == AllGamesTab.Public && _secretAccess.IsUnlockedForBrowse)
                {
                    _secretAccess.Lock("left_secret_tab");
                }
                else if (value == AllGamesTab.Secret)
                {
                    _ = RefreshSecretGateStateAsync();
                }

                RefreshActiveDisplay();
            }
        }
    }

    public bool IsPublicTabActive => ActiveAllGamesTab == AllGamesTab.Public;
    public bool IsSecretTabActive => ActiveAllGamesTab == AllGamesTab.Secret;

    public bool ViewingSecretGamePage
    {
        get => _viewingSecretGamePage;
        private set
        {
            if (SetProperty(ref _viewingSecretGamePage, value))
            {
                OnPropertyChanged(nameof(ActiveGame));
                OnPropertyChanged(nameof(HasActiveGame));
                OnPropertyChanged(nameof(ShowEmptyGameState));
                OnPropertyChanged(nameof(IsShowingSecretContext));
            }
        }
    }

    public bool IsShowingSecretContext =>
        (IsGamePageActive && ViewingSecretGamePage && IsSecretUnlocked) ||
        (IsAllGamesPageActive && IsSecretTabActive && IsSecretUnlocked);

    public GameItemViewModel? SelectedPublicGame
    {
        get => _selectedPublicGame;
        set
        {
            if (ReferenceEquals(_selectedPublicGame, value))
                return;

            if (_selectedPublicGame is not null)
                _selectedPublicGame.IsSelected = false;

            _selectedPublicGame = value;
            if (_selectedPublicGame is not null)
                _selectedPublicGame.IsSelected = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveGame));
            OnPropertyChanged(nameof(HasActiveGame));
            OnPropertyChanged(nameof(ShowEmptyGameState));
            NotifyPlayButtonChanged();

            if (_selectedPublicGame is not null)
            {
                string selectedId = _selectedPublicGame.Id;
                _ = _settingsService.UpdateAsync(s => s with { LastPublicSelectedGameId = selectedId });
            }

            if (!IsShowingSecretContext)
                RefreshActiveDisplay();
        }
    }

    public GameItemViewModel? SelectedSecretGame
    {
        get => _selectedSecretGame;
        set
        {
            if (ReferenceEquals(_selectedSecretGame, value))
                return;

            if (_selectedSecretGame is not null)
                _selectedSecretGame.IsSelected = false;

            _selectedSecretGame = value;
            if (_selectedSecretGame is not null)
                _selectedSecretGame.IsSelected = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveGame));
            OnPropertyChanged(nameof(HasActiveGame));
            OnPropertyChanged(nameof(ShowEmptyGameState));
            NotifyPlayButtonChanged();

            if (IsShowingSecretContext)
            {
                _secretAccess.RecordActivity();
                RefreshActiveDisplay();
            }
        }
    }

    public GameItemViewModel? ActiveGame =>
        IsShowingSecretContext ? SelectedSecretGame : SelectedPublicGame;

    public bool HasActiveGame => ActiveGame is not null;
    public bool HasFilteredPublicGames => FilteredPublicGames.Count > 0;
    public bool HasFilteredSecretGames => FilteredSecretGames.Count > 0;
    public bool ShowEmptyGameState => IsGamePageActive && !ViewingSecretGamePage && SelectedPublicGame is null;
    public bool ShowPublicAllGamesEmpty => FilteredPublicGames.Count == 0 && string.IsNullOrWhiteSpace(PublicSearchQuery);
    public bool ShowSearchEmptyPublic => FilteredPublicGames.Count == 0 && !string.IsNullOrWhiteSpace(PublicSearchQuery);
    public bool ShowSecretAllGamesEmpty => IsSecretUnlocked && FilteredSecretGames.Count == 0 && string.IsNullOrWhiteSpace(SecretSearchQuery);
    public bool ShowSearchEmptySecret => IsSecretUnlocked && FilteredSecretGames.Count == 0 && !string.IsNullOrWhiteSpace(SecretSearchQuery);

    public ImageSource? PersonalLogoImage
    {
        get => _personalLogoImage;
        private set => SetProperty(ref _personalLogoImage, value);
    }

    public ImageSource? CurrentWallpaper
    {
        get => _currentWallpaper;
        private set => SetProperty(ref _currentWallpaper, value);
    }

    public ImageSource? ActiveTitleLogo
    {
        get => _activeTitleLogo;
        private set
        {
            if (SetProperty(ref _activeTitleLogo, value))
                OnPropertyChanged(nameof(ShowActiveTextTitle));
        }
    }

    public string ActiveTitleText
    {
        get => _activeTitleText;
        private set => SetProperty(ref _activeTitleText, value);
    }

    public bool ShowActiveTextTitle => ActiveTitleLogo is null && !string.IsNullOrWhiteSpace(ActiveTitleText);

    public string PublicSearchQuery
    {
        get => _publicSearchQuery;
        set
        {
            if (SetProperty(ref _publicSearchQuery, value))
            {
                OnPropertyChanged(nameof(HasPublicSearchQuery));
                SchedulePublicSearch();
            }
        }
    }

    public bool HasPublicSearchQuery => !string.IsNullOrEmpty(PublicSearchQuery);

    public string SecretSearchQuery
    {
        get => _secretSearchQuery;
        set
        {
            if (SetProperty(ref _secretSearchQuery, value))
            {
                OnPropertyChanged(nameof(HasSecretSearchQuery));
                _secretAccess.RecordActivity();
                ScheduleSecretSearch();
            }
        }
    }

    public bool HasSecretSearchQuery => !string.IsNullOrEmpty(SecretSearchQuery);

    public bool IsNarrowLayout
    {
        get => _isNarrowLayout;
        set => SetProperty(ref _isNarrowLayout, value);
    }

    // Play Button state: uses exact assets/buttons/buttons_play.png when vi-VN + Ready,
    // and assets/buttons/buttons_play_blank.png + localized text otherwise!
    public bool UseOriginalVietnamesePlayAsset =>
        string.Equals(_localization.CurrentLanguage, "vi-VN", StringComparison.OrdinalIgnoreCase) &&
        (ActiveGame is null || ActiveGame.LaunchState == GameLaunchState.Ready);

    public bool ShowDynamicPlayOverlayText => !UseOriginalVietnamesePlayAsset;

    public string PlayButtonOverlayText
    {
        get
        {
            GameLaunchState state = ActiveGame?.LaunchState ?? GameLaunchState.Ready;
            return state switch
            {
                GameLaunchState.Launching => _localization.GetString("Play_Launching"),
                GameLaunchState.Running => _localization.GetString("Play_Running"),
                GameLaunchState.Missing => _localization.GetString("Play_Missing"),
                GameLaunchState.Error => _localization.GetString("Play_Error"),
                _ => _localization.GetString("Play_StartGame")
            };
        }
    }

    public bool CanPlayActiveGame =>
        ActiveGame is not null &&
        ActiveGame.LaunchState is GameLaunchState.Ready or GameLaunchState.Error;

    public bool IsActiveGameMissing => ActiveGame is not null && ActiveGame.IsMissingExe;

    // Secret PIN Gate properties
    public bool IsSecretUnlocked
    {
        get => _isSecretUnlocked;
        private set
        {
            if (SetProperty(ref _isSecretUnlocked, value))
            {
                OnPropertyChanged(nameof(ShowSecretPinGate));
                OnPropertyChanged(nameof(ShowSecretLibraryContent));
                OnPropertyChanged(nameof(IsShowingSecretContext));
                OnPropertyChanged(nameof(ShowSecretAllGamesEmpty));
                OnPropertyChanged(nameof(ShowSearchEmptySecret));
            }
        }
    }

    public bool ShowSecretPinGate => IsAllGamesPageActive && IsSecretTabActive && !IsSecretUnlocked;
    public bool ShowSecretLibraryContent => IsAllGamesPageActive && IsSecretTabActive && IsSecretUnlocked;

    public bool IsPinSetupMode
    {
        get => _isPinSetupMode;
        private set
        {
            if (SetProperty(ref _isPinSetupMode, value))
                OnPropertyChanged(nameof(IsPinUnlockMode));
        }
    }

    public bool IsPinUnlockMode => !IsPinSetupMode && !IsPasscodeCorrupted;

    public bool IsPasscodeCorrupted
    {
        get => _isPasscodeCorrupted;
        private set
        {
            if (SetProperty(ref _isPasscodeCorrupted, value))
                OnPropertyChanged(nameof(IsPinUnlockMode));
        }
    }

    public bool IsPinBusy
    {
        get => _isPinBusy;
        private set => SetProperty(ref _isPinBusy, value);
    }

    public string PinErrorMessage
    {
        get => _pinErrorMessage;
        private set => SetProperty(ref _pinErrorMessage, value);
    }

    public string SetupPin
    {
        get => _setupPin;
        set => SetProperty(ref _setupPin, value);
    }

    public string SetupConfirmPin
    {
        get => _setupConfirmPin;
        set => SetProperty(ref _setupConfirmPin, value);
    }

    public bool IsInPinCooldown => _cooldownUntilUtc.HasValue && _cooldownUntilUtc.Value > DateTimeOffset.UtcNow;

    public string StatusToast
    {
        get => _statusToast;
        set => SetProperty(ref _statusToast, value);
    }

    // Commands
    public RelayCommand GoToMainGamePageCommand { get; }
    public RelayCommand GoToAllGamesCommand { get; }
    public RelayCommand SelectPublicTabCommand { get; }
    public RelayCommand SelectSecretTabCommand { get; }
    public RelayCommand<GameItemViewModel> SelectSidebarGameCommand { get; }
    public RelayCommand OpenActiveGameFromCardCommand { get; }
    public AsyncRelayCommand PlayActiveGameCommand { get; }
    public RelayCommand OpenAddGameCommand { get; }
    public AsyncRelayCommand EditLocationCommand { get; }
    public AsyncRelayCommand EditLogoCommand { get; }
    public AsyncRelayCommand EditWallpaperCommand { get; }
    public AsyncRelayCommand DeleteActiveGameCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenSettingsSecretCommand { get; }
    public RelayCommand ClearPublicSearchCommand { get; }
    public RelayCommand ClearSecretSearchCommand { get; }
    public RelayCommand LockSecretCommand { get; }
    public AsyncRelayCommand CreateFirstPinCommand { get; }

    public MainViewModel(
        IGameLibraryService gameLibrary,
        ISearchService searchService,
        IAssetService assetService,
        IGameLaunchService launchService,
        ISecretAccessService secretAccess,
        ISettingsService settingsService,
        ILocalizationService localization,
        ImageCacheService imageCache,
        IAppLogger logger)
    {
        _gameLibrary = gameLibrary;
        _searchService = searchService;
        _assetService = assetService;
        _launchService = launchService;
        _secretAccess = secretAccess;
        _settingsService = settingsService;
        _localization = localization;
        _imageCache = imageCache;
        _logger = logger;

        GoToMainGamePageCommand = new RelayCommand(() =>
        {
            if (_secretAccess.IsUnlockedForBrowse)
                _secretAccess.Lock("navigated_to_public_home");
            ViewingSecretGamePage = false;
            ActiveView = ActiveShellView.GamePage;
        });

        GoToAllGamesCommand = new RelayCommand(() =>
        {
            if (ViewingSecretGamePage && _secretAccess.IsUnlockedForBrowse)
            {
                ActiveView = ActiveShellView.AllGamesPage;
                ActiveAllGamesTab = AllGamesTab.Secret;
            }
            else
            {
                ViewingSecretGamePage = false;
                ActiveView = ActiveShellView.AllGamesPage;
                ActiveAllGamesTab = AllGamesTab.Public;
            }
        });

        SelectPublicTabCommand = new RelayCommand(() => ActiveAllGamesTab = AllGamesTab.Public);
        SelectSecretTabCommand = new RelayCommand(() => ActiveAllGamesTab = AllGamesTab.Secret);

        SelectSidebarGameCommand = new RelayCommand<GameItemViewModel>(item =>
        {
            if (item is null) return;
            // Clicking any public sidebar icon leaves Secret cleanly
            if (_secretAccess.IsUnlockedForBrowse)
                _secretAccess.Lock("selected_public_sidebar_game");
            ViewingSecretGamePage = false;
            SelectedPublicGame = item;
            ActiveView = ActiveShellView.GamePage;
        });

        OpenActiveGameFromCardCommand = new RelayCommand(() =>
        {
            if (IsSecretTabActive && IsSecretUnlocked)
            {
                if (SelectedSecretGame is null) return;
                _secretAccess.RecordActivity();
                ViewingSecretGamePage = true;
                ActiveView = ActiveShellView.GamePage;
            }
            else
            {
                if (SelectedPublicGame is null) return;
                ViewingSecretGamePage = false;
                ActiveView = ActiveShellView.GamePage;
            }
        });

        PlayActiveGameCommand = new AsyncRelayCommand(LaunchActiveGameAsync, () => CanPlayActiveGame);
        OpenAddGameCommand = new RelayCommand(() => OpenAddGameRequested?.Invoke(this, EventArgs.Empty));
        EditLocationCommand = new AsyncRelayCommand(() => RunEditActionAsync(g => OpenEditLocationRequested?.Invoke(this, g)));
        EditLogoCommand = new AsyncRelayCommand(() => RunEditActionAsync(g => OpenEditLogoRequested?.Invoke(this, g)));
        EditWallpaperCommand = new AsyncRelayCommand(() => RunEditActionAsync(g => OpenEditWallpaperRequested?.Invoke(this, g)));
        DeleteActiveGameCommand = new AsyncRelayCommand(() => RunEditActionAsync(g => ConfirmDeleteGameRequested?.Invoke(this, g)));
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
        OpenSettingsSecretCommand = new RelayCommand(() => OpenSettingsSecretRequested?.Invoke(this, EventArgs.Empty));
        ClearPublicSearchCommand = new RelayCommand(() => PublicSearchQuery = string.Empty);
        ClearSecretSearchCommand = new RelayCommand(() => SecretSearchQuery = string.Empty);
        LockSecretCommand = new RelayCommand(() => _secretAccess.Lock("manual_button"));
        CreateFirstPinCommand = new AsyncRelayCommand(CreateFirstPinAsync);

        _gameLibrary.PublicLibraryChanged += (_, _) => _ = Application.Current.Dispatcher.InvokeAsync(() => ReloadPublicGamesAsync());
        _gameLibrary.SecretLibraryChanged += (_, _) => _ = Application.Current.Dispatcher.InvokeAsync(() => ReloadSecretGamesIfUnlockedAsync());
        _launchService.GameStateChanged += OnGameLaunchStateChanged;
        _secretAccess.ScopeChanged += OnSecretScopeChanged;
        _localization.LanguageChanged += (_, _) =>
        {
            NotifyPlayButtonChanged();
            UpdateCooldownMessage();
        };
        _settingsService.SettingsChanged += (_, _) =>
        {
            _ = LoadPersonalLogoAsync();
            Application.Current?.Dispatcher.InvokeAsync(RefreshActiveDisplay);
        };

        _inactivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _inactivityTimer.Tick += (_, _) => _secretAccess.CheckInactivityTimeout();
        _inactivityTimer.Start();

        _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _cooldownTimer.Tick += (_, _) => UpdateCooldownMessage();
    }

    public async Task InitializeAsync()
    {
        await LoadPersonalLogoAsync();
        await ReloadPublicGamesAsync(restoreSavedSelection: true);
        RefreshActiveDisplay();
    }

    public async Task LoadPersonalLogoAsync()
    {
        string path = await _assetService.ResolveAssetPathAsync(
            _settingsService.Current.AppLogoAssetId,
            AssetType.AppLogo,
            preferThumbnail: true);
        ImageSource? img = await _imageCache.LoadAsync(path, decodePixelWidth: 128);
        await Application.Current.Dispatcher.InvokeAsync(() => PersonalLogoImage = img);
    }

    public async Task ReloadPublicGamesAsync(bool restoreSavedSelection = false, string? selectGameId = null)
    {
        string? desiredId = selectGameId ??
                            (restoreSavedSelection ? _settingsService.Current.LastPublicSelectedGameId : SelectedPublicGame?.Id);

        IReadOnlyList<GameEntry> publicGames = await _gameLibrary.GetPublicGamesAsync();

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _allPublicCache.Clear();
            SidebarGames.Clear();

            foreach (GameEntry entry in publicGames)
            {
                var vm = new GameItemViewModel(
                    entry,
                    _assetService,
                    _imageCache,
                    _secretAccess,
                    _launchService.GetState(entry),
                    () => _settingsService.Current.DefaultWallpaperAssetId);
                _allPublicCache.Add(vm);
                SidebarGames.Add(vm);
                _ = vm.EnsureMediaLoadedAsync(0);
            }

            ApplyPublicFilter(desiredId);
            OnPropertyChanged(nameof(ShowEmptyGameState));
            OnPropertyChanged(nameof(ShowPublicAllGamesEmpty));
        });
    }

    public async Task ReloadSecretGamesIfUnlockedAsync(string? selectGameId = null)
    {
        if (!_secretAccess.HasScope(SecretScope.Browse))
        {
            await Application.Current.Dispatcher.InvokeAsync(ScrubSecretUiState);
            return;
        }

        long version = _secretAccess.SessionVersion;
        string? desiredId = selectGameId ?? SelectedSecretGame?.Id;

        IReadOnlyList<GameEntry> secretGames;
        try
        {
            secretGames = await _gameLibrary.GetSecretGamesAsync();
        }
        catch (SecretUnauthorizedException)
        {
            await Application.Current.Dispatcher.InvokeAsync(ScrubSecretUiState);
            return;
        }

        // Verify session wasn't locked while awaiting DB query
        if (!_secretAccess.HasScope(SecretScope.Browse) || _secretAccess.SessionVersion != version)
        {
            await Application.Current.Dispatcher.InvokeAsync(ScrubSecretUiState);
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!_secretAccess.HasScope(SecretScope.Browse) || _secretAccess.SessionVersion != version)
            {
                ScrubSecretUiState();
                return;
            }

            _allSecretCache.Clear();
            foreach (GameEntry entry in secretGames)
            {
                var vm = new GameItemViewModel(
                    entry,
                    _assetService,
                    _imageCache,
                    _secretAccess,
                    _launchService.GetState(entry),
                    () => _settingsService.Current.DefaultWallpaperAssetId);
                _allSecretCache.Add(vm);
                _ = vm.EnsureMediaLoadedAsync(version);
            }

            ApplySecretFilter(desiredId);
        });
    }

    public async Task SubmitSecretBrowsePinAsync(string pin)
    {
        if (IsPinBusy)
            return;

        IsPinBusy = true;
        PinErrorMessage = string.Empty;
        try
        {
            PinVerificationResult result = await _secretAccess.UnlockAsync(pin, SecretScope.Browse);
            HandlePinVerificationResult(result);
            if (result.Status == PinVerificationStatus.Success)
            {
                await ReloadSecretGamesIfUnlockedAsync();
                RefreshActiveDisplay();
            }
        }
        finally
        {
            IsPinBusy = false;
            SecretPinInputClearRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void HandleInvalidPinInput()
    {
        PinErrorMessage = _localization.GetString("Secret_PinFormatError");
    }

    private async Task CreateFirstPinAsync()
    {
        if (IsPinBusy)
            return;

        IsPinBusy = true;
        PinErrorMessage = string.Empty;
        try
        {
            var (success, errorKey) = await _secretAccess.SetupFirstPinAsync(SetupPin, SetupConfirmPin);
            if (!success)
            {
                PinErrorMessage = _localization.GetString(errorKey ?? "Secret_PinFormatError");
                return;
            }

            string pinToUnlock = SetupPin;
            SetupPin = string.Empty;
            SetupConfirmPin = string.Empty;
            IsPinSetupMode = false;

            // Automatically unlock Browse after successful initial setup
            PinVerificationResult unlock = await _secretAccess.UnlockAsync(pinToUnlock, SecretScope.Browse);
            if (unlock.Status == PinVerificationStatus.Success)
            {
                await ReloadSecretGamesIfUnlockedAsync();
                RefreshActiveDisplay();
            }
        }
        finally
        {
            IsPinBusy = false;
        }
    }

    public async Task RefreshSecretGateStateAsync()
    {
        PinErrorMessage = string.Empty;
        PasscodeHealthStatus health = await _secretAccess.GetPasscodeHealthAsync();
        IsPasscodeCorrupted = health == PasscodeHealthStatus.MissingOrCorrupted;
        IsPinSetupMode = health == PasscodeHealthStatus.NotConfigured;

        if (IsPasscodeCorrupted)
        {
            PinErrorMessage = _localization.GetString("Secret_PasscodeCorrupted");
            return;
        }

        if (!IsPinSetupMode && !IsSecretUnlocked)
        {
            SecurityAttemptSnapshot snapshot = await _secretAccess.GetAttemptSnapshotAsync();
            _cooldownUntilUtc = snapshot.CooldownUntilUtc;
            UpdateCooldownMessage();
            if (!IsInPinCooldown)
                SecretPinInputFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HandlePinVerificationResult(PinVerificationResult result)
    {
        switch (result.Status)
        {
            case PinVerificationStatus.Success:
                PinErrorMessage = string.Empty;
                _cooldownUntilUtc = null;
                _cooldownTimer.Stop();
                OnPropertyChanged(nameof(IsInPinCooldown));
                break;

            case PinVerificationStatus.InvalidFormat:
                PinErrorMessage = _localization.GetString("Secret_PinFormatError");
                break;

            case PinVerificationStatus.WrongPin:
                PinErrorMessage = _localization.Format("Secret_WrongPinFormat", result.RemainingAttempts);
                break;

            case PinVerificationStatus.LockedOut:
                _cooldownUntilUtc = result.CooldownUntilUtc;
                UpdateCooldownMessage();
                _cooldownTimer.Start();
                break;

            case PinVerificationStatus.PasscodeCorrupted:
                IsPasscodeCorrupted = true;
                PinErrorMessage = _localization.GetString("Secret_PasscodeCorrupted");
                break;
        }
    }

    private void UpdateCooldownMessage()
    {
        if (!_cooldownUntilUtc.HasValue)
        {
            _cooldownTimer.Stop();
            OnPropertyChanged(nameof(IsInPinCooldown));
            return;
        }

        TimeSpan remaining = _cooldownUntilUtc.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _cooldownUntilUtc = null;
            _cooldownTimer.Stop();
            PinErrorMessage = string.Empty;
            OnPropertyChanged(nameof(IsInPinCooldown));
            SecretPinInputFocusRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        OnPropertyChanged(nameof(IsInPinCooldown));
        string formatted = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
        PinErrorMessage = _localization.Format("Secret_LockedOutFormat", formatted);
    }

    private void OnSecretScopeChanged(object? sender, SecretScope scope)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            bool browseAllowed = scope is SecretScope.Browse or SecretScope.Manage;
            IsSecretUnlocked = browseAllowed;
            if (!browseAllowed)
            {
                ScrubSecretUiState();
            }
        });
    }

    /// <summary>
    /// Rule 8.2: On Secret lock, scrub ALL secret selection, search query, search results,
    /// title logo, and wallpaper from UI immediately and fall back to public selection or default!
    /// </summary>
    private void ScrubSecretUiState()
    {
        _secretSearchDebounceCts?.Cancel();
        _secretSearchQuery = string.Empty;
        OnPropertyChanged(nameof(SecretSearchQuery));
        OnPropertyChanged(nameof(HasSecretSearchQuery));

        foreach (GameItemViewModel item in _allSecretCache)
            item.ClearSecretMedia();
        _allSecretCache.Clear();
        FilteredSecretGames.Clear();

        if (_selectedSecretGame is not null)
            _selectedSecretGame.IsSelected = false;
        _selectedSecretGame = null;
        OnPropertyChanged(nameof(SelectedSecretGame));

        if (ViewingSecretGamePage)
            ViewingSecretGamePage = false;

        OnPropertyChanged(nameof(ActiveGame));
        OnPropertyChanged(nameof(HasActiveGame));
        OnPropertyChanged(nameof(HasFilteredSecretGames));
        OnPropertyChanged(nameof(ShowSecretAllGamesEmpty));
        OnPropertyChanged(nameof(ShowSearchEmptySecret));

        RefreshActiveDisplay();
    }

    private async Task LaunchActiveGameAsync()
    {
        GameItemViewModel? target = ActiveGame;
        if (target is null)
            return;

        if (target.IsSecret)
            _secretAccess.RecordActivity();

        GameLaunchResult result = await _launchService.LaunchAsync(target.Id);
        target.LaunchState = result.State;
        NotifyPlayButtonChanged();

        if (result.State == GameLaunchState.Missing)
            ShowToast(_localization.GetString("Game_MissingBanner"));
        else if (result.State == GameLaunchState.Error && !string.IsNullOrWhiteSpace(result.ErrorMessage))
            ShowToast($"{_localization.GetString("Play_Error")}: {result.ErrorMessage}");
    }

    private async Task RunEditActionAsync(Action<GameItemViewModel> action)
    {
        GameItemViewModel? target = ActiveGame;
        if (target is null)
            return;

        // Rule 8.2: Editing/managing a Secret game from Browse view MUST prompt for SecretManage authentication!
        if (target.IsSecret && !_secretAccess.HasScope(SecretScope.Manage))
        {
            if (RequestSecretManageElevationAsync is null)
                return;

            bool granted = await RequestSecretManageElevationAsync.Invoke();
            if (!granted || !_secretAccess.HasScope(SecretScope.Manage))
                return;
        }

        if (target.IsSecret)
            _secretAccess.RecordActivity();

        action(target);
    }

    private void OnGameLaunchStateChanged(object? sender, (string GameId, GameLaunchState State) args)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            GameItemViewModel? item = _allPublicCache.FirstOrDefault(g => g.Id == args.GameId)
                ?? _allSecretCache.FirstOrDefault(g => g.Id == args.GameId);
            if (item is not null)
            {
                item.LaunchState = args.State;
                if (ReferenceEquals(item, ActiveGame))
                    NotifyPlayButtonChanged();
            }
        });
    }

    private void SchedulePublicSearch()
    {
        _publicSearchDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _publicSearchDebounceCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(175, cts.Token);
                await Application.Current.Dispatcher.InvokeAsync(() => ApplyPublicFilter(SelectedPublicGame?.Id));
            }
            catch (TaskCanceledException) { }
        });
    }

    private void ScheduleSecretSearch()
    {
        _secretSearchDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _secretSearchDebounceCts = cts;
        long version = _secretAccess.SessionVersion;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(175, cts.Token);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!_secretAccess.HasScope(SecretScope.Browse) || _secretAccess.SessionVersion != version)
                        return;
                    ApplySecretFilter(SelectedSecretGame?.Id);
                });
            }
            catch (TaskCanceledException) { }
        });
    }

    private void ApplyPublicFilter(string? preferredId)
    {
        IReadOnlyList<GameEntry> matchedEntries = _searchService.FilterInMemory(
            _allPublicCache.Select(x => x.Model).ToList(),
            PublicSearchQuery);
        var matchedIds = matchedEntries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        FilteredPublicGames.Clear();
        foreach (GameItemViewModel vm in _allPublicCache)
        {
            if (matchedIds.Contains(vm.Id))
                FilteredPublicGames.Add(vm);
        }

        // Rule 4.5: If currently selected game is filtered out, pick first match or default backdrop when empty!
        if (IsAllGamesPageActive && IsPublicTabActive)
        {
            GameItemViewModel? next = FilteredPublicGames.FirstOrDefault(g => g.Id == preferredId)
                ?? FilteredPublicGames.FirstOrDefault();
            SelectedPublicGame = next;
        }
        else
        {
            GameItemViewModel? next = _allPublicCache.FirstOrDefault(g => g.Id == preferredId)
                ?? _allPublicCache.FirstOrDefault();
            SelectedPublicGame = next;
        }

        OnPropertyChanged(nameof(HasFilteredPublicGames));
        OnPropertyChanged(nameof(ShowPublicAllGamesEmpty));
        OnPropertyChanged(nameof(ShowSearchEmptyPublic));
    }

    private void ApplySecretFilter(string? preferredId)
    {
        if (!_secretAccess.HasScope(SecretScope.Browse))
            return;

        IReadOnlyList<GameEntry> matchedEntries = _searchService.FilterInMemory(
            _allSecretCache.Select(x => x.Model).ToList(),
            SecretSearchQuery);
        var matchedIds = matchedEntries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        FilteredSecretGames.Clear();
        foreach (GameItemViewModel vm in _allSecretCache)
        {
            if (matchedIds.Contains(vm.Id))
                FilteredSecretGames.Add(vm);
        }

        GameItemViewModel? next = FilteredSecretGames.FirstOrDefault(g => g.Id == preferredId)
            ?? FilteredSecretGames.FirstOrDefault();
        SelectedSecretGame = next;

        OnPropertyChanged(nameof(HasFilteredSecretGames));
        OnPropertyChanged(nameof(ShowSecretAllGamesEmpty));
        OnPropertyChanged(nameof(ShowSearchEmptySecret));
    }

    /// <summary>
    /// Updates active wallpaper and title logo/text with strict cancellation (T06)
    /// and Secret SessionVersion verification (T19) so stale loads NEVER overwrite current UI.
    /// </summary>
    public void RefreshActiveDisplay()
    {
        _wallpaperCts?.Cancel();
        var cts = new CancellationTokenSource();
        _wallpaperCts = cts;

        // If on Secret tab while locked, NEVER show a Secret wallpaper — show default wallpaper cleanly!
        bool lockedSecretView = IsAllGamesPageActive && IsSecretTabActive && !IsSecretUnlocked;
        GameItemViewModel? game = lockedSecretView ? null : ActiveGame;

        bool isSecretContext = game is not null && game.IsSecret;
        long secretVersion = _secretAccess.SessionVersion;

        ActiveTitleText = game?.DisplayName ?? string.Empty;
        NotifyPlayButtonChanged();

        _ = LoadWallpaperAndTitleAsync(game?.Model, isSecretContext, secretVersion, cts.Token);
    }

    private async Task LoadWallpaperAndTitleAsync(
        GameEntry? game,
        bool isSecretContext,
        long expectedSecretVersion,
        CancellationToken ct)
    {
        try
        {
            string? effectiveWpAssetId = !string.IsNullOrWhiteSpace(game?.WallpaperAssetId)
                ? game!.WallpaperAssetId
                : _settingsService.Current.DefaultWallpaperAssetId;

            string wpPath = await _assetService.ResolveAssetPathAsync(effectiveWpAssetId, AssetType.Wallpaper, preferThumbnail: false, ct);
            bool isLiveVideo = MediaFormats.IsVideoPath(wpPath) && System.IO.File.Exists(wpPath);

            // Load static image (either the static wallpaper itself, or an instant static poster frame behind live video!)
            string staticPath;
            if (isLiveVideo)
            {
                staticPath = game is not null
                    ? await _assetService.ResolveCardCoverImagePathAsync(game, _settingsService.Current.DefaultWallpaperAssetId, ct)
                    : await _assetService.ResolveAssetPathAsync(effectiveWpAssetId, AssetType.Wallpaper, preferThumbnail: true, ct);
            }
            else
            {
                staticPath = wpPath;
            }

            string? titlePath = game is not null && !string.IsNullOrWhiteSpace(game.TitleLogoAssetId)
                ? await _assetService.ResolveAssetPathAsync(game.TitleLogoAssetId, AssetType.TitleLogo, preferThumbnail: false, ct)
                : null;

            ImageSource? wpStatic = await _imageCache.LoadAsync(staticPath, decodePixelWidth: 1920, ct);
            ImageSource? title = !string.IsNullOrWhiteSpace(titlePath)
                ? await _imageCache.LoadAsync(titlePath, decodePixelWidth: 700, ct)
                : null;

            if (ct.IsCancellationRequested)
                return;

            // Final stale-session check before applying to UI!
            if (isSecretContext && (!_secretAccess.HasScope(SecretScope.Browse) || _secretAccess.SessionVersion != expectedSecretVersion))
                return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested)
                    return;
                if (isSecretContext && (!_secretAccess.HasScope(SecretScope.Browse) || _secretAccess.SessionVersion != expectedSecretVersion))
                    return;

                ActiveTitleLogo = title;
                CurrentWallpaper = wpStatic;
                WallpaperMediaChanged?.Invoke(this, (wpStatic, isLiveVideo ? wpPath : null));
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn("UI", $"Failed to load active wallpaper/title: {ex.Message}");
        }
    }

    public void ShowToast(string message)
    {
        StatusToast = message;
        _ = Task.Run(async () =>
        {
            await Task.Delay(4000);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (StatusToast == message)
                    StatusToast = string.Empty;
            });
        });
    }

    private void NotifyPlayButtonChanged()
    {
        OnPropertyChanged(nameof(UseOriginalVietnamesePlayAsset));
        OnPropertyChanged(nameof(ShowDynamicPlayOverlayText));
        OnPropertyChanged(nameof(PlayButtonOverlayText));
        OnPropertyChanged(nameof(CanPlayActiveGame));
        OnPropertyChanged(nameof(IsActiveGameMissing));
        PlayActiveGameCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _inactivityTimer.Stop();
        _cooldownTimer.Stop();
        _wallpaperCts?.Cancel();
        _publicSearchDebounceCts?.Cancel();
        _secretSearchDebounceCts?.Cancel();
    }
}

public sealed class RelayCommand<T> : System.Windows.Input.ICommand
{
    private readonly Action<T?> _execute;
    private readonly Predicate<T?>? _canExecute;

    public RelayCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter is T t ? t : default) ?? true;
    public void Execute(object? parameter) => _execute(parameter is T t ? t : default);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
