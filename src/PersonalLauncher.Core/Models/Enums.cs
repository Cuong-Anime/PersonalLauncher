namespace PersonalLauncher.Core.Models;

public enum GameVisibility
{
    Public = 0,
    Secret = 1
}

public enum AssetType
{
    AppLogo = 0,
    Icon = 1,
    TitleLogo = 2,
    Wallpaper = 3,
    CardCover = 4
}

public enum CloseAction
{
    MinimizeToTray = 0,
    ExitApplication = 1
}

public enum SecretScope
{
    None = 0,
    Browse = 1,
    Manage = 2
}

public enum MailTlsMode
{
    StartTls = 0,
    SslOnConnect = 1
}

public enum MailAuthMethod
{
    PasswordOrAppPassword = 0,
    OAuth2AccessToken = 1
}

public enum MailOutboxStatus
{
    Pending = 0,
    Accepted = 1,
    Failed = 2,
    Unknown = 3
}

public enum GameLaunchState
{
    Ready = 0,
    Launching = 1,
    Running = 2,
    Missing = 3,
    Error = 4
}

public enum PinVerificationStatus
{
    Success = 0,
    InvalidFormat = 1,
    WrongPin = 2,
    LockedOut = 3,
    NotConfigured = 4,
    PasscodeCorrupted = 5,
    Busy = 6
}

public enum PasscodeHealthStatus
{
    NotConfigured = 0,
    Healthy = 1,
    MissingOrCorrupted = 2
}

public enum StartupStatus
{
    Disabled = 0,
    Enabled = 1,
    DisabledByWindows = 2,
    Error = 3
}
