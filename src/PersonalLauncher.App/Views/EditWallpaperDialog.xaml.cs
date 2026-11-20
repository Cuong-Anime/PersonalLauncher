using System.IO;
using System.Windows;
using Microsoft.Win32;
using PersonalLauncher.App.Services;
using PersonalLauncher.App.ViewModels;

namespace PersonalLauncher.App.Views;

public partial class EditWallpaperDialog : Window
{
    private readonly EditWallpaperDialogViewModel _vm;

    public EditWallpaperDialog(EditWallpaperDialogViewModel vm)
    {
        InitializeComponent();
        WindowCornerHelper.EnableRoundedCorners(this);
        _vm = vm;
        DataContext = _vm;

        _vm.PreviewVideoChanged += (_, path) => Dispatcher.InvokeAsync(() => UpdateVideoPreview(path));
        Loaded += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_vm.PreviewVideoPath))
                UpdateVideoPreview(_vm.PreviewVideoPath);
        };
        _vm.RequestClose += (_, ok) =>
        {
            PreviewVideo.StopAndClear();
            DialogResult = ok;
            Close();
        };
        Closed += (_, _) => PreviewVideo.Dispose();
    }

    private void UpdateVideoPreview(string? videoPath)
    {
        if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
            PreviewVideo.Play(videoPath);
        else
            PreviewVideo.StopAndClear();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        PreviewVideo.StopAndClear();
        DialogResult = false;
        Close();
    }

    private void SelectImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Wallpaper (Image & Live Video)|*.png;*.jpg;*.jpeg;*.mp4;*.webm;*.mov;*.wmv;*.m4v;*.avi;*.mkv|Live Video (*.mp4;*.webm;*.mov;*.wmv;*.m4v;*.avi;*.mkv)|*.mp4;*.webm;*.mov;*.wmv;*.m4v;*.avi;*.mkv|Static Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
            _vm.SelectWallpaperFile(dlg.FileName);
    }

    private void SelectCover_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Static Cover Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
            _vm.SelectCardCoverFile(dlg.FileName);
    }
}
