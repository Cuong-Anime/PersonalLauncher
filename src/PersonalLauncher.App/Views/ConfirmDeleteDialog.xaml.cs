using System.Windows;

namespace PersonalLauncher.App.Views;

public partial class ConfirmDeleteDialog : Window
{
    public ConfirmDeleteDialog(string formattedMessage)
    {
        InitializeComponent();
        PersonalLauncher.App.Services.WindowCornerHelper.EnableRoundedCorners(this);
        MessageText.Text = formattedMessage;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
