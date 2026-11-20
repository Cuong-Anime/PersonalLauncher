using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using PersonalLauncher.App.Services;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.App.ViewModels;

public sealed class EditLocationDialogViewModel : ObservableObject
{
    private readonly IGameLibraryService _gameLibrary;
    private readonly ILocalizationService _localization;
    private readonly GameEntry _original;

    private string _exePath;
    private string _installDirectory;
    private string _workingDirectory;
    private string _argumentsMultiline;
    private string _errorMessage = string.Empty;
    private bool _isSaving;

    public string GameName => _original.DisplayName;
    public event EventHandler<bool>? RequestClose;

    public string ExePath
    {
        get => _exePath;
        set
        {
            if (SetProperty(ref _exePath, value) && !string.IsNullOrWhiteSpace(value))
            {
                string? dir = Path.GetDirectoryName(value.Trim().Trim('"'));
                if (!string.IsNullOrEmpty(dir))
                {
                    InstallDirectory = dir;
                    WorkingDirectory = dir;
                }
            }
        }
    }

    public string InstallDirectory
    {
        get => _installDirectory;
        set => SetProperty(ref _installDirectory, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    public string ArgumentsMultiline
    {
        get => _argumentsMultiline;
        set => SetProperty(ref _argumentsMultiline, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
                SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand OpenCurrentFolderCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    public EditLocationDialogViewModel(GameEntry game, IGameLibraryService gameLibrary, ILocalizationService localization)
    {
        _original = game;
        _gameLibrary = gameLibrary;
        _localization = localization;

        _exePath = game.ExePath;
        _installDirectory = game.InstallDirectory;
        _workingDirectory = game.WorkingDirectory;
        _argumentsMultiline = string.Join(Environment.NewLine, game.Arguments);

        OpenCurrentFolderCommand = new RelayCommand(OpenCurrentFolder);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsSaving);
    }

    private void OpenCurrentFolder()
    {
        string targetDir = Directory.Exists(InstallDirectory)
            ? InstallDirectory
            : Path.GetDirectoryName(ExePath) ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(targetDir) && Directory.Exists(targetDir))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { targetDir },
                UseShellExecute = false
            });
        }
    }

    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        string cleanExe = ExePath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(cleanExe) || !File.Exists(cleanExe) ||
            !cleanExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = _localization.GetString("AddGame_ErrorExeRequired");
            return;
        }

        IsSaving = true;
        try
        {
            string[] args = string.IsNullOrWhiteSpace(ArgumentsMultiline)
                ? Array.Empty<string>()
                : ArgumentsMultiline.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            await _gameLibrary.UpdateGameLocationAsync(new GameUpdateLocationRequest
            {
                GameId = _original.Id,
                ExePath = cleanExe,
                InstallDirectory = InstallDirectory.Trim(),
                WorkingDirectory = WorkingDirectory.Trim(),
                Arguments = args
            });
            RequestClose?.Invoke(this, true);
        }
        catch (DuplicateGameException)
        {
            ErrorMessage = _localization.GetString("AddGame_ErrorDuplicate");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }
}

public sealed class EditLogoDialogViewModel : ObservableObject
{
    private readonly GameEntry _original;
    private readonly IGameLibraryService _gameLibrary;
    private readonly IAssetService _assetService;
    private readonly IAppPaths _paths;
    private readonly ImageCacheService _imageCache;

    private string? _newIconPath;
    private bool _resetIconToDefault;
    private string? _newTitleLogoPath;
    private bool _clearTitleLogo;
    private ImageSource? _iconPreview;
    private ImageSource? _titleLogoPreview;
    private string _errorMessage = string.Empty;
    private bool _isSaving;

    public string GameName => _original.DisplayName;
    public event EventHandler<bool>? RequestClose;

    public ImageSource? IconPreview
    {
        get => _iconPreview;
        private set => SetProperty(ref _iconPreview, value);
    }

    public ImageSource? TitleLogoPreview
    {
        get => _titleLogoPreview;
        private set
        {
            if (SetProperty(ref _titleLogoPreview, value))
                OnPropertyChanged(nameof(ShowTitleTextFallback));
        }
    }

