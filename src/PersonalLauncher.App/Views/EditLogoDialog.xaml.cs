using System.Windows;
using Microsoft.Win32;
using PersonalLauncher.App.ViewModels;

namespace PersonalLauncher.App.Views;

public partial class EditLogoDialog : Window
{
    private readonly EditLogoDialogViewModel _vm;

    public EditLogoDialog(EditLogoDialogViewModel vm)
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
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Icon Images (*.png;*.jpg;*.jpeg;*.ico)|*.png;*.jpg;*.jpeg;*.ico",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
            _vm.SelectIconFile(dlg.FileName);
    }

    private void ChooseTitleLogo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Transparent PNG Logo (*.png)|*.png",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
            _vm.SelectTitleLogoFile(dlg.FileName);
    }
}
