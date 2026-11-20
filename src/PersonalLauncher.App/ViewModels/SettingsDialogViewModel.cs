using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Threading;
using PersonalLauncher.App.Services;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.App.ViewModels;

public enum SettingsSection
{
    General,
    Appearance,
    Secret,
    About
}

public sealed record LanguageChoice(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class SettingsDialogViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localization;
    private readonly IStartupService _startupService;
    private readonly IAssetService _assetService;
    private readonly IAppPaths _paths;
    private readonly IGameLibraryService _gameLibrary;
    private readonly ISecretAccessService _secretAccess;
    private readonly IMailOutboxService _mailOutbox;
    private readonly ImageCacheService _imageCache;
    private readonly DispatcherTimer _cooldownTimer;

    private SettingsSection _activeSection = SettingsSection.General;
    private LanguageChoice _selectedLanguage;
    private bool _startWithWindows;
    private bool _isStartupDisabledByWindows;
    private string _startupError = string.Empty;
    private bool _closeToTray;
    private bool _suppressStartupSetter;

    // Appearance
    private ImageSource? _personalLogoPreview;
    private ImageSource? _defaultIconPreview;
    private ImageSource? _defaultWallpaperPreview;
    private string _appearanceMessage = string.Empty;

    // Secret Management (ALWAYS requires fresh SecretManage auth!)
    private bool _isManageAuthenticated;
    private bool _isPinSetupMode;
    private bool _isPasscodeCorrupted;
    private bool _isPinBusy;
    private string _secretAuthError = string.Empty;
    private string _setupPin = string.Empty;
    private string _setupConfirmPin = string.Empty;
    private DateTimeOffset? _cooldownUntilUtc;

    // Change PIN
    private string _currentPinInput = string.Empty;
    private string _newPinInput = string.Empty;
    private string _confirmNewPinInput = string.Empty;
    private string _changePinStatus = string.Empty;

    // Mail Settings
    private string _recipientAddress = string.Empty;
    private string _smtpHost = string.Empty;
    private string _smtpPortText = "587";
    private MailTlsMode _tlsMode = MailTlsMode.StartTls;
    private string _senderAddress = string.Empty;
    private string _senderDisplayName = "PersonalLauncher";
    private MailAuthMethod _authMethod = MailAuthMethod.PasswordOrAppPassword;
    private bool _hasStoredMailCredential;
    private string _mailStatusSummary = string.Empty;
    private string _mailFeedbackMessage = string.Empty;
    private bool _isMailBusy;

    public ObservableCollection<LanguageChoice> Languages { get; } = new();
    public ObservableCollection<GameEntry> PublicGames { get; } = new();
    public ObservableCollection<GameEntry> SecretGames { get; } = new();

    public event EventHandler? SecretPinInputClearRequested;
    public event EventHandler? SecretPinInputFocusRequested;
    public event EventHandler? OpenAddSecretGameRequested;
    public event Func<string?>? RetrieveMailCredentialPassword;
    public event Action? ClearMailCredentialPasswordBox;

    public SettingsSection ActiveSection
    {
        get => _activeSection;
        set
        {
            if (SetProperty(ref _activeSection, value))
            {
                OnPropertyChanged(nameof(IsGeneralActive));
                OnPropertyChanged(nameof(IsAppearanceActive));
                OnPropertyChanged(nameof(IsSecretActive));
                OnPropertyChanged(nameof(IsAboutActive));

                if (value == SettingsSection.Secret)
                {
                    // Rule 8.1 #5: Entering Settings -> Secret ALWAYS requires fresh Manage PIN verification!
                    _isManageAuthenticated = false;
                    OnPropertyChanged(nameof(IsManageAuthenticated));
                    OnPropertyChanged(nameof(ShowSecretManageGate));
                    _ = PrepareSecretManageGateAsync();
                }
                else if (_isManageAuthenticated)
                {
                    // Leaving Settings -> Secret revokes Manage session cleanly
                    _isManageAuthenticated = false;
                    OnPropertyChanged(nameof(IsManageAuthenticated));
                    OnPropertyChanged(nameof(ShowSecretManageGate));
                    _secretAccess.Lock("left_settings_secret");
                }
            }
        }
    }

    public bool IsGeneralActive => ActiveSection == SettingsSection.General;
    public bool IsAppearanceActive => ActiveSection == SettingsSection.Appearance;
    public bool IsSecretActive => ActiveSection == SettingsSection.Secret;
    public bool IsAboutActive => ActiveSection == SettingsSection.About;

    // General properties
    public LanguageChoice SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is not null && SetProperty(ref _selectedLanguage, value))
            {
                _localization.SetLanguage(value.Code);
                _ = _settingsService.UpdateAsync(s => s with { Language = value.Code });
                _ = RefreshMailStatusSummaryAsync();
            }
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_suppressStartupSetter)
            {
                SetProperty(ref _startWithWindows, value);
                return;
            }

            if (SetProperty(ref _startWithWindows, value))
                ApplyStartupToggle(value);
        }
    }

    public bool IsStartupDisabledByWindows
    {
        get => _isStartupDisabledByWindows;
        private set => SetProperty(ref _isStartupDisabledByWindows, value);
    }

    public string StartupError
    {
        get => _startupError;
        private set => SetProperty(ref _startupError, value);
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (SetProperty(ref _closeToTray, value))
            {
                OnPropertyChanged(nameof(CloseExitApp));
                _ = _settingsService.UpdateAsync(s => s with
                {
                    CloseAction = value ? CloseAction.MinimizeToTray : CloseAction.ExitApplication
                });
            }
        }
    }

    public bool CloseExitApp
    {
        get => !_closeToTray;
        set
        {
            if (value)
                CloseToTray = false;
        }
    }

    // Appearance properties
    public ImageSource? PersonalLogoPreview
    {
        get => _personalLogoPreview;
        private set => SetProperty(ref _personalLogoPreview, value);
    }

    public ImageSource? DefaultIconPreview
    {
        get => _defaultIconPreview;
        private set => SetProperty(ref _defaultIconPreview, value);
    }

    public ImageSource? DefaultWallpaperPreview
    {
        get => _defaultWallpaperPreview;
        private set => SetProperty(ref _defaultWallpaperPreview, value);
    }

    public string AppearanceMessage
    {
        get => _appearanceMessage;
        private set => SetProperty(ref _appearanceMessage, value);
    }

    // Secret Management properties
    public bool IsManageAuthenticated
    {
        get => _isManageAuthenticated;
        private set
        {
            if (SetProperty(ref _isManageAuthenticated, value))
                OnPropertyChanged(nameof(ShowSecretManageGate));
        }
    }

    public bool ShowSecretManageGate => IsSecretActive && !IsManageAuthenticated;

    public bool IsPinSetupMode
    {
        get => _isPinSetupMode;
        private set
        {
            if (SetProperty(ref _isPinSetupMode, value))
                OnPropertyChanged(nameof(IsPinUnlockMode));
        }
    }

    public bool IsPasscodeCorrupted
    {
        get => _isPasscodeCorrupted;
        private set
        {
            if (SetProperty(ref _isPasscodeCorrupted, value))
                OnPropertyChanged(nameof(IsPinUnlockMode));
        }
    }

    public bool IsPinUnlockMode => !IsPinSetupMode && !IsPasscodeCorrupted;

    public bool IsPinBusy
    {
        get => _isPinBusy;
        private set => SetProperty(ref _isPinBusy, value);
    }

    public string SecretAuthError
    {
        get => _secretAuthError;
        private set => SetProperty(ref _secretAuthError, value);
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

    // Change PIN
    public string CurrentPinInput
    {
        get => _currentPinInput;
        set => SetProperty(ref _currentPinInput, value);
    }

    public string NewPinInput
    {
        get => _newPinInput;
        set => SetProperty(ref _newPinInput, value);
    }

    public string ConfirmNewPinInput
    {
        get => _confirmNewPinInput;
        set => SetProperty(ref _confirmNewPinInput, value);
    }

    public string ChangePinStatus
    {
        get => _changePinStatus;
        private set => SetProperty(ref _changePinStatus, value);
    }

    // Mail Settings
    public string RecipientAddress
    {
        get => _recipientAddress;
        set => SetProperty(ref _recipientAddress, value);
    }

    public string SmtpHost
    {
        get => _smtpHost;
        set => SetProperty(ref _smtpHost, value);
    }

    public string SmtpPortText
    {
        get => _smtpPortText;
        set => SetProperty(ref _smtpPortText, value);
    }

    public MailTlsMode TlsMode
    {
        get => _tlsMode;
        set => SetProperty(ref _tlsMode, value);
    }

    public MailTlsMode[] AvailableTlsModes { get; } = { MailTlsMode.StartTls, MailTlsMode.SslOnConnect };

    public string SenderAddress
    {
        get => _senderAddress;
        set => SetProperty(ref _senderAddress, value);
    }

    public string SenderDisplayName
    {
        get => _senderDisplayName;
        set => SetProperty(ref _senderDisplayName, value);
    }

    public MailAuthMethod AuthMethod
    {
        get => _authMethod;
        set => SetProperty(ref _authMethod, value);
    }

    public MailAuthMethod[] AvailableAuthMethods { get; } = { MailAuthMethod.PasswordOrAppPassword, MailAuthMethod.OAuth2AccessToken };

    public bool HasStoredMailCredential
    {
        get => _hasStoredMailCredential;
        private set => SetProperty(ref _hasStoredMailCredential, value);
    }

    public string MailStatusSummary
    {
        get => _mailStatusSummary;
        private set => SetProperty(ref _mailStatusSummary, value);
    }

    public string MailFeedbackMessage
    {
        get => _mailFeedbackMessage;
        private set => SetProperty(ref _mailFeedbackMessage, value);
    }

    public bool IsMailBusy
    {
        get => _isMailBusy;
        private set
        {
            if (SetProperty(ref _isMailBusy, value))
            {
                SaveMailConfigCommand.RaiseCanExecuteChanged();
                SendTestEmailCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // About info (strictly personal launcher metadata — zero HoYoPlay metadata!)
    public string AppTitle => "PersonalLauncher";
    public string AppVersionText { get; }
    public string AppAuthorText => "CuongAnime";
    public string DataDirectoryPath => _paths.DataRootDirectory;
    public string LibrariesUsedText => ".NET 10 LTS (WPF), Microsoft.Data.Sqlite, MailKit / MimeKit, LibVLCSharp / LibVLC, Konscious.Security.Cryptography.Argon2, Windows DPAPI";

    // Commands
    public RelayCommand SelectGeneralCommand { get; }
    public RelayCommand SelectAppearanceCommand { get; }
    public RelayCommand SelectSecretCommand { get; }
    public RelayCommand SelectAboutCommand { get; }
    public AsyncRelayCommand ResetPersonalLogoCommand { get; }
    public AsyncRelayCommand ResetDefaultWallpaperCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public AsyncRelayCommand CreateFirstPinCommand { get; }
    public AsyncRelayCommand<GameEntry> HideGameCommand { get; }
    public AsyncRelayCommand<GameEntry> UnhideGameCommand { get; }
    public RelayCommand AddDirectSecretGameCommand { get; }
    public AsyncRelayCommand ChangePinCommand { get; }
    public AsyncRelayCommand SaveMailConfigCommand { get; }
    public AsyncRelayCommand SendTestEmailCommand { get; }

    public SettingsDialogViewModel(
        ISettingsService settingsService,
        ILocalizationService localization,
        IStartupService startupService,
        IAssetService assetService,
        IAppPaths paths,
        IGameLibraryService gameLibrary,
        ISecretAccessService secretAccess,
        IMailOutboxService mailOutbox,
        ImageCacheService imageCache,
        SettingsSection initialSection = SettingsSection.General)
    {
        _settingsService = settingsService;
        _localization = localization;
        _startupService = startupService;
        _assetService = assetService;
        _paths = paths;
        _gameLibrary = gameLibrary;
        _secretAccess = secretAccess;
        _mailOutbox = mailOutbox;
        _imageCache = imageCache;

        Assembly asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        string? infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        AppVersionText = string.IsNullOrWhiteSpace(infoVer) ? (asm.GetName().Version?.ToString() ?? "1.0.0") : infoVer;

        foreach (var (code, name) in _localization.SupportedLanguages)
            Languages.Add(new LanguageChoice(code, name));

        _selectedLanguage = Languages.FirstOrDefault(l => string.Equals(l.Code, _settingsService.Current.Language, StringComparison.OrdinalIgnoreCase))
                            ?? Languages[0];

        _closeToTray = _settingsService.Current.CloseAction == CloseAction.MinimizeToTray;
        RefreshActualStartupState();

        SelectGeneralCommand = new RelayCommand(() => ActiveSection = SettingsSection.General);
        SelectAppearanceCommand = new RelayCommand(() => ActiveSection = SettingsSection.Appearance);
        SelectSecretCommand = new RelayCommand(() => ActiveSection = SettingsSection.Secret);
        SelectAboutCommand = new RelayCommand(() => ActiveSection = SettingsSection.About);
        ResetPersonalLogoCommand = new AsyncRelayCommand(ResetPersonalLogoAsync);
        ResetDefaultWallpaperCommand = new AsyncRelayCommand(ResetDefaultWallpaperAsync);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        CreateFirstPinCommand = new AsyncRelayCommand(CreateFirstPinAsync);
        HideGameCommand = new AsyncRelayCommand<GameEntry>(g => MoveGameVisibilityAsync(g, GameVisibility.Secret));
        UnhideGameCommand = new AsyncRelayCommand<GameEntry>(g => MoveGameVisibilityAsync(g, GameVisibility.Public));
        AddDirectSecretGameCommand = new RelayCommand(() => OpenAddSecretGameRequested?.Invoke(this, EventArgs.Empty));
        ChangePinCommand = new AsyncRelayCommand(ChangePinAsync);
        SaveMailConfigCommand = new AsyncRelayCommand(SaveMailConfigAsync, () => !IsMailBusy);
        SendTestEmailCommand = new AsyncRelayCommand(SendTestEmailAsync, () => !IsMailBusy);

        _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _cooldownTimer.Tick += (_, _) => UpdateCooldownText();

        _ = LoadAppearancePreviewsAsync();

        if (initialSection != SettingsSection.General)
            ActiveSection = initialSection;
    }

    private void RefreshActualStartupState()
    {
        StartupStatus status = _startupService.GetStatus();
        _suppressStartupSetter = true;
        try
        {
            IsStartupDisabledByWindows = status == StartupStatus.DisabledByWindows;
            StartWithWindows = status == StartupStatus.Enabled;
        }
        finally
        {
            _suppressStartupSetter = false;
        }
    }

    private void ApplyStartupToggle(bool desired)
    {
        StartupError = string.Empty;
        var (success, resultingStatus, error) = _startupService.SetEnabled(desired);
        if (!success)
        {
            StartupError = error ?? "Failed to update Windows startup setting.";
        }

        _suppressStartupSetter = true;
        try
        {
            IsStartupDisabledByWindows = resultingStatus == StartupStatus.DisabledByWindows;
            StartWithWindows = resultingStatus == StartupStatus.Enabled;
        }
        finally
        {
            _suppressStartupSetter = false;
        }

        _ = _settingsService.UpdateAsync(s => s with { StartWithWindows = StartWithWindows });
    }

    private async Task LoadAppearancePreviewsAsync()
    {
        string appLogo = await _assetService.ResolveAssetPathAsync(_settingsService.Current.AppLogoAssetId, AssetType.AppLogo);
        string defaultWp = await _assetService.ResolveAssetPathAsync(
            _settingsService.Current.DefaultWallpaperAssetId,
            AssetType.Wallpaper,
            preferThumbnail: true);
        PersonalLogoPreview = await _imageCache.LoadAsync(appLogo, decodePixelWidth: 160);
        DefaultIconPreview = await _imageCache.LoadAsync(_paths.DefaultGameIconPath, decodePixelWidth: 128);
        DefaultWallpaperPreview = await _imageCache.LoadAsync(defaultWp, decodePixelWidth: 420);
    }

    public async Task ChangePersonalLogoAsync(string sourcePath)
    {
        AppearanceMessage = string.Empty;
        try
        {
            AssetRecord asset = await _assetService.ImportAppLogoAsync(sourcePath);
            await _settingsService.UpdateAsync(s => s with { AppLogoAssetId = asset.Id });
            await _assetService.CleanupUnreferencedAssetsAsync(asset.Id, _settingsService.Current.DefaultWallpaperAssetId);
            await LoadAppearancePreviewsAsync();
        }
        catch (Exception ex)
        {
            AppearanceMessage = ex.Message;
        }
    }

    private async Task ResetPersonalLogoAsync()
    {
        AppearanceMessage = string.Empty;
        await _settingsService.UpdateAsync(s => s with { AppLogoAssetId = null });
        await _assetService.CleanupUnreferencedAssetsAsync(null, _settingsService.Current.DefaultWallpaperAssetId);
        await LoadAppearancePreviewsAsync();
    }

    public async Task ChangeDefaultWallpaperAsync(string sourcePath)
    {
        AppearanceMessage = string.Empty;
        try
        {
            AssetRecord asset = await _assetService.ImportDefaultWallpaperAsync(sourcePath);
            await _settingsService.UpdateAsync(s => s with { DefaultWallpaperAssetId = asset.Id });
            await _assetService.CleanupUnreferencedAssetsAsync(_settingsService.Current.AppLogoAssetId, asset.Id);
            await LoadAppearancePreviewsAsync();
        }
        catch (Exception ex)
        {
            AppearanceMessage = ex.Message;
        }
    }

    private async Task ResetDefaultWallpaperAsync()
    {
        AppearanceMessage = string.Empty;
        await _settingsService.UpdateAsync(s => s with { DefaultWallpaperAssetId = null });
        await _assetService.CleanupUnreferencedAssetsAsync(_settingsService.Current.AppLogoAssetId, null);
        await LoadAppearancePreviewsAsync();
    }

    private void OpenDataFolder()
    {
        _paths.EnsureDirectoriesExist();
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { _paths.DataRootDirectory },
            UseShellExecute = false
        });
    }

    private async Task PrepareSecretManageGateAsync()
    {
        SecretAuthError = string.Empty;
        PasscodeHealthStatus health = await _secretAccess.GetPasscodeHealthAsync();
        IsPasscodeCorrupted = health == PasscodeHealthStatus.MissingOrCorrupted;
        IsPinSetupMode = health == PasscodeHealthStatus.NotConfigured;

        if (IsPasscodeCorrupted)
        {
            SecretAuthError = _localization.GetString("Secret_PasscodeCorrupted");
            return;
        }

        if (!IsPinSetupMode)
        {
            SecurityAttemptSnapshot snapshot = await _secretAccess.GetAttemptSnapshotAsync();
            _cooldownUntilUtc = snapshot.CooldownUntilUtc;
            UpdateCooldownText();
            if (!IsInPinCooldown)
                SecretPinInputFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void HandleInvalidPinInput()
    {
        SecretAuthError = _localization.GetString("Secret_PinFormatError");
    }

    public async Task SubmitManagePinAsync(string pin)
    {
        if (IsPinBusy)
            return;

        IsPinBusy = true;
        SecretAuthError = string.Empty;
        try
        {
            PinVerificationResult res = await _secretAccess.UnlockAsync(pin, SecretScope.Manage);
            if (res.Status == PinVerificationStatus.Success)
            {
                _cooldownUntilUtc = null;
                _cooldownTimer.Stop();
                OnPropertyChanged(nameof(IsInPinCooldown));
                IsManageAuthenticated = true;
                await LoadSecretManageContentAsync();
            }
            else if (res.Status == PinVerificationStatus.WrongPin)
            {
                SecretAuthError = _localization.Format("Secret_WrongPinFormat", res.RemainingAttempts);
            }
            else if (res.Status == PinVerificationStatus.LockedOut)
            {
                _cooldownUntilUtc = res.CooldownUntilUtc;
                UpdateCooldownText();
                _cooldownTimer.Start();
            }
            else if (res.Status == PinVerificationStatus.PasscodeCorrupted)
            {
                IsPasscodeCorrupted = true;
                SecretAuthError = _localization.GetString("Secret_PasscodeCorrupted");
            }
            else if (res.Status == PinVerificationStatus.InvalidFormat)
            {
                SecretAuthError = _localization.GetString("Secret_PinFormatError");
            }
        }
        finally
        {
            IsPinBusy = false;
            SecretPinInputClearRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task CreateFirstPinAsync()
    {
        if (IsPinBusy)
            return;

        IsPinBusy = true;
        SecretAuthError = string.Empty;
        try
        {
            var (success, errorKey) = await _secretAccess.SetupFirstPinAsync(SetupPin, SetupConfirmPin);
            if (!success)
            {
                SecretAuthError = _localization.GetString(errorKey ?? "Secret_PinFormatError");
                return;
            }

            string createdPin = SetupPin;
            SetupPin = string.Empty;
            SetupConfirmPin = string.Empty;
            IsPinSetupMode = false;

            PinVerificationResult unlock = await _secretAccess.UnlockAsync(createdPin, SecretScope.Manage);
            if (unlock.Status == PinVerificationStatus.Success)
            {
                IsManageAuthenticated = true;
                await LoadSecretManageContentAsync();
            }
        }
        finally
        {
            IsPinBusy = false;
        }
    }

    public async Task LoadSecretManageContentAsync()
    {
        if (!IsManageAuthenticated || !_secretAccess.HasScope(SecretScope.Manage))
            return;

        await ReloadManageGameListsAsync();

        MailSettingsRecord mail = await _mailOutbox.GetSettingsAsync();
        RecipientAddress = mail.RecipientAddress;
        SmtpHost = mail.SmtpHost;
        SmtpPortText = mail.SmtpPort.ToString();
        TlsMode = mail.TlsMode;
        SenderAddress = mail.SenderAddress;
        SenderDisplayName = mail.SenderDisplayName;
        AuthMethod = mail.AuthMethod;

        await RefreshMailStatusSummaryAsync();
    }

    public async Task ReloadManageGameListsAsync()
    {
        if (!_secretAccess.HasScope(SecretScope.Manage))
            return;

        IReadOnlyList<GameEntry> pub = await _gameLibrary.GetPublicGamesAsync();
        IReadOnlyList<GameEntry> sec = await _gameLibrary.GetSecretGamesAsync();

        PublicGames.Clear();
        foreach (GameEntry g in pub) PublicGames.Add(g);

        SecretGames.Clear();
        foreach (GameEntry g in sec) SecretGames.Add(g);
    }

    private async Task MoveGameVisibilityAsync(GameEntry? game, GameVisibility target)
    {
        if (game is null || !_secretAccess.HasScope(SecretScope.Manage))
            return;

        await _gameLibrary.SetGameVisibilityAsync(game.Id, target);
        await ReloadManageGameListsAsync();
    }

    private async Task ChangePinAsync()
    {
        ChangePinStatus = string.Empty;
        try
        {
            PinVerificationResult res = await _secretAccess.ChangePinAsync(CurrentPinInput, NewPinInput, ConfirmNewPinInput);
            if (res.Status == PinVerificationStatus.Success)
            {
                CurrentPinInput = string.Empty;
                NewPinInput = string.Empty;
                ConfirmNewPinInput = string.Empty;
                ChangePinStatus = _localization.GetString("Secret_PinChangedSuccess");
            }
            else if (res.Status == PinVerificationStatus.InvalidFormat)
            {
                ChangePinStatus = _localization.GetString(res.ErrorMessage ?? "Secret_PinFormatError");
            }
            else if (res.Status == PinVerificationStatus.WrongPin)
            {
                CurrentPinInput = string.Empty;
                ChangePinStatus = _localization.Format("Secret_WrongPinFormat", res.RemainingAttempts);
            }
            else if (res.Status == PinVerificationStatus.LockedOut)
            {
                IsManageAuthenticated = false;
                _cooldownUntilUtc = res.CooldownUntilUtc;
                UpdateCooldownText();
                _cooldownTimer.Start();
            }
        }
        catch (Exception ex)
        {
            ChangePinStatus = ex.Message;
        }
    }

    private async Task SaveMailConfigAsync()
    {
        MailFeedbackMessage = string.Empty;
        if (!int.TryParse(SmtpPortText, out int port) || port is <= 0 or > 65535)
        {
            MailFeedbackMessage = "SMTP Port must be between 1 and 65535.";
            return;
        }

        string trimmedRecipient = RecipientAddress.Trim();
        if (!string.IsNullOrEmpty(trimmedRecipient) && !IsValidEmail(trimmedRecipient))
        {
            MailFeedbackMessage = _localization.GetString("Mail_InvalidRecipient");
            return;
        }

        if (string.IsNullOrEmpty(trimmedRecipient) &&
            (!string.IsNullOrWhiteSpace(SmtpHost) || !string.IsNullOrWhiteSpace(SenderAddress)))
        {
            MailFeedbackMessage = _localization.GetString("Mail_InvalidRecipient");
            return;
        }

        IsMailBusy = true;
        try
        {
            string? newSecret = RetrieveMailCredentialPassword?.Invoke();
            var settings = new MailSettingsRecord
            {
                SmtpHost = SmtpHost.Trim(),
                SmtpPort = port,
                TlsMode = TlsMode,
                SenderAddress = SenderAddress.Trim(),
                SenderDisplayName = string.IsNullOrWhiteSpace(SenderDisplayName) ? "PersonalLauncher" : SenderDisplayName.Trim(),
                AuthMethod = AuthMethod,
                RecipientAddress = trimmedRecipient
            };

            await _mailOutbox.SaveSettingsAsync(settings, string.IsNullOrEmpty(newSecret) ? null : newSecret);
            ClearMailCredentialPasswordBox?.Invoke();
            await RefreshMailStatusSummaryAsync();
            MailFeedbackMessage = _localization.GetString("Mail_ConfigSaved");
        }
        catch (Exception ex)
        {
            MailFeedbackMessage = ex.Message;
        }
        finally
        {
            IsMailBusy = false;
        }
    }

    private async Task SendTestEmailAsync()
    {
        MailFeedbackMessage = string.Empty;
        IsMailBusy = true;
        try
        {
            MailSendResult result = await _mailOutbox.SendTestEmailAsync();
            MailFeedbackMessage = result.Outcome switch
            {
                MailOutboxStatus.Accepted => _localization.GetString("Mail_TestSuccess"),
                MailOutboxStatus.Unknown => $"{_localization.GetString("Mail_StatusUnknown")}: {result.ErrorSummary}",
                _ => $"{_localization.GetString("Mail_StatusFailed")}: {result.ErrorSummary}"
            };
            await RefreshMailStatusSummaryAsync();
        }
        catch (Exception ex)
        {
            MailFeedbackMessage = ex.Message;
        }
        finally
        {
            IsMailBusy = false;
        }
    }

    private static bool IsValidEmail(string value)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(value);
            return string.Equals(addr.Address, value, StringComparison.OrdinalIgnoreCase) && value.Contains('@') && value.IndexOf('.', value.IndexOf('@')) > value.IndexOf('@') + 1;
        }
        catch
        {
            return false;
        }
    }

    private async Task RefreshMailStatusSummaryAsync()
    {
        MailAlertStatusSummary summary = await _mailOutbox.GetStatusSummaryAsync();
        HasStoredMailCredential = summary.HasStoredCredential;

        if (!summary.IsConfigured)
        {
            string baseText = _localization.GetString("Mail_StatusNotConfigured");
            MailStatusSummary = summary.PendingCount > 0
                ? $"{baseText} — {_localization.Format("Mail_StatusPending", summary.PendingCount)}"
                : baseText;
            return;
        }

        if (summary.LatestItem is null)
        {
            MailStatusSummary = _localization.GetString("Mail_StatusConfigured");
            return;
        }

        string latestStatus = summary.LatestItem.Status switch
        {
            MailOutboxStatus.Pending => _localization.Format("Mail_StatusPending", summary.PendingCount),
            MailOutboxStatus.Accepted => _localization.GetString("Mail_StatusAccepted"),
            MailOutboxStatus.Unknown => _localization.GetString("Mail_StatusUnknown"),
            _ => $"{_localization.GetString("Mail_StatusFailed")}: {summary.LatestItem.LastErrorSummary}"
        };
        MailStatusSummary = latestStatus;
    }

    private void UpdateCooldownText()
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
            SecretAuthError = string.Empty;
            OnPropertyChanged(nameof(IsInPinCooldown));
            SecretPinInputFocusRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        OnPropertyChanged(nameof(IsInPinCooldown));
        string formatted = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
        SecretAuthError = _localization.Format("Secret_LockedOutFormat", formatted);
    }

    public void Dispose()
    {
        _cooldownTimer.Stop();
        // Rule 8.2: Always lock SecretManage when closing Settings!
        if (_isManageAuthenticated)
            _secretAccess.Lock("closed_settings");
    }
}

public sealed class AsyncRelayCommand<T> : System.Windows.Input.ICommand
{
    private readonly Func<T?, Task> _execute;
    private bool _running;

    public AsyncRelayCommand(Func<T?, Task> execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute(parameter is T t ? t : default);
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
