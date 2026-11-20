using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Infrastructure.Persistence;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly HashSet<string> SupportedLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "vi-VN", "en-US", "ja-JP", "zh-CN"
    };

    private readonly IAppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();
    public event EventHandler<AppSettings>? SettingsChanged;

    public JsonSettingsService(IAppPaths paths, IAppLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();
            if (!File.Exists(_paths.SettingsFilePath))
            {
                Current = new AppSettings();
                await WriteAtomicallyAsync(Current, ct);
                return Current;
            }

            try
            {
                string json = await File.ReadAllTextAsync(_paths.SettingsFilePath, ct);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is null)
                    throw new InvalidDataException("Settings JSON deserialized to null.");

                Current = Sanitize(loaded);
                return Current;
            }
            catch (Exception ex)
            {
                // Preserve corrupt settings copy before recovering with defaults
                string backupPath = $"{_paths.SettingsFilePath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
                try
                {
                    File.Copy(_paths.SettingsFilePath, backupPath, overwrite: true);
                }
                catch { }

                _logger.Error("Settings", $"Corrupted settings.json backed up to '{Path.GetFileName(backupPath)}'. Falling back to defaults.", ex);
                Current = new AppSettings();
                await WriteAtomicallyAsync(Current, ct);
                return Current;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            AppSettings sanitized = Sanitize(settings);
            await WriteAtomicallyAsync(sanitized, ct);
            Current = sanitized;
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, Current);
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> updater, CancellationToken ct = default)
    {
        AppSettings updated;
        await _gate.WaitAsync(ct);
        try
        {
            updated = Sanitize(updater(Current));
            await WriteAtomicallyAsync(updated, ct);
            Current = updated;
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, updated);
    }

    private async Task WriteAtomicallyAsync(AppSettings settings, CancellationToken ct)
    {
        _paths.EnsureDirectoriesExist();
        string tempPath = Path.Combine(
            _paths.DataRootDirectory,
            $"settings.json.tmp.{Guid.NewGuid():N}");

        string json = JsonSerializer.Serialize(settings, JsonOptions);
        await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(fs))
        {
            await writer.WriteAsync(json.AsMemory(), ct);
            await writer.FlushAsync(ct);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(_paths.SettingsFilePath))
        {
            File.Replace(tempPath, _paths.SettingsFilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, _paths.SettingsFilePath);
        }
    }

    private static AppSettings Sanitize(AppSettings raw)
    {
        string lang = SupportedLanguageCodes.Contains(raw.Language ?? string.Empty)
            ? raw.Language!
            : "vi-VN";

        double width = raw.WindowWidth is >= 800 and <= 7680 ? raw.WindowWidth : 1280;
        double height = raw.WindowHeight is >= 520 and <= 4320 ? raw.WindowHeight : 720;

        return raw with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            Language = lang,
            WindowWidth = width,
            WindowHeight = height
        };
    }
}
