using System.Windows;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.App.Views;

public partial class ManagePinPromptDialog : Window
{
    private readonly ISecretAccessService _secretAccess;
    private readonly ILocalizationService _localization;
    private bool _verifying;

    public ManagePinPromptDialog(ISecretAccessService secretAccess, ILocalizationService localization)
    {
        InitializeComponent();
        PersonalLauncher.App.Services.WindowCornerHelper.EnableRoundedCorners(this);
        _secretAccess = secretAccess;
        _localization = localization;
        Loaded += (_, _) => PinControl.FocusInput();
    }

    private async void PinControl_PinCompleted(object? sender, string pin)
    {
        if (_verifying) return;
        _verifying = true;
        ErrorText.Text = string.Empty;
        try
        {
            PinVerificationResult result = await _secretAccess.UnlockAsync(pin, SecretScope.Manage);
            if (result.Status == PinVerificationStatus.Success)
            {
                DialogResult = true;
                Close();
                return;
            }

            ErrorText.Text = result.Status switch
            {
                PinVerificationStatus.WrongPin => _localization.Format("Secret_WrongPinFormat", result.RemainingAttempts),
                PinVerificationStatus.LockedOut => _localization.Format("Secret_LockedOutFormat", FormatRemaining(result.CooldownUntilUtc)),
                PinVerificationStatus.PasscodeCorrupted => _localization.GetString("Secret_PasscodeCorrupted"),
                _ => _localization.GetString("Secret_PinFormatError")
            };

            if (result.Status == PinVerificationStatus.LockedOut)
                PinControl.IsEnabled = false;
        }
        finally
        {
            _verifying = false;
            PinControl.Clear();
            if (PinControl.IsEnabled)
                PinControl.FocusInput();
        }
    }

    private void PinControl_InvalidInputAttempted(object? sender, EventArgs e)
    {
        ErrorText.Text = _localization.GetString("Secret_PinFormatError");
    }

    private static string FormatRemaining(DateTimeOffset? untilUtc)
    {
        if (!untilUtc.HasValue) return "05:00";
        TimeSpan rem = untilUtc.Value - DateTimeOffset.UtcNow;
        if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
        return $"{(int)rem.TotalMinutes:D2}:{rem.Seconds:D2}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
