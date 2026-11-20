using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;
using PersonalLauncher.Core.Services;

namespace PersonalLauncher.Infrastructure.Mail;

public sealed class MailOutboxService : IMailOutboxService, IDisposable
{
    private readonly IMailRepository _mailRepo;
    private readonly IMailCredentialStore _credentialStore;
    private readonly IMailTransport _transport;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _jitterProvider;
    private readonly SemaphoreSlim _processGate = new(1, 1);

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    public event EventHandler? StatusChanged;

    public MailOutboxService(
        IMailRepository mailRepo,
        IMailCredentialStore credentialStore,
        IMailTransport transport,
        IAppLogger logger,
        TimeProvider? timeProvider = null,
        Func<double>? jitterProvider = null)
    {
        _mailRepo = mailRepo;
        _credentialStore = credentialStore;
        _transport = transport;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jitterProvider = jitterProvider ?? (() => Random.Shared.NextDouble());
    }

    public Task<MailSettingsRecord> GetSettingsAsync(CancellationToken ct = default)
        => _mailRepo.GetSettingsAsync(ct);

    public async Task SaveSettingsAsync(MailSettingsRecord settings, string? newSecretCredential, CancellationToken ct = default)
    {
        MailSettingsRecord normalized = settings with
        {
            RecipientAddress = settings.RecipientAddress.Trim(),
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };

        await _mailRepo.SaveSettingsAsync(normalized, ct);

        if (!string.IsNullOrEmpty(newSecretCredential))
        {
            await _credentialStore.SaveCredentialAsync(newSecretCredential, ct);
        }

        _logger.Info("Mail", "Saved SMTP alert configuration.");
        StatusChanged?.Invoke(this, EventArgs.Empty);
        SignalWorker();
    }

    public async Task<MailAlertStatusSummary> GetStatusSummaryAsync(CancellationToken ct = default)
    {
        MailSettingsRecord settings = await _mailRepo.GetSettingsAsync(ct);
        bool hasCred = _credentialStore.HasCredential();
        bool isConfigured = !string.IsNullOrWhiteSpace(settings.SmtpHost) &&
                            settings.SmtpPort is > 0 and <= 65535 &&
                            !string.IsNullOrWhiteSpace(settings.SenderAddress) &&
                            !string.IsNullOrWhiteSpace(settings.RecipientAddress) &&
                            hasCred;

        IReadOnlyList<MailOutboxItem> items = await _mailRepo.GetAllOutboxItemsAsync(ct);

        return new MailAlertStatusSummary
        {
            IsConfigured = isConfigured,
            HasStoredCredential = hasCred,
            PendingCount = items.Count(i => i.Status == MailOutboxStatus.Pending),
            AcceptedCount = items.Count(i => i.Status == MailOutboxStatus.Accepted),
            FailedCount = items.Count(i => i.Status == MailOutboxStatus.Failed),
            UnknownCount = items.Count(i => i.Status == MailOutboxStatus.Unknown),
            LatestItem = items.FirstOrDefault()
        };
    }

