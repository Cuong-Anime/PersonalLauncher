using System.Windows;
using Microsoft.Win32;
using PersonalLauncher.App.ViewModels;

namespace PersonalLauncher.App.Views;

public partial class AddGameDialog : Window
{
    private readonly AddGameDialogViewModel _vm;

    public AddGameDialog(AddGameDialogViewModel vm)
    {
        InitializeComponent();
        PersonalLauncher.App.Services.WindowCornerHelper.EnableRoundedCorners(this);
        _vm = vm;
        DataContext = _vm;
        _vm.RequestClose += (_, ok) =>
        {
            DialogResult = ok;
            Close();
        };
        Closed += (_, _) => _vm.CancelPendingOperations();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Executable Files (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
            _vm.ExePath = dlg.FileName;
    }

    private void BrowseInstallDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true)
            _vm.InstallDirectory = dlg.FolderName;
    }

    private void BrowseWorkingDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true)
            _vm.WorkingDirectory = dlg.FolderName;
    }

    private void BrowseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Icon Images (*.png;*.jpg;*.jpeg;*.ico)|*.png;*.jpg;*.jpeg;*.ico",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
            _vm.IconSourcePath = dlg.FileName;
    }

    private void ClearIcon_Click(object sender, RoutedEventArgs e) => _vm.IconSourcePath = string.Empty;

    private void BrowseWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Wallpaper (Image & Live Video)|*.png;*.jpg;*.jpeg;*.mp4;*.webm;*.mov;*.wmv;*.m4v;*.avi;*.mkv|Live Video (*.mp4;*.webm;*.mov;*.wmv;*.m4v;*.avi;*.mkv)|*.mp4;*.webm;*.mov;*.wmv;*.m4v;*.avi;*.mkv|Static Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
            _vm.WallpaperSourcePath = dlg.FileName;
    }

    private void ClearWallpaper_Click(object sender, RoutedEventArgs e) => _vm.WallpaperSourcePath = string.Empty;

    private void BrowseCardCover_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Static Cover Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
            _vm.CardCoverSourcePath = dlg.FileName;
    }

    private void ClearCardCover_Click(object sender, RoutedEventArgs e) => _vm.CardCoverSourcePath = string.Empty;

    private void BrowseTitleLogo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Transparent PNG Logo (*.png)|*.png",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
            _vm.TitleLogoSourcePath = dlg.FileName;
    }

    private void ClearTitleLogo_Click(object sender, RoutedEventArgs e) => _vm.TitleLogoSourcePath = string.Empty;

    private void BrowseScanRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true)
            _vm.ScanRootDirectory = dlg.FolderName;
    }
}
