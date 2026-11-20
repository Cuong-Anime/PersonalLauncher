namespace PersonalLauncher.Core.Models;

public sealed record GameEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string SearchKey { get; init; }
    public required string ExePath { get; init; }
    public required string NormalizedExeKey { get; init; }
    public string? FileIdentity { get; init; }
    public required string InstallDirectory { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string? IconAssetId { get; init; }
    public string? TitleLogoAssetId { get; init; }
    public string? WallpaperAssetId { get; init; }
    public string? CardCoverAssetId { get; init; }
    public GameVisibility Visibility { get; init; } = GameVisibility.Public;
    public int SortOrder { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record GameCreateRequest
{
    public required string DisplayName { get; init; }
    public required string ExePath { get; init; }
    public string? InstallDirectory { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string? IconSourceFilePath { get; init; }
    public string? TitleLogoSourceFilePath { get; init; }
    public string? WallpaperSourceFilePath { get; init; }
    public string? CardCoverSourceFilePath { get; init; }
    public GameVisibility Visibility { get; init; } = GameVisibility.Public;
}

public sealed record GameUpdateLocationRequest
{
    public required string GameId { get; init; }
    public required string ExePath { get; init; }
    public required string InstallDirectory { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
}

public sealed record GameUpdateMediaRequest
{
    public required string GameId { get; init; }
    public string? IconSourceFilePath { get; init; }
    public bool ResetIconToDefault { get; init; }
    public string? TitleLogoSourceFilePath { get; init; }
    public bool ClearTitleLogo { get; init; }
    public string? WallpaperSourceFilePath { get; init; }
    public bool ResetWallpaperToDefault { get; init; }
    public string? CardCoverSourceFilePath { get; init; }
    public bool ResetCardCoverToAuto { get; init; }
}

public sealed record AssetRecord
{
    public required string Id { get; init; }
    public required AssetType AssetType { get; init; }
    public required string RelativePath { get; init; }
    public string? ThumbnailRelativePath { get; init; }
    public required string Sha256 { get; init; }
    public long FileSizeBytes { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Language { get; init; } = "vi-VN";
    public CloseAction CloseAction { get; init; } = CloseAction.MinimizeToTray;
    public bool StartWithWindows { get; init; } = false;
    public string? LastPublicSelectedGameId { get; init; }
    public string? AppLogoAssetId { get; init; }
    public string? DefaultWallpaperAssetId { get; init; }
    public double WindowWidth { get; init; } = 1280;
    public double WindowHeight { get; init; } = 720;
    public double? WindowLeft { get; init; }
    public double? WindowTop { get; init; }
    public bool IsMaximized { get; init; } = false;
}