    public async Task<MailSendResult> SendTestEmailAsync(CancellationToken ct = default)
    {
        MailSettingsRecord settings = await _mailRepo.GetSettingsAsync(ct);
        string? credential = await _credentialStore.LoadCredentialAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.SmtpHost) ||
            string.IsNullOrWhiteSpace(settings.SenderAddress) ||
            string.IsNullOrWhiteSpace(settings.RecipientAddress) ||
            string.IsNullOrEmpty(credential))
        {
            return new MailSendResult
            {
                Outcome = MailOutboxStatus.Failed,
                ErrorSummary = "Email alert is not configured."
            };
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string testId = Guid.NewGuid().ToString("N");
        string messageId = $"<test-{testId}@personallauncher.local>";
        string subject = "[PersonalLauncher] Test Security Alert Email";
        string body = BuildTestBody(testId, now);

        MailSendResult result = await _transport.SendAsync(settings, credential, messageId, subject, body, ct);
        _logger.Info("Mail", $"Test email outcome={result.Outcome}");
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        if (!await _processGate.WaitAsync(0, ct))
            return;

        try
        {
            DateTimeOffset utcNow = _timeProvider.GetUtcNow();
            IReadOnlyList<MailOutboxItem> due = await _mailRepo.GetDueOutboxItemsAsync(utcNow, ct);
            if (due.Count == 0)
                return;

            MailSettingsRecord settings = await _mailRepo.GetSettingsAsync(ct);
            string? credential = await _credentialStore.LoadCredentialAsync(ct);
            bool configured = !string.IsNullOrWhiteSpace(settings.SmtpHost) &&
                              settings.SmtpPort is > 0 and <= 65535 &&
                              !string.IsNullOrWhiteSpace(settings.SenderAddress) &&
                              !string.IsNullOrWhiteSpace(settings.RecipientAddress) &&
                              !string.IsNullOrEmpty(credential);

            if (!configured)
            {
                // Keep queued in Pending state without prematurely marking sent or spamming retry count
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            foreach (MailOutboxItem item in due)
            {
                ct.ThrowIfCancellationRequested();

                // Never resend items already Accepted or in Unknown post-DATA outcome!
                if (item.Status is MailOutboxStatus.Accepted or MailOutboxStatus.Unknown)
                    continue;

                string subject = "[PersonalLauncher] Security Alert - 5 Failed Secret PIN Attempts";
                string body = BuildAlertBody(item);

                MailSendResult sendResult = await _transport.SendAsync(
                    settings,
                    credential!,
                    item.MessageId,
                    subject,
                    body,
                    ct);

                DateTimeOffset attemptTime = _timeProvider.GetUtcNow();
                MailOutboxItem updated;

                if (sendResult.Outcome == MailOutboxStatus.Accepted)
                {
                    updated = item with
                    {
                        Status = MailOutboxStatus.Accepted,
                        LastAttemptAtUtc = attemptTime,
                        NextRetryAtUtc = null,
                        LastErrorSummary = null
                    };
                    _logger.Info("Mail", $"Alert event {item.EventId} accepted by SMTP server.");
                }
                else if (sendResult.Outcome == MailOutboxStatus.Unknown)
                {
                    // Do NOT blindly auto-retry Unknown post-DATA timeout outcomes (Rule 9.3)
                    updated = item with
                    {
                        Status = MailOutboxStatus.Unknown,
                        LastAttemptAtUtc = attemptTime,
                        NextRetryAtUtc = null,
                        LastErrorSummary = sendResult.ErrorSummary
                    };
                    _logger.Warn("Mail", $"Alert event {item.EventId} ended in Unknown state: {sendResult.ErrorSummary}");
                }
                else
                {
                    TimeSpan backoff = MailRetryPolicy.GetNextDelay(item.RetryCount, _jitterProvider());
                    updated = item with
                    {
                        Status = MailOutboxStatus.Failed,
                        RetryCount = item.RetryCount + 1,
                        LastAttemptAtUtc = attemptTime,
                        NextRetryAtUtc = attemptTime + backoff,
                        LastErrorSummary = sendResult.ErrorSummary
                    };
                    _logger.Warn("Mail", $"Alert event {item.EventId} failed (retry #{updated.RetryCount}, next in {backoff.TotalSeconds:F0}s): {sendResult.ErrorSummary}");
                }

                await _mailRepo.UpdateOutboxItemAsync(updated, ct);
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    public void SignalWorker()
    {
        try
        {
            if (_wakeSignal.CurrentCount == 0)
                _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public void StartBackgroundWorker()
    {
        if (_workerTask is not null)
            return;

        _workerCts = new CancellationTokenSource();
        CancellationToken token = _workerCts.Token;

        _workerTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ProcessPendingAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error("Mail", "Background mail worker iteration failed.", ex);
                }

                try
                {
                    await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(30), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }, token);
    }

    public async Task StopBackgroundWorkerAsync()
    {
        if (_workerCts is null || _workerTask is null)
            return;

        _workerCts.Cancel();
        try
        {
            await _workerTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
        }
        finally
        {
            _workerCts.Dispose();
            _workerCts = null;
            _workerTask = null;
        }
    }

    public void Dispose()
    {
        _workerCts?.Cancel();
        _workerCts?.Dispose();
        _processGate.Dispose();
        _wakeSignal.Dispose();
    }

    /// <summary>
    /// Strictly minimal alert email content (Rule 9.3):
    /// Contains ONLY launcher name, timestamp, reason "Sai PIN 5 lần", and Event ID.
    /// NEVER contains PIN, secret game names, paths, or images.
    /// </summary>
    public static string BuildAlertBody(MailOutboxItem item)
    {
        return $"""
            PersonalLauncher — Cảnh báo bảo mật / Security Alert

            Ứng dụng / Application: PersonalLauncher
            Lý do / Reason: Sai PIN 5 lần (5 consecutive failed Secret PIN attempts)
            Thời điểm (UTC): {item.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC
            Thời điểm (Local): {item.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}
            Mã sự kiện / Event ID: {item.EventId}
            """;
    }

    private static string BuildTestBody(string testId, DateTimeOffset utcNow)
    {
        return $"""
            PersonalLauncher — Thư kiểm tra cấu hình cảnh báo / Alert Test Email

            Ứng dụng / Application: PersonalLauncher
            Thời điểm (UTC): {utcNow:yyyy-MM-dd HH:mm:ss} UTC
            Mã kiểm tra / Test ID: {testId}
            """;
    }
}
