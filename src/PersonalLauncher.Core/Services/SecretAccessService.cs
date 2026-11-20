using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Core.Services;

public sealed class SecretAccessService : ISecretAccessService
{
    private readonly ISecurityRepository _securityRepo;
    private readonly IGameRepository _gameRepo;
    private readonly IPasscodeStore _passcodeStore;
    private readonly ISecurityAttemptService _attemptService;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Action? _onAlertQueued;
    private readonly object _sessionLock = new();

    private SecretScope _currentScope = SecretScope.None;
    private long _sessionVersion;
    private long _lastActivityTimestamp;

    public SecretScope CurrentScope
    {
        get
        {
            CheckInactivityTimeout();
            lock (_sessionLock)
                return _currentScope;
        }
    }

    public long SessionVersion
    {
        get
        {
            lock (_sessionLock)
                return _sessionVersion;
        }
    }

    public bool IsUnlockedForBrowse => HasScope(SecretScope.Browse);
    public bool IsUnlockedForManage => HasScope(SecretScope.Manage);

    public event EventHandler<SecretScope>? ScopeChanged;

    public SecretAccessService(
        ISecurityRepository securityRepo,
        IGameRepository gameRepo,
        IPasscodeStore passcodeStore,
        ISecurityAttemptService attemptService,
        IAppLogger logger,
        TimeProvider? timeProvider = null,
        Action? onAlertQueued = null)
    {
        _securityRepo = securityRepo;
        _gameRepo = gameRepo;
        _passcodeStore = passcodeStore;
        _attemptService = attemptService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onAlertQueued = onAlertQueued;
    }

    public async Task<PasscodeHealthStatus> GetPasscodeHealthAsync(CancellationToken ct = default)
    {
        SecurityStateRecord state = await _securityRepo.GetStateAsync(ct);
        bool hasSecretGames = await _gameRepo.HasAnySecretGamesAsync(ct);
        bool requiresPasscode = state.IsPinConfigured || hasSecretGames;
        return await _passcodeStore.CheckHealthAsync(state.GenerationId, requiresPasscode, ct);
    }

    public async Task<bool> IsPinConfiguredAsync(CancellationToken ct = default)
    {
        SecurityStateRecord state = await _securityRepo.GetStateAsync(ct);
        bool hasSecretGames = await _gameRepo.HasAnySecretGamesAsync(ct);
        return state.IsPinConfigured || hasSecretGames;
    }

    public Task<SecurityAttemptSnapshot> GetAttemptSnapshotAsync(CancellationToken ct = default)
        => _attemptService.GetSnapshotAsync(ct);

    public async Task<(bool Success, string? ErrorKey)> SetupFirstPinAsync(
        string pin,
        string confirmPin,
        CancellationToken ct = default)
    {
        PasscodeHealthStatus health = await GetPasscodeHealthAsync(ct);
        if (health != PasscodeHealthStatus.NotConfigured)
        {
            // Never allow overwriting/bypassing an already-configured or corrupted passcode!
            return (false, health == PasscodeHealthStatus.MissingOrCorrupted
                ? "Secret_PasscodeCorrupted"
                : "Secret_AlreadyConfigured");
        }

        if (!PinValidation.IsValidPin(pin))
            return (false, "Secret_PinFormatError");

        if (!string.Equals(pin, confirmPin, StringComparison.Ordinal))
            return (false, "Secret_PinMismatch");

        string generationId = await _passcodeStore.CreatePasscodeAsync(pin, ct);
        SecurityStateRecord existing = await _securityRepo.GetStateAsync(ct);
        SecurityStateRecord updated = existing with
        {
            IsPinConfigured = true,
            GenerationId = generationId,
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };

        await _securityRepo.SaveStateAsync(updated, ct);
        _logger.Info("Security", "Initial Secret PIN configured successfully.");
        return (true, null);
    }

    public async Task<PinVerificationResult> UnlockAsync(
        string pin,
        SecretScope targetScope,
        CancellationToken ct = default)
    {
        if (targetScope == SecretScope.None)
            throw new ArgumentException("Target scope must be Browse or Manage.", nameof(targetScope));

        PasscodeHealthStatus health = await GetPasscodeHealthAsync(ct);
        if (health == PasscodeHealthStatus.NotConfigured)
        {
            return new PinVerificationResult { Status = PinVerificationStatus.NotConfigured };
        }
        if (health == PasscodeHealthStatus.MissingOrCorrupted)
        {
            Lock("passcode_corrupted");
            return new PinVerificationResult { Status = PinVerificationStatus.PasscodeCorrupted };
        }

        PinVerificationResult result = await _attemptService.VerifyPinWithTrackingAsync(
            pin,
            (candidatePin, expectedGenId, token) => _passcodeStore.VerifyPasscodeAsync(candidatePin, expectedGenId, token),
            ct);

        if (result.QueuedAlertEventId is not null)
        {
            _onAlertQueued?.Invoke();
        }

        if (result.Status == PinVerificationStatus.Success)
        {
            SecretScope newScope;
            lock (_sessionLock)
            {
                _currentScope = targetScope;
                _sessionVersion++;
                _lastActivityTimestamp = _timeProvider.GetTimestamp();
                newScope = _currentScope;
            }

            _logger.Info("Security", $"Secret session unlocked with scope={newScope}.");
            ScopeChanged?.Invoke(this, newScope);
        }

        return result;
    }

