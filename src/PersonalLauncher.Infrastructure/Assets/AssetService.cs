using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;
using PersonalLauncher.Core.Services;

namespace PersonalLauncher.Infrastructure.Assets;

public sealed class AssetService : IAssetService
{
    public const long MaxImageFileSizeBytes = 20L * 1024 * 1024; // 20 MiB for static images
    public const long MaxVideoFileSizeBytes = 500L * 1024 * 1024; // 500 MiB for live video wallpapers
    public const long MaxImagePixels = 40_000_000L; // 40 megapixels

    private static readonly HashSet<string> TitleLogoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png"
    };

    private readonly IAppPaths _paths;
    private readonly IAssetRepository _assetRepo;
    private readonly IAppLogger _logger;

    public AssetService(IAppPaths paths, IAssetRepository assetRepo, IAppLogger logger)
    {
        _paths = paths;
        _assetRepo = assetRepo;
        _logger = logger;
    }

    public Task<AssetRecord> ImportGameAssetAsync(string gameId, AssetType assetType, string sourceFilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("Game ID is required.", nameof(gameId));

        string folder = Path.Combine(_paths.GameAssetsRootDirectory, gameId);
        return ImportCoreAsync(folder, Path.Combine("games", gameId), assetType, sourceFilePath, ct);
    }

    public Task<AssetRecord> ImportAppLogoAsync(string sourceFilePath, CancellationToken ct = default)
    {
        string folder = Path.Combine(_paths.AssetsRootDirectory, "app");
        return ImportCoreAsync(folder, "app", AssetType.AppLogo, sourceFilePath, ct);
    }

    public Task<AssetRecord> ImportDefaultWallpaperAsync(string sourceFilePath, CancellationToken ct = default)
    {
        string folder = Path.Combine(_paths.AssetsRootDirectory, "app");
        return ImportCoreAsync(folder, "app", AssetType.Wallpaper, sourceFilePath, ct);
    }

    public async Task<string> ResolveAssetPathAsync(
        string? assetId,
        AssetType assetType,
        bool preferThumbnail = false,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(assetId))
        {
            AssetRecord? record = await _assetRepo.GetByIdAsync(assetId, ct);
            if (record is not null)
            {
                if (preferThumbnail && !string.IsNullOrWhiteSpace(record.ThumbnailRelativePath))
                {
                    string thumbFull = Path.Combine(_paths.ThumbnailsCacheDirectory, record.ThumbnailRelativePath);
                    if (PathNormalization.IsPathInsideDirectory(thumbFull, _paths.ThumbnailsCacheDirectory) &&
                        File.Exists(thumbFull))
                    {
                        return thumbFull;
                    }
                }

                string assetFull = Path.Combine(_paths.AssetsRootDirectory, record.RelativePath);
                if (PathNormalization.IsPathInsideDirectory(assetFull, _paths.AssetsRootDirectory) &&
                    File.Exists(assetFull))
                {
                    // When caller specifically asks for a static thumbnail and the full asset is a video
                    // without a generated thumbnail, fall back to static default wallpaper instead of a video!
                    if (preferThumbnail && MediaFormats.IsVideoPath(assetFull))
                        return _paths.DefaultWallpaperPath;

                    return assetFull;
                }
            }
        }

        return GetDefaultPath(assetType);
    }

    public async Task<string> ResolveCardCoverImagePathAsync(
        GameEntry game,
        string? activeDefaultWallpaperAssetId = null,
        CancellationToken ct = default)
    {
        // 1. Dedicated static cover image set by user takes top priority
        if (!string.IsNullOrWhiteSpace(game.CardCoverAssetId))
        {
            string coverPath = await ResolveAssetPathAsync(game.CardCoverAssetId, AssetType.CardCover, preferThumbnail: true, ct);
            if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath) && !MediaFormats.IsVideoPath(coverPath))
                return coverPath;
        }

        // 2. Auto static thumbnail of game's wallpaper (works for both image & live video wallpaper!)
        if (!string.IsNullOrWhiteSpace(game.WallpaperAssetId))
        {
            string wpThumb = await ResolveAssetPathAsync(game.WallpaperAssetId, AssetType.Wallpaper, preferThumbnail: true, ct);
            if (!string.IsNullOrWhiteSpace(wpThumb) && File.Exists(wpThumb) && !MediaFormats.IsVideoPath(wpThumb))
                return wpThumb;
        }

        // 3. Static thumbnail of custom default wallpaper (if configured)
        if (!string.IsNullOrWhiteSpace(activeDefaultWallpaperAssetId))
        {
            string defThumb = await ResolveAssetPathAsync(activeDefaultWallpaperAssetId, AssetType.Wallpaper, preferThumbnail: true, ct);
            if (!string.IsNullOrWhiteSpace(defThumb) && File.Exists(defThumb) && !MediaFormats.IsVideoPath(defThumb))
                return defThumb;
        }

        // 4. Bundled static default wallpaper
        return _paths.DefaultWallpaperPath;
    }

    public async Task CleanupUnreferencedAssetsAsync(
        string? activeAppLogoAssetId,
        string? activeDefaultWallpaperAssetId = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<AssetRecord> orphans = await _assetRepo.GetUnreferencedAssetsAsync(
            activeAppLogoAssetId,
            activeDefaultWallpaperAssetId,
            ct);

        foreach (AssetRecord asset in orphans)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string fullPath = Path.Combine(_paths.AssetsRootDirectory, asset.RelativePath);
                if (PathNormalization.IsPathInsideDirectory(fullPath, _paths.AssetsRootDirectory) && File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }

                if (!string.IsNullOrWhiteSpace(asset.ThumbnailRelativePath))
                {
                    string thumbFull = Path.Combine(_paths.ThumbnailsCacheDirectory, asset.ThumbnailRelativePath);
                    if (PathNormalization.IsPathInsideDirectory(thumbFull, _paths.ThumbnailsCacheDirectory) && File.Exists(thumbFull))
                    {
                        File.Delete(thumbFull);
                    }
                }

                await _assetRepo.DeleteAsync(asset.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.Warn("Assets", $"Could not clean up orphan asset id={asset.Id}: {ex.Message}");
            }
        }
    }

    public Task DeleteGameAssetsFolderAsync(string gameId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            return Task.CompletedTask;

        string folder = Path.Combine(_paths.GameAssetsRootDirectory, gameId);
        // Critical safety guard: verify folder resolves strictly inside launcher's managed GameAssetsRootDirectory!
        if (!PathNormalization.IsPathInsideDirectory(folder, _paths.GameAssetsRootDirectory))
        {
            _logger.Warn("Assets", "Blocked deletion outside managed game assets directory.");
            return Task.CompletedTask;
        }

        if (Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.Warn("Assets", $"Failed to delete managed assets folder for gameId={gameId}: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    private async Task<AssetRecord> ImportCoreAsync(
        string targetDirectory,
        string relativeFolderPrefix,
        AssetType assetType,
        string sourceFilePath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            throw new AssetValidationException("FILE_NOT_FOUND", "Selected media file does not exist.");

        string ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (assetType == AssetType.Wallpaper && MediaFormats.VideoWallpaperExtensions.Contains(ext))
        {
            return await ImportVideoWallpaperAsync(targetDirectory, relativeFolderPrefix, sourceFilePath, ext, ct);
        }

        HashSet<string> allowed = assetType switch
        {
            AssetType.Icon or AssetType.AppLogo => MediaFormats.IconExtensions,
            AssetType.TitleLogo => TitleLogoExtensions,
            _ => MediaFormats.StaticImageExtensions
        };

        if (!allowed.Contains(ext))
            throw new AssetValidationException("UNSUPPORTED_FORMAT", $"File extension '{ext}' is not supported for {assetType}.");

        var fi = new FileInfo(sourceFilePath);
        if (fi.Length == 0 || fi.Length > MaxImageFileSizeBytes)
            throw new AssetValidationException("FILE_TOO_LARGE", $"Image file size must be between 1 byte and {MaxImageFileSizeBytes / (1024 * 1024)} MiB.");

        // Validate real magic bytes & actual image decoding (never trust extension alone!)
        byte[] rawBytes = await File.ReadAllBytesAsync(sourceFilePath, ct);
        if (!HasValidImageSignature(rawBytes, ext))
            throw new AssetValidationException("INVALID_SIGNATURE", "File header does not match a supported image format.");

        (int pixelWidth, int pixelHeight) = await Task.Run(() => DecodeAndValidate(rawBytes), ct);

        _paths.EnsureDirectoriesExist();
        Directory.CreateDirectory(targetDirectory);

        string assetId = Guid.NewGuid().ToString("N");
        string fileName = $"{assetType.ToString().ToLowerInvariant()}_{assetId}{ext}";
        string finalPath = Path.Combine(targetDirectory, fileName);
        string tempPath = Path.Combine(targetDirectory, $".tmp_{assetId}{ext}");

        string? thumbRelPath = null;
        string? thumbTempPath = null;
        string? thumbFinalPath = null;

        try
        {
            await File.WriteAllBytesAsync(tempPath, rawBytes, ct);
            await Task.Run(() =>
            {
                using var verifyStream = File.OpenRead(tempPath);
                BitmapDecoder.Create(verifyStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            }, ct);

            if (assetType is AssetType.Wallpaper or AssetType.CardCover or AssetType.Icon or AssetType.AppLogo)
            {
                bool saveJpeg = assetType is AssetType.Wallpaper or AssetType.CardCover;
                int maxThumbDim = saveJpeg ? 480 : 128;
                string thumbExt = saveJpeg ? ".jpg" : ".png";
                thumbRelPath = $"{assetId}{thumbExt}";
                thumbFinalPath = Path.Combine(_paths.ThumbnailsCacheDirectory, thumbRelPath);
                thumbTempPath = Path.Combine(_paths.ThumbnailsCacheDirectory, $".tmp_{assetId}{thumbExt}");

                await Task.Run(() => GenerateThumbnail(rawBytes, pixelWidth, pixelHeight, maxThumbDim, saveJpeg, thumbTempPath), ct);
            }

            File.Move(tempPath, finalPath, overwrite: true);
            if (thumbTempPath is not null && thumbFinalPath is not null)
                File.Move(thumbTempPath, thumbFinalPath, overwrite: true);

            string sha256 = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
            var record = new AssetRecord
            {
                Id = assetId,
                AssetType = assetType,
                RelativePath = Path.Combine(relativeFolderPrefix, fileName),
                ThumbnailRelativePath = thumbRelPath,
                Sha256 = sha256,
                FileSizeBytes = rawBytes.Length,
                PixelWidth = pixelWidth,
                PixelHeight = pixelHeight,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            await _assetRepo.InsertAsync(record, ct);
            return record;
        }
        catch
        {
            TryDeleteQuietly(tempPath);
            TryDeleteQuietly(thumbTempPath);
            throw;
        }
    }

    private async Task<AssetRecord> ImportVideoWallpaperAsync(
        string targetDirectory,
        string relativeFolderPrefix,
        string sourceFilePath,
        string ext,
        CancellationToken ct)
    {
        var fi = new FileInfo(sourceFilePath);
        if (fi.Length < 16 || fi.Length > MaxVideoFileSizeBytes)
            throw new AssetValidationException("FILE_TOO_LARGE", $"Video file size must be between 16 bytes and {MaxVideoFileSizeBytes / (1024 * 1024)} MiB.");

        // Read header bytes for magic-signature verification
        byte[] header = new byte[Math.Min(64, (int)fi.Length)];
        await using (var hs = File.OpenRead(sourceFilePath))
        {
            await hs.ReadExactlyAsync(header, ct);
        }

        if (!HasValidVideoSignature(header, ext))
            throw new AssetValidationException("INVALID_SIGNATURE", "File header does not match a supported live video wallpaper format.");

        _paths.EnsureDirectoriesExist();
        Directory.CreateDirectory(targetDirectory);

        string assetId = Guid.NewGuid().ToString("N");
        string fileName = $"wallpaper_{assetId}{ext}";
        string finalPath = Path.Combine(targetDirectory, fileName);
        string tempPath = Path.Combine(targetDirectory, $".tmp_{assetId}{ext}");

        string? thumbRelPath = null;
        string? thumbTempPath = null;
        string? thumbFinalPath = null;
        int thumbW = 0;
        int thumbH = 0;

        try
        {
            // Stream-copy + compute SHA-256 without loading huge 100MB+ videos into RAM!
            string sha256;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var src = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
            await using (var dst = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                byte[] buffer = new byte[128 * 1024];
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await dst.FlushAsync(ct);
                sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            File.Move(tempPath, finalPath, overwrite: true);

            // Auto-extract a static JPEG cover thumbnail for the Game Library card strip!
            string candidateThumbRel = $"{assetId}.jpg";
            thumbTempPath = Path.Combine(_paths.ThumbnailsCacheDirectory, $".tmp_{assetId}.jpg");
            thumbFinalPath = Path.Combine(_paths.ThumbnailsCacheDirectory, candidateThumbRel);

            (bool extracted, int w, int h) = await Task.Run(
                () => VideoThumbnailExtractor.TryExtractThumbnailJpeg(finalPath, thumbTempPath, maxDimension: 640),
                ct);

            if (extracted && File.Exists(thumbTempPath))
            {
                File.Move(thumbTempPath, thumbFinalPath, overwrite: true);
                thumbRelPath = candidateThumbRel;
                thumbW = w;
                thumbH = h;
            }
            else
            {
                TryDeleteQuietly(thumbTempPath);
            }

            var record = new AssetRecord
            {
                Id = assetId,
                AssetType = AssetType.Wallpaper,
                RelativePath = Path.Combine(relativeFolderPrefix, fileName),
                ThumbnailRelativePath = thumbRelPath,
                Sha256 = sha256,
                FileSizeBytes = fi.Length,
                PixelWidth = thumbW,
                PixelHeight = thumbH,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            await _assetRepo.InsertAsync(record, ct);
            return record;
        }
        catch
        {
            TryDeleteQuietly(tempPath);
            TryDeleteQuietly(thumbTempPath);
            throw;
        }
    }

    private static (int Width, int Height) DecodeAndValidate(byte[] rawBytes)
    {
        try
        {
            using var ms = new MemoryStream(rawBytes, writable: false);
            BitmapDecoder decoder = BitmapDecoder.Create(
                ms,
                BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
                throw new AssetValidationException("DECODE_FAILED", "Image contains no readable frames.");

            BitmapFrame frame = decoder.Frames[0];
            int w = frame.PixelWidth;
            int h = frame.PixelHeight;
            if (w <= 0 || h <= 0)
                throw new AssetValidationException("INVALID_DIMENSIONS", "Image has invalid pixel dimensions.");

            if ((long)w * h > MaxImagePixels)
                throw new AssetValidationException("TOO_MANY_PIXELS", "Image exceeds maximum allowed 40 megapixels.");

            return (w, h);
        }
        catch (AssetValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AssetValidationException("DECODE_FAILED", $"Image could not be decoded: {ex.Message}");
        }
    }

    private static void GenerateThumbnail(
        byte[] rawBytes,
        int pixelWidth,
        int pixelHeight,
        int maxDimension,
        bool saveAsJpeg,
        string destinationPath)
    {
        using var ms = new MemoryStream(rawBytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (pixelWidth >= pixelHeight && pixelWidth > maxDimension)
            bitmap.DecodePixelWidth = maxDimension;
        else if (pixelHeight > pixelWidth && pixelHeight > maxDimension)
            bitmap.DecodePixelHeight = maxDimension;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();

        BitmapEncoder encoder = saveAsJpeg
            ? new JpegBitmapEncoder { QualityLevel = 86 }
            : new PngBitmapEncoder();

        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fs);
    }

    private static bool HasValidImageSignature(byte[] data, string ext)
    {
        if (data.Length < 4)
            return false;

        bool isPng = data.Length >= 8 &&
                     data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
                     data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

        bool isJpeg = data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
        bool isIco = data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01 && data[3] == 0x00;

        return ext switch
        {
            ".png" => isPng,
            ".jpg" or ".jpeg" => isJpeg,
            ".ico" => isIco || isPng,
            _ => false
        };
    }

    private static bool HasValidVideoSignature(byte[] header, string ext)
    {
        if (header.Length < 12)
            return false;

        // ISO Base Media (MP4 / MOV / M4V): "ftyp" / "moov" / "wide" / "mdat" at offset 4
        bool isIsoMp4 = (header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p') ||
                        (header[4] == (byte)'m' && header[5] == (byte)'o' && header[6] == (byte)'o' && header[7] == (byte)'v') ||
                        (header[4] == (byte)'w' && header[5] == (byte)'i' && header[6] == (byte)'d' && header[7] == (byte)'e') ||
                        (header[4] == (byte)'m' && header[5] == (byte)'d' && header[6] == (byte)'a' && header[7] == (byte)'t');

        // EBML (WebM / MKV): 1A 45 DF A3
        bool isEbml = header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3;

        // ASF (WMV): 30 26 B2 75 8E 66 CF 11
        bool isAsf = header[0] == 0x30 && header[1] == 0x26 && header[2] == 0xB2 && header[3] == 0x75;

        // RIFF AVI: "RIFF" ... "AVI "
        bool isAvi = header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
                     header[8] == (byte)'A' && header[9] == (byte)'V' && header[10] == (byte)'I';

        return ext switch
        {
            ".mp4" or ".m4v" or ".mov" => isIsoMp4,
            ".webm" or ".mkv" => isEbml,
            ".wmv" => isAsf,
            ".avi" => isAvi,
            _ => false
        };
    }

    private string GetDefaultPath(AssetType type) => type switch
    {
        AssetType.AppLogo => _paths.DefaultAppLogoPath,
        AssetType.Icon => _paths.DefaultGameIconPath,
        AssetType.Wallpaper or AssetType.CardCover => _paths.DefaultWallpaperPath,
        _ => string.Empty
    };

    private static void TryDeleteQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
