using System.Windows;
using Microsoft.Win32;
using PersonalLauncher.App.ViewModels;

namespace PersonalLauncher.App.Views;

public partial class EditLocationDialog : Window
{
    private readonly EditLocationDialogViewModel _vm;

    public EditLocationDialog(EditLocationDialogViewModel vm)
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

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Executable Files (*.exe)|*.exe", CheckFileExists = true };
        if (dlg.ShowDialog(this) == true) _vm.ExePath = dlg.FileName;
    }

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true) _vm.InstallDirectory = dlg.FolderName;
    }

    private void BrowseWorkDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true) _vm.WorkingDirectory = dlg.FolderName;
    }
}
