using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using PersonalLauncher.App.Services;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;
using PersonalLauncher.Core.Services;

namespace PersonalLauncher.App.ViewModels;

public sealed class ScanCandidateRow : ObservableObject
{
    private bool _isSelected;
    private string _suggestedName;

    public ScanCandidate Model { get; }
    public string ExePath => Model.ExePath;
    public string ExeFileName => Path.GetFileName(Model.ExePath);
    public string DirectParentDirectory => Model.DirectParentDirectory;
    public bool IsLikelyHelper => Model.IsLikelyHelper;
    public bool IsAlreadyInLibrary => Model.IsAlreadyInLibrary;
    public bool HasMultipleExesInFolder => Model.HasMultipleExesInFolder;
    public bool CanSelect => !IsAlreadyInLibrary;
    public string StatusLabel { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (CanSelect)
                SetProperty(ref _isSelected, value);
        }
    }

    public string SuggestedName
    {
        get => _suggestedName;
        set => SetProperty(ref _suggestedName, value);
    }

    public ScanCandidateRow(ScanCandidate model, ILocalizationService loc)
    {
        Model = model;
        _suggestedName = model.SuggestedName;
        _isSelected = model.IsSuggestedSelected && !model.IsAlreadyInLibrary;

        if (model.IsAlreadyInLibrary)
            StatusLabel = loc.GetString("Scan_StatusDuplicate");
        else if (model.IsLikelyHelper)
            StatusLabel = loc.GetString("Scan_StatusHelper");
        else if (model.HasMultipleExesInFolder)
            StatusLabel = loc.GetString("Scan_StatusMultiExe");
        else
            StatusLabel = loc.GetString("Scan_StatusNew");
    }
}

public sealed class AddGameDialogViewModel : ObservableObject
{
    private readonly IGameLibraryService _gameLibrary;
    private readonly IGameScanService _scanService;
    private readonly IAppPaths _paths;
    private readonly ILocalizationService _localization;
    private readonly ImageCacheService _imageCache;

    private CancellationTokenSource? _scanCts;
    private readonly List<ScanCandidateRow> _allScannedRows = new();

    // Tab 1: Manual
    private string _displayName = string.Empty;
    private string _exePath = string.Empty;
    private string _installDirectory = string.Empty;
    private string _workingDirectory = string.Empty;
    private string _argumentsMultiline = string.Empty;
    private string _iconSourcePath = string.Empty;
    private string _wallpaperSourcePath = string.Empty;
    private string _cardCoverSourcePath = string.Empty;
    private string _titleLogoSourcePath = string.Empty;
    private ImageSource? _iconPreview;
    private ImageSource? _wallpaperPreview;
    private ImageSource? _titleLogoPreview;
    private bool _isWallpaperVideo;
    private string _manualError = string.Empty;
    private bool _isSaving;

    // Tab 2: Auto Folder Scan
    private string _scanRootDirectory = string.Empty;
    private bool _includeSubdirectories = true;
    private bool _showHelperCandidates;
    private bool _isScanning;
    private bool _isImporting;
    private string _scanProgressText = string.Empty;
    private string _scanCurrentDir = string.Empty;
    private string _scanSummaryText = string.Empty;
    private string _scanSkippedText = string.Empty;

    public GameVisibility TargetVisibility { get; }
    public string DialogTitle => TargetVisibility == GameVisibility.Secret
        ? _localization.GetString("AddGame_TitleSecret")
        : _localization.GetString("AddGame_Title");

    public ObservableCollection<ScanCandidateRow> VisibleScanCandidates { get; } = new();

    public GameEntry? CreatedGame { get; private set; }
    public BatchImportResult? LastBatchResult { get; private set; }
    public event EventHandler<bool>? RequestClose;

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

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
                    if (string.IsNullOrWhiteSpace(InstallDirectory))
                        InstallDirectory = dir;
                    if (string.IsNullOrWhiteSpace(WorkingDirectory))
                        WorkingDirectory = dir;
                    if (string.IsNullOrWhiteSpace(DisplayName))
                        DisplayName = PathNormalization.GetDirectParentFolderName(value);
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

    public string IconSourcePath
    {
        get => _iconSourcePath;
        set
        {
            if (SetProperty(ref _iconSourcePath, value))
                RefreshManualPreviews();
        }
    }

    public string WallpaperSourcePath
    {
        get => _wallpaperSourcePath;
        set
        {
            if (SetProperty(ref _wallpaperSourcePath, value))
            {
                IsWallpaperVideo = MediaFormats.IsVideoPath(value);
                RefreshManualPreviews();
            }
        }
    }

    public string CardCoverSourcePath
    {
        get => _cardCoverSourcePath;
        set
        {
            if (SetProperty(ref _cardCoverSourcePath, value))
                RefreshManualPreviews();
        }
    }

    public bool IsWallpaperVideo
    {
        get => _isWallpaperVideo;
        private set => SetProperty(ref _isWallpaperVideo, value);
    }

