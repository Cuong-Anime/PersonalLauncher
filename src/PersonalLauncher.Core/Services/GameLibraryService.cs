using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Core.Services;

public sealed class GameLibraryService : IGameLibraryService
{
    private readonly IGameRepository _gameRepo;
    private readonly IAssetService _assetService;
    private readonly ISecretAccessService _secretAccess;
    private readonly ISettingsService _settingsService;
    private readonly IFileIdentityProvider _fileIdentityProvider;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _timeProvider;

    public event EventHandler? PublicLibraryChanged;
    public event EventHandler? SecretLibraryChanged;

    public GameLibraryService(
        IGameRepository gameRepo,
        IAssetService assetService,
        ISecretAccessService secretAccess,
        ISettingsService settingsService,
        IFileIdentityProvider fileIdentityProvider,
        IAppLogger logger,
        TimeProvider? timeProvider = null)
    {
        _gameRepo = gameRepo;
        _assetService = assetService;
        _secretAccess = secretAccess;
        _settingsService = settingsService;
        _fileIdentityProvider = fileIdentityProvider;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<GameEntry>> GetPublicGamesAsync(CancellationToken ct = default)
    {
        return _gameRepo.GetByVisibilityAsync(GameVisibility.Public, ct);
    }

    public async Task<IReadOnlyList<GameEntry>> GetSecretGamesAsync(CancellationToken ct = default)
    {
        EnsureSecretScope(SecretScope.Browse);
        _secretAccess.RecordActivity();
        return await _gameRepo.GetByVisibilityAsync(GameVisibility.Secret, ct);
    }

    public async Task<GameEntry?> GetGameByIdAsync(string gameId, CancellationToken ct = default)
    {
        GameEntry? game = await _gameRepo.GetByIdAsync(gameId, ct);
        if (game is null)
            return null;

        if (game.Visibility == GameVisibility.Secret)
        {
            EnsureSecretScope(SecretScope.Browse);
            _secretAccess.RecordActivity();
        }

        return game;
    }

    public async Task<GameEntry> AddManualGameAsync(GameCreateRequest request, CancellationToken ct = default)
    {
        if (request.Visibility == GameVisibility.Secret)
        {
            EnsureSecretScope(SecretScope.Manage);
            _secretAccess.RecordActivity();
        }

        string displayName = TextNormalization.NormalizeDisplayName(request.DisplayName);
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.ExePath))
            throw new ArgumentException("Executable path is required.", nameof(request));

        string fullExePath = Path.GetFullPath(request.ExePath.Trim().Trim('"'));
        string normalizedExeKey = PathNormalization.NormalizeExePathKey(fullExePath);
        string? fileIdentity = _fileIdentityProvider.TryGetFileIdentity(fullExePath);

        bool duplicate = await _gameRepo.ExistsByNormalizedExeKeyOrIdentityAsync(normalizedExeKey, fileIdentity, null, ct);
        if (duplicate)
        {
            // Intentionally generic so Secret entries are never leaked when adding a public game
            throw new DuplicateGameException("A game with this executable already exists in the library.");
        }

        string exeDir = Path.GetDirectoryName(fullExePath) ?? string.Empty;
        string installDir = string.IsNullOrWhiteSpace(request.InstallDirectory)
            ? exeDir
            : Path.GetFullPath(request.InstallDirectory.Trim().Trim('"'));
        string workingDir = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? exeDir
            : Path.GetFullPath(request.WorkingDirectory.Trim().Trim('"'));