    public async Task<PinVerificationResult> ChangePinAsync(
        string currentPin,
        string newPin,
        string confirmPin,
        CancellationToken ct = default)
    {
        if (!HasScope(SecretScope.Manage))
            throw new SecretUnauthorizedException(SecretScope.Manage);

        if (!PinValidation.IsValidPin(newPin))
        {
            return new PinVerificationResult
            {
                Status = PinVerificationStatus.InvalidFormat,
                ErrorMessage = "Secret_PinFormatError"
            };
        }

        if (!string.Equals(newPin, confirmPin, StringComparison.Ordinal))
        {
            return new PinVerificationResult
            {
                Status = PinVerificationStatus.InvalidFormat,
                ErrorMessage = "Secret_PinMismatch"
            };
        }

        PasscodeHealthStatus health = await GetPasscodeHealthAsync(ct);
        if (health == PasscodeHealthStatus.MissingOrCorrupted)
        {
            Lock("passcode_corrupted");
            return new PinVerificationResult { Status = PinVerificationStatus.PasscodeCorrupted };
        }

        // Verify current PIN using the SAME shared attempt counter!
        PinVerificationResult verifyResult = await _attemptService.VerifyPinWithTrackingAsync(
            currentPin,
            (candidatePin, expectedGenId, token) => _passcodeStore.VerifyPasscodeAsync(candidatePin, expectedGenId, token),
            ct);

        if (verifyResult.QueuedAlertEventId is not null)
        {
            _onAlertQueued?.Invoke();
        }

        if (verifyResult.Status != PinVerificationStatus.Success)
        {
            if (verifyResult.Status == PinVerificationStatus.LockedOut)
                Lock("pin_change_lockout");
            return verifyResult;
        }

        SecurityStateRecord currentState = await _securityRepo.GetStateAsync(ct);
        string newGenerationId = await _passcodeStore.RotatePasscodeAsync(newPin, currentState.GenerationId, ct);

        try
        {
            SecurityStateRecord updated = currentState with
            {
                IsPinConfigured = true,
                GenerationId = newGenerationId,
                UpdatedAtUtc = _timeProvider.GetUtcNow()
            };
            await _securityRepo.SaveStateAsync(updated, ct);
            RecordActivity();
            _logger.Info("Security", "Secret PIN rotated successfully.");
            return verifyResult;
        }
        catch
        {
            // Rollback passcode file to previous backup so file and DB remain consistent
            await _passcodeStore.RestoreBackupAfterFailedRotationAsync(CancellationToken.None);
            throw;
        }
    }

    public bool HasScope(SecretScope requiredScope)
    {
        if (requiredScope == SecretScope.None)
            return true;

        CheckInactivityTimeout();
        lock (_sessionLock)
        {
            return requiredScope switch
            {
                SecretScope.Browse => _currentScope is SecretScope.Browse or SecretScope.Manage,
                SecretScope.Manage => _currentScope == SecretScope.Manage,
                _ => false
            };
        }
    }

    public void RecordActivity()
    {
        lock (_sessionLock)
        {
            if (_currentScope != SecretScope.None)
            {
                _lastActivityTimestamp = _timeProvider.GetTimestamp();
            }
        }
    }

    public void CheckInactivityTimeout()
    {
        bool shouldLock = false;
        lock (_sessionLock)
        {
            if (_currentScope != SecretScope.None)
            {
                TimeSpan elapsed = _timeProvider.GetElapsedTime(_lastActivityTimestamp);
                if (elapsed >= PinValidation.InactivityLockTimeout)
                {
                    shouldLock = true;
                }
            }
        }

        if (shouldLock)
        {
            Lock("inactivity_timeout");
        }
    }

    public void Lock(string reason = "manual")
    {
        bool changed = false;
        lock (_sessionLock)
        {
            if (_currentScope != SecretScope.None)
            {
                _currentScope = SecretScope.None;
                _sessionVersion++;
                changed = true;
            }
            else
            {
                // Still bump version to cancel any in-flight tasks started before lock
                _sessionVersion++;
            }
        }

        if (changed)
        {
            _logger.Info("Security", $"Secret session locked (reason={reason}).");
            ScopeChanged?.Invoke(this, SecretScope.None);
        }
    }
}