    public string TitleLogoSourcePath
    {
        get => _titleLogoSourcePath;
        set
        {
            if (SetProperty(ref _titleLogoSourcePath, value))
                RefreshManualPreviews();
        }
    }

    public ImageSource? IconPreview
    {
        get => _iconPreview;
        private set => SetProperty(ref _iconPreview, value);
    }

    public ImageSource? WallpaperPreview
    {
        get => _wallpaperPreview;
        private set => SetProperty(ref _wallpaperPreview, value);
    }

    public ImageSource? TitleLogoPreview
    {
        get => _titleLogoPreview;
        private set => SetProperty(ref _titleLogoPreview, value);
    }

    public string ManualError
    {
        get => _manualError;
        private set => SetProperty(ref _manualError, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
                SaveManualCommand.RaiseCanExecuteChanged();
        }
    }

    // Auto scan props
    public string ScanRootDirectory
    {
        get => _scanRootDirectory;
        set
        {
            if (SetProperty(ref _scanRootDirectory, value))
                StartScanCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IncludeSubdirectories
    {
        get => _includeSubdirectories;
        set => SetProperty(ref _includeSubdirectories, value);
    }

    public bool ShowHelperCandidates
    {
        get => _showHelperCandidates;
        set
        {
            if (SetProperty(ref _showHelperCandidates, value))
                RefreshVisibleCandidates();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                StartScanCommand.RaiseCanExecuteChanged();
                CancelScanCommand.RaiseCanExecuteChanged();
                ImportSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (SetProperty(ref _isImporting, value))
                ImportSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    public string ScanProgressText
    {
        get => _scanProgressText;
        private set => SetProperty(ref _scanProgressText, value);
    }

    public string ScanCurrentDir
    {
        get => _scanCurrentDir;
        private set => SetProperty(ref _scanCurrentDir, value);
    }

    public string ScanSummaryText
    {
        get => _scanSummaryText;
        private set => SetProperty(ref _scanSummaryText, value);
    }

    public string ScanSkippedText
    {
        get => _scanSkippedText;
        private set => SetProperty(ref _scanSkippedText, value);
    }

    public AsyncRelayCommand SaveManualCommand { get; }
    public AsyncRelayCommand StartScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public AsyncRelayCommand ImportSelectedCommand { get; }

    public AddGameDialogViewModel(
        IGameLibraryService gameLibrary,
        IGameScanService scanService,
        IAppPaths paths,
        ILocalizationService localization,
        ImageCacheService imageCache,
        GameVisibility targetVisibility = GameVisibility.Public)
    {
        _gameLibrary = gameLibrary;
        _scanService = scanService;
        _paths = paths;
        _localization = localization;
        _imageCache = imageCache;
        TargetVisibility = targetVisibility;

        SaveManualCommand = new AsyncRelayCommand(SaveManualAsync, () => !IsSaving);
        StartScanCommand = new AsyncRelayCommand(StartScanAsync, () => !IsScanning && Directory.Exists(ScanRootDirectory));
        CancelScanCommand = new RelayCommand(() => _scanCts?.Cancel(), () => IsScanning);
        ImportSelectedCommand = new AsyncRelayCommand(ImportSelectedAsync, () => !IsScanning && !IsImporting && _allScannedRows.Any(r => r.IsSelected));

        RefreshManualPreviews();
    }

    public void RefreshManualPreviews()
    {
        string iconPath = !string.IsNullOrWhiteSpace(IconSourcePath) && File.Exists(IconSourcePath)
            ? IconSourcePath
            : _paths.DefaultGameIconPath;

        // Static card preview prioritizes CardCoverSourcePath, then static WallpaperSourcePath, then default
        string wpPreviewPath = !string.IsNullOrWhiteSpace(CardCoverSourcePath) && File.Exists(CardCoverSourcePath)
            ? CardCoverSourcePath
            : (!string.IsNullOrWhiteSpace(WallpaperSourcePath) && File.Exists(WallpaperSourcePath) && !MediaFormats.IsVideoPath(WallpaperSourcePath)
                ? WallpaperSourcePath
                : _paths.DefaultWallpaperPath);

        IconPreview = _imageCache.Load(iconPath, decodePixelWidth: 128);
        WallpaperPreview = _imageCache.Load(wpPreviewPath, decodePixelWidth: 640);
        TitleLogoPreview = !string.IsNullOrWhiteSpace(TitleLogoSourcePath) && File.Exists(TitleLogoSourcePath)
            ? _imageCache.Load(TitleLogoSourcePath, decodePixelWidth: 400)
            : null;
    }

    private async Task SaveManualAsync()
    {
        if (IsSaving)
            return;

        ManualError = string.Empty;
        string cleanName = TextNormalization.NormalizeDisplayName(DisplayName);
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            ManualError = _localization.GetString("AddGame_ErrorNameRequired");
            return;
        }

        string cleanExe = ExePath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(cleanExe) || !File.Exists(cleanExe) ||
            !cleanExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            ManualError = _localization.GetString("AddGame_ErrorExeRequired");
            return;
        }

        IsSaving = true;
        try
        {
            string[] args = string.IsNullOrWhiteSpace(ArgumentsMultiline)
                ? Array.Empty<string>()
                : ArgumentsMultiline.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var request = new GameCreateRequest
            {
                DisplayName = cleanName,
                ExePath = cleanExe,
                InstallDirectory = string.IsNullOrWhiteSpace(InstallDirectory) ? null : InstallDirectory.Trim(),
                WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim(),
                Arguments = args,
                IconSourceFilePath = string.IsNullOrWhiteSpace(IconSourcePath) ? null : IconSourcePath.Trim(),
                WallpaperSourceFilePath = string.IsNullOrWhiteSpace(WallpaperSourcePath) ? null : WallpaperSourcePath.Trim(),
                CardCoverSourceFilePath = string.IsNullOrWhiteSpace(CardCoverSourcePath) ? null : CardCoverSourcePath.Trim(),
                TitleLogoSourceFilePath = string.IsNullOrWhiteSpace(TitleLogoSourcePath) ? null : TitleLogoSourcePath.Trim(),
                Visibility = TargetVisibility
            };

            CreatedGame = await _gameLibrary.AddManualGameAsync(request);
            RequestClose?.Invoke(this, true);
        }
        catch (DuplicateGameException)
        {
            ManualError = _localization.GetString("AddGame_ErrorDuplicate");
        }
        catch (AssetValidationException ex)
        {
            ManualError = ex.Message;
        }
        catch (Exception ex)
        {
            ManualError = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task StartScanAsync()
    {
        if (IsScanning || !Directory.Exists(ScanRootDirectory))
            return;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        ScanSummaryText = string.Empty;
        ScanSkippedText = string.Empty;
        _allScannedRows.Clear();
        VisibleScanCandidates.Clear();

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressText = _localization.Format("Scan_ProgressFormat", p.ScannedDirectories, p.FoundExecutables);
            ScanCurrentDir = p.CurrentDirectory;
        });

        try
        {
            ScanResult result = await _scanService.ScanDirectoryAsync(
                new ScanOptions
                {
                    RootDirectory = ScanRootDirectory,
                    IncludeSubdirectories = IncludeSubdirectories
                },
                progress,
                _scanCts.Token);

            foreach (ScanCandidate candidate in result.Candidates)
            {
                var row = new ScanCandidateRow(candidate, _localization);
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ScanCandidateRow.IsSelected))
                        ImportSelectedCommand.RaiseCanExecuteChanged();
                };
                _allScannedRows.Add(row);
            }

            RefreshVisibleCandidates();

            if (result.InaccessibleDirectories.Count > 0 || result.SkippedReparsePoints.Count > 0)
            {
                ScanSkippedText = _localization.Format(
                    "Scan_SkippedInfo",
                    result.InaccessibleDirectories.Count,
                    result.SkippedReparsePoints.Count);
            }

            ScanCurrentDir = string.Empty;
        }
        catch (OperationCanceledException)
        {
            ScanCurrentDir = string.Empty;
        }
        catch (Exception ex)
        {
            ScanSummaryText = ex.Message;
        }
        finally
        {
            IsScanning = false;
            ImportSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    private void RefreshVisibleCandidates()
    {
        VisibleScanCandidates.Clear();
        foreach (ScanCandidateRow row in _allScannedRows)
        {
            if (ShowHelperCandidates || !row.IsLikelyHelper)
                VisibleScanCandidates.Add(row);
        }
        ImportSelectedCommand.RaiseCanExecuteChanged();
    }

    private async Task ImportSelectedAsync()
    {
        var selected = _allScannedRows
            .Where(r => r.IsSelected && !r.IsAlreadyInLibrary)
            .Select(r => new BatchImportItem
            {
                DisplayName = r.SuggestedName,
                ExePath = r.ExePath,
                InstallDirectory = r.DirectParentDirectory
            })
            .ToList();

        if (selected.Count == 0)
            return;

        IsImporting = true;
        try
        {
            BatchImportResult result = await _gameLibrary.ImportScannedGamesAsync(selected, TargetVisibility);
            LastBatchResult = result;
            ScanSummaryText = _localization.Format("Scan_ResultSummary", result.AddedCount, result.SkippedDuplicateCount, result.ErrorCount);
            if (result.AddedCount > 0 && result.ErrorCount == 0)
            {
                CreatedGame = result.AddedGames.FirstOrDefault();
                RequestClose?.Invoke(this, true);
            }
        }
        catch (Exception ex)
        {
            ScanSummaryText = ex.Message;
        }
        finally
        {
            IsImporting = false;
        }
    }

    public void CancelPendingOperations()
    {
        _scanCts?.Cancel();
    }
}