        string gameId = Guid.NewGuid().ToString("N");
        string? iconAssetId = null;
        string? titleLogoAssetId = null;
        string? wallpaperAssetId = null;
        string? cardCoverAssetId = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(request.IconSourceFilePath))
                iconAssetId = (await _assetService.ImportGameAssetAsync(gameId, AssetType.Icon, request.IconSourceFilePath, ct)).Id;

            if (!string.IsNullOrWhiteSpace(request.TitleLogoSourceFilePath))
                titleLogoAssetId = (await _assetService.ImportGameAssetAsync(gameId, AssetType.TitleLogo, request.TitleLogoSourceFilePath, ct)).Id;

            if (!string.IsNullOrWhiteSpace(request.WallpaperSourceFilePath))
                wallpaperAssetId = (await _assetService.ImportGameAssetAsync(gameId, AssetType.Wallpaper, request.WallpaperSourceFilePath, ct)).Id;

            if (!string.IsNullOrWhiteSpace(request.CardCoverSourceFilePath))
                cardCoverAssetId = (await _assetService.ImportGameAssetAsync(gameId, AssetType.CardCover, request.CardCoverSourceFilePath, ct)).Id;

            int sortOrder = await _gameRepo.GetNextSortOrderAsync(request.Visibility, ct);
            DateTimeOffset now = _timeProvider.GetUtcNow();

            var entry = new GameEntry
            {
                Id = gameId,
                DisplayName = displayName,
                SearchKey = TextNormalization.NormalizeForSearch(displayName),
                ExePath = fullExePath,
                NormalizedExeKey = normalizedExeKey,
                FileIdentity = fileIdentity,
                InstallDirectory = installDir,
                WorkingDirectory = workingDir,
                Arguments = request.Arguments.Where(a => !string.IsNullOrEmpty(a)).ToArray(),
                IconAssetId = iconAssetId,
                TitleLogoAssetId = titleLogoAssetId,
                WallpaperAssetId = wallpaperAssetId,
                CardCoverAssetId = cardCoverAssetId,
                Visibility = request.Visibility,
                SortOrder = sortOrder,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await _gameRepo.InsertAsync(entry, ct);

            if (request.Visibility == GameVisibility.Public)
            {
                _logger.Info("Library", $"Added public game id={entry.Id}");
                PublicLibraryChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Never log secret game metadata
                _logger.Info("Library", "Added secret game entry.");
                SecretLibraryChanged?.Invoke(this, EventArgs.Empty);
            }

            return entry;
        }
        catch
        {
            // Rollback any staged asset files on failure
            try
            {
                await _assetService.DeleteGameAssetsFolderAsync(gameId, CancellationToken.None);
            }
            catch
            {
                // Best effort cleanup
            }
            throw;
        }
    }

    public async Task<BatchImportResult> ImportScannedGamesAsync(
        IReadOnlyList<BatchImportItem> items,
        GameVisibility visibility,
        CancellationToken ct = default)
    {
        if (visibility == GameVisibility.Secret)
        {
            EnsureSecretScope(SecretScope.Manage);
            _secretAccess.RecordActivity();
        }

        if (items.Count == 0)
            return new BatchImportResult();

        HashSet<string> existingKeys = await _gameRepo.GetAllNormalizedExeKeysAsync(ct);
        HashSet<string> existingIdentities = await _gameRepo.GetAllFileIdentitiesAsync(ct);
        int nextSortOrder = await _gameRepo.GetNextSortOrderAsync(visibility, ct);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        var toInsert = new List<GameEntry>();
        var errors = new List<string>();
        int skipped = 0;

        foreach (BatchImportItem item in items)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                string displayName = TextNormalization.NormalizeDisplayName(item.DisplayName);
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = PathNormalization.GetDirectParentFolderName(item.ExePath);

                string fullExePath = Path.GetFullPath(item.ExePath.Trim().Trim('"'));
                string normalizedKey = PathNormalization.NormalizeExePathKey(fullExePath);
                string? fileIdentity = _fileIdentityProvider.TryGetFileIdentity(fullExePath);

                if (existingKeys.Contains(normalizedKey) ||
                    (fileIdentity is not null && existingIdentities.Contains(fileIdentity)))
                {
                    skipped++;
                    continue;
                }

                existingKeys.Add(normalizedKey);
                if (fileIdentity is not null)
                    existingIdentities.Add(fileIdentity);

                string exeDir = Path.GetDirectoryName(fullExePath) ?? item.InstallDirectory;
                string installDir = string.IsNullOrWhiteSpace(item.InstallDirectory)
                    ? exeDir
                    : Path.GetFullPath(item.InstallDirectory.Trim().Trim('"'));

                // Auto-imported games ALWAYS use default icon & wallpaper (null asset IDs -> defaults)
                toInsert.Add(new GameEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DisplayName = displayName,
                    SearchKey = TextNormalization.NormalizeForSearch(displayName),
                    ExePath = fullExePath,
                    NormalizedExeKey = normalizedKey,
                    FileIdentity = fileIdentity,
                    InstallDirectory = installDir,
                    WorkingDirectory = exeDir,
                    Arguments = Array.Empty<string>(),
                    IconAssetId = null,
                    TitleLogoAssetId = null,
                    WallpaperAssetId = null,
                    Visibility = visibility,
                    SortOrder = nextSortOrder++,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (toInsert.Count > 0)
        {
            await _gameRepo.InsertBatchAsync(toInsert, ct);
            if (visibility == GameVisibility.Public)
            {
                _logger.Info("Library", $"Batch imported {toInsert.Count} public game(s).");
                PublicLibraryChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _logger.Info("Library", $"Batch imported {toInsert.Count} secret game(s).");
                SecretLibraryChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        return new BatchImportResult
        {
            AddedCount = toInsert.Count,
            SkippedDuplicateCount = skipped,
            ErrorCount = errors.Count,
            Errors = errors,
            AddedGames = toInsert
        };
    }

    public async Task<GameEntry> UpdateGameLocationAsync(GameUpdateLocationRequest request, CancellationToken ct = default)
    {
        GameEntry existing = await _gameRepo.GetByIdAsync(request.GameId, ct)
            ?? throw new KeyNotFoundException($"Game '{request.GameId}' was not found.");

        if (existing.Visibility == GameVisibility.Secret)
        {
            EnsureSecretScope(SecretScope.Manage);
            _secretAccess.RecordActivity();
        }

        string fullExePath = Path.GetFullPath(request.ExePath.Trim().Trim('"'));
        string normalizedExeKey = PathNormalization.NormalizeExePathKey(fullExePath);
        string? fileIdentity = _fileIdentityProvider.TryGetFileIdentity(fullExePath);

        bool duplicate = await _gameRepo.ExistsByNormalizedExeKeyOrIdentityAsync(
            normalizedExeKey, fileIdentity, existing.Id, ct);
        if (duplicate)
            throw new DuplicateGameException("Another game in the library already points to this executable.");

        string exeDir = Path.GetDirectoryName(fullExePath) ?? existing.InstallDirectory;
        string installDir = string.IsNullOrWhiteSpace(request.InstallDirectory)
            ? exeDir
            : Path.GetFullPath(request.InstallDirectory.Trim().Trim('"'));
        string workingDir = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? exeDir
            : Path.GetFullPath(request.WorkingDirectory.Trim().Trim('"'));

        GameEntry updated = existing with
        {
            ExePath = fullExePath,
            NormalizedExeKey = normalizedExeKey,
            FileIdentity = fileIdentity,
            InstallDirectory = installDir,
            WorkingDirectory = workingDir,
            Arguments = request.Arguments.Where(a => !string.IsNullOrEmpty(a)).ToArray(),
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };

        await _gameRepo.UpdateAsync(updated, ct);
        RaiseChanged(updated.Visibility);
        return updated;
    }

    public async Task<GameEntry> UpdateGameMediaAsync(GameUpdateMediaRequest request, CancellationToken ct = default)
    {
        GameEntry existing = await _gameRepo.GetByIdAsync(request.GameId, ct)
            ?? throw new KeyNotFoundException($"Game '{request.GameId}' was not found.");

        if (existing.Visibility == GameVisibility.Secret)
        {
            EnsureSecretScope(SecretScope.Manage);
            _secretAccess.RecordActivity();
        }

        string? newIconId = existing.IconAssetId;
        string? newTitleLogoId = existing.TitleLogoAssetId;
        string? newWallpaperId = existing.WallpaperAssetId;
        string? newCardCoverId = existing.CardCoverAssetId;

        if (request.ResetIconToDefault)
            newIconId = null;
        else if (!string.IsNullOrWhiteSpace(request.IconSourceFilePath))
            newIconId = (await _assetService.ImportGameAssetAsync(existing.Id, AssetType.Icon, request.IconSourceFilePath, ct)).Id;

        if (request.ClearTitleLogo)
            newTitleLogoId = null;
        else if (!string.IsNullOrWhiteSpace(request.TitleLogoSourceFilePath))
            newTitleLogoId = (await _assetService.ImportGameAssetAsync(existing.Id, AssetType.TitleLogo, request.TitleLogoSourceFilePath, ct)).Id;

        if (request.ResetWallpaperToDefault)
            newWallpaperId = null;
        else if (!string.IsNullOrWhiteSpace(request.WallpaperSourceFilePath))
            newWallpaperId = (await _assetService.ImportGameAssetAsync(existing.Id, AssetType.Wallpaper, request.WallpaperSourceFilePath, ct)).Id;

        if (request.ResetCardCoverToAuto)
            newCardCoverId = null;
        else if (!string.IsNullOrWhiteSpace(request.CardCoverSourceFilePath))
            newCardCoverId = (await _assetService.ImportGameAssetAsync(existing.Id, AssetType.CardCover, request.CardCoverSourceFilePath, ct)).Id;

        GameEntry updated = existing with
        {
            IconAssetId = newIconId,
            TitleLogoAssetId = newTitleLogoId,
            WallpaperAssetId = newWallpaperId,
            CardCoverAssetId = newCardCoverId,
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };

        await _gameRepo.UpdateAsync(updated, ct);

        // Cleanup old unreferenced assets strictly after DB commit succeeds
        await _assetService.CleanupUnreferencedAssetsAsync(
            _settingsService.Current.AppLogoAssetId,
            _settingsService.Current.DefaultWallpaperAssetId,
            ct);

        RaiseChanged(updated.Visibility);
        return updated;
    }

    public async Task<GameEntry> SetGameVisibilityAsync(string gameId, GameVisibility targetVisibility, CancellationToken ct = default)
    {
        EnsureSecretScope(SecretScope.Manage);
        _secretAccess.RecordActivity();

        GameEntry existing = await _gameRepo.GetByIdAsync(gameId, ct)
            ?? throw new KeyNotFoundException($"Game '{gameId}' was not found.");

        if (existing.Visibility == targetVisibility)
            return existing;

        int nextSortOrder = await _gameRepo.GetNextSortOrderAsync(targetVisibility, ct);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        await _gameRepo.UpdateVisibilityAsync(gameId, targetVisibility, nextSortOrder, now, ct);

        // If moving to Secret and it was saved as lastPublicSelectedGameId, scrub it immediately!
        if (targetVisibility == GameVisibility.Secret &&
            string.Equals(_settingsService.Current.LastPublicSelectedGameId, gameId, StringComparison.Ordinal))
        {
            await _settingsService.UpdateAsync(s => s with { LastPublicSelectedGameId = null }, ct);
        }

        GameEntry updated = existing with
        {
            Visibility = targetVisibility,
            SortOrder = nextSortOrder,
            UpdatedAtUtc = now
        };

        PublicLibraryChanged?.Invoke(this, EventArgs.Empty);
        SecretLibraryChanged?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public async Task DeleteGameFromLibraryAsync(string gameId, CancellationToken ct = default)
    {
        GameEntry existing = await _gameRepo.GetByIdAsync(gameId, ct)
            ?? throw new KeyNotFoundException($"Game '{gameId}' was not found.");

        if (existing.Visibility == GameVisibility.Secret)
        {
            EnsureSecretScope(SecretScope.Manage);
            _secretAccess.RecordActivity();
        }

        await _gameRepo.DeleteAsync(gameId, ct);

        if (string.Equals(_settingsService.Current.LastPublicSelectedGameId, gameId, StringComparison.Ordinal))
        {
            await _settingsService.UpdateAsync(s => s with { LastPublicSelectedGameId = null }, ct);
        }

        // Remove ONLY launcher-managed assets (never touch the game's install directory!)
        await _assetService.CleanupUnreferencedAssetsAsync(
            _settingsService.Current.AppLogoAssetId,
            _settingsService.Current.DefaultWallpaperAssetId,
            ct);
        await _assetService.DeleteGameAssetsFolderAsync(gameId, ct);

        RaiseChanged(existing.Visibility);
    }

    private void EnsureSecretScope(SecretScope required)
    {
        if (!_secretAccess.HasScope(required))
            throw new SecretUnauthorizedException(required);
    }

    private void RaiseChanged(GameVisibility visibility)
    {
        if (visibility == GameVisibility.Public)
            PublicLibraryChanged?.Invoke(this, EventArgs.Empty);
        else
            SecretLibraryChanged?.Invoke(this, EventArgs.Empty);
    }
}
