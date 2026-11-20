using System.IO;
using System.Windows;
using System.Windows.Media;
using PersonalLauncher.App.Services;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.App.ViewModels;

public sealed class GameItemViewModel : ObservableObject
{
    private readonly IAssetService _assetService;
    private readonly ImageCacheService _imageCache;
    private readonly ISecretAccessService _secretAccess;
    private readonly Func<string?> _getDefaultWallpaperAssetId;

    private ImageSource? _iconImage;
    private ImageSource? _cardWallpaperImage;
    private ImageSource? _titleLogoImage;
    private GameLaunchState _launchState;
    private bool _isSelected;
    private bool _mediaLoaded;

    public GameEntry Model { get; private set; }
    public string Id => Model.Id;
    public string DisplayName => Model.DisplayName;
    public GameVisibility Visibility => Model.Visibility;
    public bool IsSecret => Model.Visibility == GameVisibility.Secret;
    public bool HasTitleLogo => !string.IsNullOrWhiteSpace(Model.TitleLogoAssetId);
    public bool IsMissingExe => _launchState == GameLaunchState.Missing || !File.Exists(Model.ExePath);

    public ImageSource? IconImage
    {
        get => _iconImage;
        private set => SetProperty(ref _iconImage, value);
    }

    public ImageSource? CardWallpaperImage
    {
        get => _cardWallpaperImage;
        private set => SetProperty(ref _cardWallpaperImage, value);
    }

    public ImageSource? TitleLogoImage
    {
        get => _titleLogoImage;
        private set
        {
            if (SetProperty(ref _titleLogoImage, value))
                OnPropertyChanged(nameof(ShowTextTitleFallback));
        }
    }

    public bool ShowTextTitleFallback => TitleLogoImage is null;

    public GameLaunchState LaunchState
    {
        get => _launchState;
        set
        {
            if (SetProperty(ref _launchState, value))
                OnPropertyChanged(nameof(IsMissingExe));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public GameItemViewModel(
        GameEntry model,
        IAssetService assetService,
        ImageCacheService imageCache,
        ISecretAccessService secretAccess,
        GameLaunchState initialLaunchState,
        Func<string?>? getDefaultWallpaperAssetId = null)
    {
        Model = model;
        _assetService = assetService;
        _imageCache = imageCache;
        _secretAccess = secretAccess;
        _launchState = initialLaunchState;
        _getDefaultWallpaperAssetId = getDefaultWallpaperAssetId ?? (() => null);
    }

    public void UpdateModel(GameEntry updated, GameLaunchState launchState)
    {
        Model = updated;
        _launchState = launchState;
        _mediaLoaded = false;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasTitleLogo));
        OnPropertyChanged(nameof(LaunchState));
        OnPropertyChanged(nameof(IsMissingExe));
    }

    public async Task EnsureMediaLoadedAsync(long expectedSecretVersion)
    {
        if (_mediaLoaded)
            return;
        _mediaLoaded = true;

        try
        {
            string iconPath = await _assetService.ResolveAssetPathAsync(Model.IconAssetId, AssetType.Icon, preferThumbnail: true);
            // Library cards ALWAYS display a static image (custom CardCover -> auto video/image thumbnail -> default image)
            string cardCoverPath = await _assetService.ResolveCardCoverImagePathAsync(Model, _getDefaultWallpaperAssetId());
            string? titlePath = !string.IsNullOrWhiteSpace(Model.TitleLogoAssetId)
                ? await _assetService.ResolveAssetPathAsync(Model.TitleLogoAssetId, AssetType.TitleLogo, preferThumbnail: false)
                : null;

            ImageSource? icon = await _imageCache.LoadAsync(iconPath, decodePixelWidth: 128);
            ImageSource? wpThumb = await _imageCache.LoadAsync(cardCoverPath, decodePixelWidth: 420);
            ImageSource? titleLogo = !string.IsNullOrWhiteSpace(titlePath)
                ? await _imageCache.LoadAsync(titlePath, decodePixelWidth: 500)
                : null;

            // Security Rule 8.2: verify Secret session is still valid before pushing images to UI!
            if (IsSecret && (!_secretAccess.HasScope(SecretScope.Browse) || _secretAccess.SessionVersion != expectedSecretVersion))
                return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (IsSecret && (!_secretAccess.HasScope(SecretScope.Browse) || _secretAccess.SessionVersion != expectedSecretVersion))
                    return;

                IconImage = icon;
                CardWallpaperImage = wpThumb;
                TitleLogoImage = titleLogo;
            });
        }
        catch
        {
            _mediaLoaded = false;
        }
    }

    public void ClearSecretMedia()
    {
        IconImage = null;
        CardWallpaperImage = null;
        TitleLogoImage = null;
        _mediaLoaded = false;
    }
}
