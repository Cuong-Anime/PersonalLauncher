namespace PersonalLauncher.Core.Services;

public static class PinValidation
{
    public const int RequiredLength = 4;
    public const int MaxFailedAttemptsBeforeAlert = 5;
    public static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan InactivityLockTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Validates strictly 4 ASCII digits '0'-'9'. Preserves leading zeros (e.g., "0042").
    /// </summary>
    public static bool IsValidPin(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length != RequiredLength)
            return false;

        for (int i = 0; i < RequiredLength; i++)
        {
            char c = pin[i];
            if (c < '0' || c > '9')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Extracts up to 4 ASCII digits from pasted text only if the entire trimmed text is strictly 4 ASCII digits.
    /// </summary>
    public static bool TryExtractPastedPin(string? clipboardText, out string pin)
    {
        pin = string.Empty;
        if (string.IsNullOrWhiteSpace(clipboardText))
            return false;

        string trimmed = clipboardText.Trim();
        if (!IsValidPin(trimmed))
            return false;

        pin = trimmed;
        return true;
    }
}
