using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Core.Contracts;

public interface IAppPaths
{
    string DataRootDirectory { get; }
    string DatabaseFilePath { get; }
    string SettingsFilePath { get; }
    string AssetsRootDirectory { get; }
    string GameAssetsRootDirectory { get; }
    string ThumbnailsCacheDirectory { get; }
    string SecurityDirectory { get; }
    string PasscodeFilePath { get; }
    string PasscodeBackupFilePath { get; }
    string MailCredentialsFilePath { get; }
    string LogsDirectory { get; }
    string DefaultAppLogoPath { get; }
    string DefaultGameIconPath { get; }
    string DefaultWallpaperPath { get; }
    void EnsureDirectoriesExist();
}

public interface IAppLogger
{
    void Info(string category, string message);
    void Warn(string category, string message);
    void Error(string category, string message, Exception? exception = null);
}

public interface IGameRepository
{
    Task<IReadOnlyList<GameEntry>> GetByVisibilityAsync(GameVisibility visibility, CancellationToken ct = default);
    Task<GameEntry?> GetByIdAsync(string gameId, CancellationToken ct = default);
    Task<bool> ExistsByNormalizedExeKeyOrIdentityAsync(string normalizedExeKey, string? fileIdentity, string? excludeGameId = null, CancellationToken ct = default);
    Task<HashSet<string>> GetAllNormalizedExeKeysAsync(CancellationToken ct = default);
    Task<HashSet<string>> GetAllFileIdentitiesAsync(CancellationToken ct = default);
    Task<bool> HasAnySecretGamesAsync(CancellationToken ct = default);
    Task<int> GetNextSortOrderAsync(GameVisibility visibility, CancellationToken ct = default);
    Task InsertAsync(GameEntry game, CancellationToken ct = default);
    Task InsertBatchAsync(IReadOnlyList<GameEntry> games, CancellationToken ct = default);
    Task UpdateAsync(GameEntry game, CancellationToken ct = default);
    Task UpdateVisibilityAsync(string gameId, GameVisibility visibility, int sortOrder, DateTimeOffset updatedAtUtc, CancellationToken ct = default);
    Task DeleteAsync(string gameId, CancellationToken ct = default);
}

public interface IAssetRepository
{
    Task<AssetRecord?> GetByIdAsync(string assetId, CancellationToken ct = default);
    Task InsertAsync(AssetRecord asset, CancellationToken ct = default);
    Task DeleteAsync(string assetId, CancellationToken ct = default);
    Task<IReadOnlyList<AssetRecord>> GetUnreferencedAssetsAsync(
        string? activeAppLogoAssetId,
        string? activeDefaultWallpaperAssetId = null,
        CancellationToken ct = default);
}

public interface ISecurityRepository
{
    Task<SecurityStateRecord> GetStateAsync(CancellationToken ct = default);
    Task SaveStateAsync(SecurityStateRecord state, CancellationToken ct = default);
    Task SaveAttemptAndOptionalAlertAsync(
        SecurityStateRecord updatedState,
        MailOutboxItem? newOutboxItem,
        CancellationToken ct = default);
}

public interface IMailRepository
{
    Task<MailSettingsRecord> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(MailSettingsRecord settings, CancellationToken ct = default);
    Task EnqueueOutboxAsync(MailOutboxItem item, CancellationToken ct = default);
    Task<IReadOnlyList<MailOutboxItem>> GetDueOutboxItemsAsync(DateTimeOffset utcNow, CancellationToken ct = default);
    Task<IReadOnlyList<MailOutboxItem>> GetAllOutboxItemsAsync(CancellationToken ct = default);
    Task UpdateOutboxItemAsync(MailOutboxItem item, CancellationToken ct = default);
}
