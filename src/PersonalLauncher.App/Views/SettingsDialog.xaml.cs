using System.Windows;
using Microsoft.Win32;
using PersonalLauncher.App.ViewModels;

namespace PersonalLauncher.App.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsDialogViewModel _vm;

    public SettingsDialog(SettingsDialogViewModel vm)
    {
        InitializeComponent();
        PersonalLauncher.App.Services.WindowCornerHelper.EnableRoundedCorners(this);
        _vm = vm;
        DataContext = _vm;

        _vm.SecretPinInputClearRequested += (_, _) => SettingsSecretPinInput.Clear();
        _vm.SecretPinInputFocusRequested += (_, _) => Dispatcher.InvokeAsync(() => SettingsSecretPinInput.FocusInput());
        _vm.RetrieveMailCredentialPassword += () => MailCredentialBox.Password;
        _vm.ClearMailCredentialPasswordBox += () => MailCredentialBox.Clear();

        Closed += (_, _) => _vm.Dispose();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void ChangePersonalLogo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Logo Images (*.png;*.jpg;*.jpeg;*.ico)|*.png;*.jpg;*.jpeg;*.ico",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
        {
            await _vm.ChangePersonalLogoAsync(dlg.FileName);
        }
    }

    private async void ChangeDefaultWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Wallpaper (Image & Live Video)|*.png;*.jpg;*.jpeg;*.mp4;*.webm;*.mov;*.wmv;*.m4v;*.avi;*.mkv|Live Video (*.mp4;*.webm;*.mov;*.wmv;*.m4v;*.avi;*.mkv)|*.mp4;*.webm;*.mov;*.wmv;*.m4v;*.avi;*.mkv|Static Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
        {
            await _vm.ChangeDefaultWallpaperAsync(dlg.FileName);
        }
    }

    private async void SettingsSecretPinInput_PinCompleted(object? sender, string pin)
    {
        await _vm.SubmitManagePinAsync(pin);
    }

    private void SettingsSecretPinInput_InvalidInputAttempted(object? sender, EventArgs e)
    {
        _vm.HandleInvalidPinInput();
    }
}
