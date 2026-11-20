namespace PersonalLauncher.Core.Models;

public sealed record SecurityStateRecord
{
    public bool IsPinConfigured { get; init; }
    public string? GenerationId { get; init; }
    public byte[]? ProtectedStateBlob { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record SecurityAttemptSnapshot
{
    public int FailedAttempts { get; init; }
    public DateTimeOffset? CooldownUntilUtc { get; init; }
    public int AlertCycleCount { get; init; }
    public DateTimeOffset LastUpdatedUtc { get; init; }
}

public sealed record PinVerificationResult
{
    public required PinVerificationStatus Status { get; init; }
    public int FailedAttempts { get; init; }
    public int RemainingAttempts { get; init; }
    public DateTimeOffset? CooldownUntilUtc { get; init; }
    public string? QueuedAlertEventId { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record MailSettingsRecord
{
    public string SmtpHost { get; init; } = string.Empty;
    public int SmtpPort { get; init; } = 587;
    public MailTlsMode TlsMode { get; init; } = MailTlsMode.StartTls;
    public string SenderAddress { get; init; } = string.Empty;
    public string SenderDisplayName { get; init; } = "PersonalLauncher";
    public MailAuthMethod AuthMethod { get; init; } = MailAuthMethod.PasswordOrAppPassword;
    public string CredentialRef { get; init; } = "mail-credentials.dat";
    public string RecipientAddress { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record MailOutboxItem
{
    public required string EventId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public required string AlertType { get; init; }
    public MailOutboxStatus Status { get; init; }
    public required string MessageId { get; init; }
    public int RetryCount { get; init; }
    public DateTimeOffset? NextRetryAtUtc { get; init; }
    public DateTimeOffset? LastAttemptAtUtc { get; init; }
    public string? LastErrorSummary { get; init; }
}

public sealed record MailSendResult
{
    public required MailOutboxStatus Outcome { get; init; }
    public string? ErrorSummary { get; init; }
}

public sealed record MailAlertStatusSummary
{
    public bool IsConfigured { get; init; }
    public bool HasStoredCredential { get; init; }
    public int PendingCount { get; init; }
    public int AcceptedCount { get; init; }
    public int FailedCount { get; init; }
    public int UnknownCount { get; init; }
    public MailOutboxItem? LatestItem { get; init; }
}

public sealed record ScanOptions
{
    public required string RootDirectory { get; init; }
    public bool IncludeSubdirectories { get; init; } = true;
}

public sealed record ScanProgress
{
    public int ScannedDirectories { get; init; }
    public int FoundExecutables { get; init; }
    public string CurrentDirectory { get; init; } = string.Empty;
}

public sealed record ScanCandidate
{
    public required string ExePath { get; init; }
    public required string NormalizedExeKey { get; init; }
    public required string DirectParentDirectory { get; init; }
    public required string SuggestedName { get; init; }
    public bool IsLikelyHelper { get; init; }
    public bool IsAlreadyInLibrary { get; init; }
    public bool HasMultipleExesInFolder { get; init; }
    public bool IsSuggestedSelected { get; init; }
    public string? HelperReason { get; init; }
}

public sealed record ScanResult
{
    public IReadOnlyList<ScanCandidate> Candidates { get; init; } = Array.Empty<ScanCandidate>();
    public IReadOnlyList<string> InaccessibleDirectories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SkippedReparsePoints { get; init; } = Array.Empty<string>();
    public bool WasCancelled { get; init; }
}

public sealed record BatchImportItem
{
    public required string DisplayName { get; init; }
    public required string ExePath { get; init; }
    public required string InstallDirectory { get; init; }
}

public sealed record BatchImportResult
{
    public int AddedCount { get; init; }
    public int SkippedDuplicateCount { get; init; }
    public int ErrorCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<GameEntry> AddedGames { get; init; } = Array.Empty<GameEntry>();
}

public sealed record GameLaunchResult
{
    public required GameLaunchState State { get; init; }
    public int? ProcessId { get; init; }
    public string? ErrorMessage { get; init; }
}
