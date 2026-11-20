using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Core.Contracts;

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? SettingsChanged;
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
    Task UpdateAsync(Func<AppSettings, AppSettings> updater, CancellationToken ct = default);
}

public interface IPasscodeStore
{
    Task<PasscodeHealthStatus> CheckHealthAsync(string? expectedGenerationId, bool hasSecretGamesOrConfigured, CancellationToken ct = default);
    Task<string> CreatePasscodeAsync(string pin, CancellationToken ct = default);
    Task<bool> VerifyPasscodeAsync(string pin, string? expectedGenerationId, CancellationToken ct = default);
    Task<string> RotatePasscodeAsync(string newPin, string? previousGenerationId, CancellationToken ct = default);
    Task RestoreBackupAfterFailedRotationAsync(CancellationToken ct = default);
}

public interface ISecurityAttemptService
{
    Task<SecurityAttemptSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    Task<PinVerificationResult> VerifyPinWithTrackingAsync(
        string pin,
        Func<string, string?, CancellationToken, Task<bool>> verifyFunc,
        CancellationToken ct = default);
    Task<SecurityStateRecord> GetSecurityStateAsync(CancellationToken ct = default);
}

public interface ISecretAccessService
{
    SecretScope CurrentScope { get; }
    long SessionVersion { get; }
    bool IsUnlockedForBrowse { get; }
    bool IsUnlockedForManage { get; }
    event EventHandler<SecretScope>? ScopeChanged;

    Task<PasscodeHealthStatus> GetPasscodeHealthAsync(CancellationToken ct = default);
    Task<bool> IsPinConfiguredAsync(CancellationToken ct = default);
    Task<SecurityAttemptSnapshot> GetAttemptSnapshotAsync(CancellationToken ct = default);
    Task<(bool Success, string? ErrorKey)> SetupFirstPinAsync(string pin, string confirmPin, CancellationToken ct = default);
    Task<PinVerificationResult> UnlockAsync(string pin, SecretScope targetScope, CancellationToken ct = default);
    Task<PinVerificationResult> ChangePinAsync(string currentPin, string newPin, string confirmPin, CancellationToken ct = default);
    bool HasScope(SecretScope requiredScope);
    void RecordActivity();
    void CheckInactivityTimeout();
    void Lock(string reason = "manual");
}

public interface IAssetService
{
    Task<AssetRecord> ImportGameAssetAsync(string gameId, AssetType assetType, string sourceFilePath, CancellationToken ct = default);
    Task<AssetRecord> ImportAppLogoAsync(string sourceFilePath, CancellationToken ct = default);
    Task<AssetRecord> ImportDefaultWallpaperAsync(string sourceFilePath, CancellationToken ct = default);
    Task<string> ResolveAssetPathAsync(string? assetId, AssetType assetType, bool preferThumbnail = false, CancellationToken ct = default);
    Task<string> ResolveCardCoverImagePathAsync(GameEntry game, string? activeDefaultWallpaperAssetId = null, CancellationToken ct = default);
    Task CleanupUnreferencedAssetsAsync(string? activeAppLogoAssetId, string? activeDefaultWallpaperAssetId = null, CancellationToken ct = default);
    Task DeleteGameAssetsFolderAsync(string gameId, CancellationToken ct = default);
}

public interface IGameLibraryService
{
    event EventHandler? PublicLibraryChanged;
    event EventHandler? SecretLibraryChanged;

    Task<IReadOnlyList<GameEntry>> GetPublicGamesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GameEntry>> GetSecretGamesAsync(CancellationToken ct = default);
    Task<GameEntry?> GetGameByIdAsync(string gameId, CancellationToken ct = default);
    Task<GameEntry> AddManualGameAsync(GameCreateRequest request, CancellationToken ct = default);
    Task<BatchImportResult> ImportScannedGamesAsync(IReadOnlyList<BatchImportItem> items, GameVisibility visibility, CancellationToken ct = default);
    Task<GameEntry> UpdateGameLocationAsync(GameUpdateLocationRequest request, CancellationToken ct = default);
    Task<GameEntry> UpdateGameMediaAsync(GameUpdateMediaRequest request, CancellationToken ct = default);
    Task<GameEntry> SetGameVisibilityAsync(string gameId, GameVisibility targetVisibility, CancellationToken ct = default);
    Task DeleteGameFromLibraryAsync(string gameId, CancellationToken ct = default);
}

public interface ISearchService
{
    Task<IReadOnlyList<GameEntry>> SearchAsync(string? query, GameVisibility scope, CancellationToken ct = default);
    IReadOnlyList<GameEntry> FilterInMemory(IReadOnlyList<GameEntry> source, string? query);
}

public interface IGameScanService
{
    Task<ScanResult> ScanDirectoryAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default);
}

public interface IGameLaunchService
{
    event EventHandler<(string GameId, GameLaunchState State)>? GameStateChanged;
    GameLaunchState GetState(GameEntry game);
    Task<GameLaunchResult> LaunchAsync(string gameId, CancellationToken ct = default);
}

public interface IMailCredentialStore
{
    bool HasCredential();
    Task SaveCredentialAsync(string secret, CancellationToken ct = default);
    Task<string?> LoadCredentialAsync(CancellationToken ct = default);
    Task ClearCredentialAsync(CancellationToken ct = default);
}

public interface IMailTransport
{
    Task<MailSendResult> SendAsync(
        MailSettingsRecord settings,
        string secretCredential,
        string messageId,
        string subject,
        string bodyText,
        CancellationToken ct = default);
}

public interface IMailOutboxService
{
    event EventHandler? StatusChanged;
    Task<MailSettingsRecord> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(MailSettingsRecord settings, string? newSecretCredential, CancellationToken ct = default);
    Task<MailAlertStatusSummary> GetStatusSummaryAsync(CancellationToken ct = default);
    Task<MailSendResult> SendTestEmailAsync(CancellationToken ct = default);
    Task ProcessPendingAsync(CancellationToken ct = default);
    void StartBackgroundWorker();
    Task StopBackgroundWorkerAsync();
}

public interface IStartupService
{
    StartupStatus GetStatus();
    (bool Success, StartupStatus ResultingStatus, string? ErrorMessage) SetEnabled(bool enabled);
}

public interface ITrayService : IDisposable
{
    bool IsAvailable { get; }
    string? LastError { get; }
    event EventHandler? OpenRequested;
    event EventHandler? LockSecretRequested;
    event EventHandler? ExitRequested;
    bool Initialize();
    void UpdateState(bool secretUnlocked, string openLabel, string lockSecretLabel, string exitLabel, string tooltip);
    void Hide();
}

public interface ILocalizationService
{
    string CurrentLanguage { get; }
    IReadOnlyList<(string Code, string DisplayName)> SupportedLanguages { get; }
    event EventHandler<string>? LanguageChanged;
    void SetLanguage(string languageCode);
    string GetString(string key);
    string Format(string key, params object[] args);
}
