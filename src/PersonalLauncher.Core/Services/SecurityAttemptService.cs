using System.Text;
using System.Text.Json;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Core.Services;

public sealed class SecurityAttemptService : ISecurityAttemptService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PersonalLauncher.SecurityAttemptState.v1");

    private readonly ISecurityRepository _securityRepo;
    private readonly IDataProtector _dataProtector;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long? _sessionCooldownEndTicks;

    public SecurityAttemptService(
        ISecurityRepository securityRepo,
        IDataProtector dataProtector,
        IAppLogger logger,
        TimeProvider? timeProvider = null)
    {
        _securityRepo = securityRepo;
        _dataProtector = dataProtector;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<SecurityStateRecord> GetSecurityStateAsync(CancellationToken ct = default)
        => _securityRepo.GetStateAsync(ct);

    public async Task<SecurityAttemptSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            SecurityStateRecord state = await _securityRepo.GetStateAsync(ct);
            return DecodeSnapshot(state.ProtectedStateBlob, _timeProvider.GetUtcNow());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PinVerificationResult> VerifyPinWithTrackingAsync(
        string pin,
        Func<string, string?, CancellationToken, Task<bool>> verifyFunc,
        CancellationToken ct = default)
    {
        // Reject malformed input before acquiring tracking penalty (Section 9.1)
        if (!PinValidation.IsValidPin(pin))
        {
            return new PinVerificationResult
            {
                Status = PinVerificationStatus.InvalidFormat
            };
        }

        // Prevent concurrent duplicate submits from double-counting
        if (!await _gate.WaitAsync(0, ct))
        {
            return new PinVerificationResult
            {
                Status = PinVerificationStatus.Busy
            };
        }

        try
        {
            DateTimeOffset utcNow = _timeProvider.GetUtcNow();
            SecurityStateRecord state = await _securityRepo.GetStateAsync(ct);
            if (!state.IsPinConfigured)
            {
                return new PinVerificationResult
                {
                    Status = PinVerificationStatus.NotConfigured
                };
            }

            SecurityAttemptSnapshot snapshot = DecodeSnapshot(state.ProtectedStateBlob, utcNow);

            // Check cooldown using both monotonic session timer and persisted UTC timestamp
            if (IsInCooldown(snapshot, utcNow, out DateTimeOffset? effectiveCooldownUntil))
            {
                return new PinVerificationResult
                {
                    Status = PinVerificationStatus.LockedOut,
                    FailedAttempts = snapshot.FailedAttempts,
                    RemainingAttempts = 0,
                    CooldownUntilUtc = effectiveCooldownUntil
                };
            }

            // If previous cooldown has expired, start fresh cycle of up to 5 attempts
            int currentCycleFailures = snapshot.CooldownUntilUtc.HasValue && snapshot.CooldownUntilUtc.Value <= utcNow
                ? 0
                : snapshot.FailedAttempts;

            bool verified = await verifyFunc(pin, state.GenerationId, ct);
            if (verified)
            {
                _sessionCooldownEndTicks = null;
                var resetSnapshot = new SecurityAttemptSnapshot
                {
                    FailedAttempts = 0,
                    CooldownUntilUtc = null,
                    AlertCycleCount = snapshot.AlertCycleCount,
                    LastUpdatedUtc = utcNow
                };

                SecurityStateRecord updated = state with
                {
                    ProtectedStateBlob = EncodeSnapshot(resetSnapshot),
                    UpdatedAtUtc = utcNow
                };

                // Resets current failure streak without deleting previously queued mail alerts
                await _securityRepo.SaveStateAsync(updated, ct);

                return new PinVerificationResult
                {
                    Status = PinVerificationStatus.Success,
                    FailedAttempts = 0,
                    RemainingAttempts = PinValidation.MaxFailedAttemptsBeforeAlert
                };
            }

            // Wrong PIN
            int nextFailures = currentCycleFailures + 1;
            if (nextFailures >= PinValidation.MaxFailedAttemptsBeforeAlert)
            {
                DateTimeOffset cooldownUntil = utcNow + PinValidation.CooldownDuration;
                _sessionCooldownEndTicks = _timeProvider.GetTimestamp() +
                    (long)(PinValidation.CooldownDuration.TotalSeconds * _timeProvider.TimestampFrequency);

                var lockedSnapshot = new SecurityAttemptSnapshot
                {
                    FailedAttempts = PinValidation.MaxFailedAttemptsBeforeAlert,
                    CooldownUntilUtc = cooldownUntil,
                    AlertCycleCount = snapshot.AlertCycleCount + 1,
                    LastUpdatedUtc = utcNow
                };

                string eventId = Guid.NewGuid().ToString("N");
                var outboxItem = new MailOutboxItem
                {
                    EventId = eventId,
                    CreatedAtUtc = utcNow,
                    AlertType = "PinFailed5Times",
                    Status = MailOutboxStatus.Pending,
                    MessageId = $"<alert-{eventId}@personallauncher.local>",
                    RetryCount = 0,
                    NextRetryAtUtc = utcNow
                };

                SecurityStateRecord updated = state with
                {
                    ProtectedStateBlob = EncodeSnapshot(lockedSnapshot),
                    UpdatedAtUtc = utcNow
                };

                // Persist attempt counter, cooldown, and alert outbox in ONE atomic SQLite transaction
                await _securityRepo.SaveAttemptAndOptionalAlertAsync(updated, outboxItem, ct);
                _logger.Warn("Security", $"Secret PIN lockout triggered; alert event queued id={eventId}.");

                return new PinVerificationResult
                {
                    Status = PinVerificationStatus.LockedOut,
                    FailedAttempts = PinValidation.MaxFailedAttemptsBeforeAlert,
                    RemainingAttempts = 0,
                    CooldownUntilUtc = cooldownUntil,
                    QueuedAlertEventId = eventId
                };
            }
            else
            {
                var failedSnapshot = new SecurityAttemptSnapshot
                {
                    FailedAttempts = nextFailures,
                    CooldownUntilUtc = null,
                    AlertCycleCount = snapshot.AlertCycleCount,
                    LastUpdatedUtc = utcNow
                };

                SecurityStateRecord updated = state with
                {
                    ProtectedStateBlob = EncodeSnapshot(failedSnapshot),
                    UpdatedAtUtc = utcNow
                };

                await _securityRepo.SaveStateAsync(updated, ct);
                _logger.Warn("Security", $"Failed PIN verification attempt {nextFailures}/{PinValidation.MaxFailedAttemptsBeforeAlert}.");

                return new PinVerificationResult
                {
                    Status = PinVerificationStatus.WrongPin,
                    FailedAttempts = nextFailures,
                    RemainingAttempts = PinValidation.MaxFailedAttemptsBeforeAlert - nextFailures
                };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsInCooldown(SecurityAttemptSnapshot snapshot, DateTimeOffset utcNow, out DateTimeOffset? cooldownUntil)
    {
        cooldownUntil = snapshot.CooldownUntilUtc;
        if (snapshot.CooldownUntilUtc.HasValue && snapshot.CooldownUntilUtc.Value > utcNow)
            return true;

        if (_sessionCooldownEndTicks.HasValue)
        {
            long nowTicks = _timeProvider.GetTimestamp();
            if (nowTicks < _sessionCooldownEndTicks.Value)
            {
                double remainingSec = (_sessionCooldownEndTicks.Value - nowTicks) / (double)_timeProvider.TimestampFrequency;
                cooldownUntil = utcNow + TimeSpan.FromSeconds(Math.Ceiling(remainingSec));
                return true;
            }
            _sessionCooldownEndTicks = null;
        }

        return false;
    }

    private byte[] EncodeSnapshot(SecurityAttemptSnapshot snapshot)
    {
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        return _dataProtector.Protect(jsonBytes, Entropy);
    }

    private SecurityAttemptSnapshot DecodeSnapshot(byte[]? protectedBlob, DateTimeOffset utcNow)
    {
        if (protectedBlob is null || protectedBlob.Length == 0)
        {
            return new SecurityAttemptSnapshot
            {
                FailedAttempts = 0,
                CooldownUntilUtc = null,
                AlertCycleCount = 0,
                LastUpdatedUtc = utcNow
            };
        }

        try
        {
            byte[] plain = _dataProtector.Unprotect(protectedBlob, Entropy);
            return JsonSerializer.Deserialize<SecurityAttemptSnapshot>(plain)
                ?? new SecurityAttemptSnapshot { LastUpdatedUtc = utcNow };
        }
        catch (Exception ex)
        {
            // Conservative fallback if blob cannot be decrypted: do not silently grant bonus attempts
            _logger.Error("Security", "Failed to unprotect security attempt state blob.", ex);
            return new SecurityAttemptSnapshot
            {
                FailedAttempts = PinValidation.MaxFailedAttemptsBeforeAlert - 1,
                CooldownUntilUtc = null,
                AlertCycleCount = 0,
                LastUpdatedUtc = utcNow
            };
        }
    }
}