    public bool ShowTitleTextFallback => TitleLogoPreview is null;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
                SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand ResetIconCommand { get; }
    public RelayCommand ClearTitleLogoCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    public EditLogoDialogViewModel(
        GameEntry game,
        IGameLibraryService gameLibrary,
        IAssetService assetService,
        IAppPaths paths,
        ImageCacheService imageCache)
    {
        _original = game;
        _gameLibrary = gameLibrary;
        _assetService = assetService;
        _paths = paths;
        _imageCache = imageCache;

        ResetIconCommand = new RelayCommand(() =>
        {
            _newIconPath = null;
            _resetIconToDefault = true;
            IconPreview = _imageCache.Load(_paths.DefaultGameIconPath, decodePixelWidth: 256);
        });

        ClearTitleLogoCommand = new RelayCommand(() =>
        {
            _newTitleLogoPath = null;
            _clearTitleLogo = true;
            TitleLogoPreview = null;
        });

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsSaving);
        _ = LoadInitialPreviewsAsync();
    }

    public void SelectIconFile(string path)
    {
        _newIconPath = path;
        _resetIconToDefault = false;
        IconPreview = _imageCache.Load(path, decodePixelWidth: 256);
    }

    public void SelectTitleLogoFile(string path)
    {
        _newTitleLogoPath = path;
        _clearTitleLogo = false;
        TitleLogoPreview = _imageCache.Load(path, decodePixelWidth: 500);
    }

    private async Task LoadInitialPreviewsAsync()
    {
        string iconPath = await _assetService.ResolveAssetPathAsync(_original.IconAssetId, AssetType.Icon);
        IconPreview = await _imageCache.LoadAsync(iconPath, decodePixelWidth: 256);

        if (!string.IsNullOrWhiteSpace(_original.TitleLogoAssetId))
        {
            string titlePath = await _assetService.ResolveAssetPathAsync(_original.TitleLogoAssetId, AssetType.TitleLogo);
            TitleLogoPreview = await _imageCache.LoadAsync(titlePath, decodePixelWidth: 500);
        }
    }

    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        IsSaving = true;
        try
        {
            await _gameLibrary.UpdateGameMediaAsync(new GameUpdateMediaRequest
            {
                GameId = _original.Id,
                IconSourceFilePath = _newIconPath,
                ResetIconToDefault = _resetIconToDefault,
                TitleLogoSourceFilePath = _newTitleLogoPath,
                ClearTitleLogo = _clearTitleLogo
            });
            RequestClose?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }
}

public sealed class EditWallpaperDialogViewModel : ObservableObject
{
    private readonly GameEntry _original;
    private readonly IGameLibraryService _gameLibrary;
    private readonly IAssetService _assetService;
    private readonly IAppPaths _paths;
    private readonly ImageCacheService _imageCache;

    private string? _newWallpaperPath;
    private bool _resetToDefault;
    private string? _newCardCoverPath;
    private bool _resetCardCoverToAuto;

    private ImageSource? _wallpaperPreview;
    private ImageSource? _cardCoverPreview;
    private string? _previewVideoPath;
    private bool _isLiveVideoPreview;
    private string _errorMessage = string.Empty;
    private bool _isSaving;

    public string GameName => _original.DisplayName;
    public event EventHandler<bool>? RequestClose;
    public event EventHandler<string?>? PreviewVideoChanged;

    public ImageSource? WallpaperPreview
    {
        get => _wallpaperPreview;
        private set => SetProperty(ref _wallpaperPreview, value);
    }

    public ImageSource? CardCoverPreview
    {
        get => _cardCoverPreview;
        private set => SetProperty(ref _cardCoverPreview, value);
    }

    public string? PreviewVideoPath
    {
        get => _previewVideoPath;
        private set
        {
            if (SetProperty(ref _previewVideoPath, value))
            {
                IsLiveVideoPreview = !string.IsNullOrWhiteSpace(value);
                PreviewVideoChanged?.Invoke(this, value);
            }
        }
    }

    public bool IsLiveVideoPreview
    {
        get => _isLiveVideoPreview;
        private set => SetProperty(ref _isLiveVideoPreview, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
                SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand ResetDefaultCommand { get; }
    public RelayCommand ResetCardCoverToAutoCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    public EditWallpaperDialogViewModel(
        GameEntry game,
        IGameLibraryService gameLibrary,
        IAssetService assetService,
        IAppPaths paths,
        ImageCacheService imageCache)
    {
        _original = game;
        _gameLibrary = gameLibrary;
        _assetService = assetService;
        _paths = paths;
        _imageCache = imageCache;

        ResetDefaultCommand = new RelayCommand(() =>
        {
            _newWallpaperPath = null;
            _resetToDefault = true;
            PreviewVideoPath = null;
            WallpaperPreview = _imageCache.Load(_paths.DefaultWallpaperPath, decodePixelWidth: 960);
            if (_newCardCoverPath is null && (string.IsNullOrWhiteSpace(_original.CardCoverAssetId) || _resetCardCoverToAuto))
                CardCoverPreview = _imageCache.Load(_paths.DefaultWallpaperPath, decodePixelWidth: 420);
        });

        ResetCardCoverToAutoCommand = new RelayCommand(() =>
        {
            _newCardCoverPath = null;
            _resetCardCoverToAuto = true;
            _ = RefreshAutoCardCoverPreviewAsync();
        });

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsSaving);
        _ = LoadInitialPreviewAsync();
    }

    public void SelectWallpaperFile(string path)
    {
        _newWallpaperPath = path;
        _resetToDefault = false;

        if (PersonalLauncher.Core.Services.MediaFormats.IsVideoPath(path))
        {
            PreviewVideoPath = path;
            // Keep existing poster underneath while preview plays
        }
        else
        {
            PreviewVideoPath = null;
            WallpaperPreview = _imageCache.Load(path, decodePixelWidth: 960);
            if (_newCardCoverPath is null && (string.IsNullOrWhiteSpace(_original.CardCoverAssetId) || _resetCardCoverToAuto))
                CardCoverPreview = _imageCache.Load(path, decodePixelWidth: 420);
        }
    }

    public void SelectCardCoverFile(string path)
    {
        _newCardCoverPath = path;
        _resetCardCoverToAuto = false;
        CardCoverPreview = _imageCache.Load(path, decodePixelWidth: 420);
    }

    private async Task LoadInitialPreviewAsync()
    {
        string wpPath = await _assetService.ResolveAssetPathAsync(_original.WallpaperAssetId, AssetType.Wallpaper, preferThumbnail: false);
        string coverPath = await _assetService.ResolveCardCoverImagePathAsync(_original);

        CardCoverPreview = await _imageCache.LoadAsync(coverPath, decodePixelWidth: 420);

        if (PersonalLauncher.Core.Services.MediaFormats.IsVideoPath(wpPath) && File.Exists(wpPath))
        {
            WallpaperPreview = await _imageCache.LoadAsync(coverPath, decodePixelWidth: 960);
            PreviewVideoPath = wpPath;
        }
        else
        {
            PreviewVideoPath = null;
            WallpaperPreview = await _imageCache.LoadAsync(wpPath, decodePixelWidth: 960);
        }
    }

    private async Task RefreshAutoCardCoverPreviewAsync()
    {
        if (!string.IsNullOrWhiteSpace(_newWallpaperPath) && !PersonalLauncher.Core.Services.MediaFormats.IsVideoPath(_newWallpaperPath))
        {
            CardCoverPreview = _imageCache.Load(_newWallpaperPath, decodePixelWidth: 420);
            return;
        }

        string wpThumb = _resetToDefault
            ? _paths.DefaultWallpaperPath
            : await _assetService.ResolveAssetPathAsync(_original.WallpaperAssetId, AssetType.Wallpaper, preferThumbnail: true);
        CardCoverPreview = await _imageCache.LoadAsync(wpThumb, decodePixelWidth: 420);
    }

    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        IsSaving = true;
        try
        {
            await _gameLibrary.UpdateGameMediaAsync(new GameUpdateMediaRequest
            {
                GameId = _original.Id,
                WallpaperSourceFilePath = _newWallpaperPath,
                ResetWallpaperToDefault = _resetToDefault,
                CardCoverSourceFilePath = _newCardCoverPath,
                ResetCardCoverToAuto = _resetCardCoverToAuto
            });
            RequestClose?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }
}
