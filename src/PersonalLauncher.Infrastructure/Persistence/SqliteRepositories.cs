using Microsoft.Data.Sqlite;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Infrastructure.Persistence;

public sealed class SqliteAssetRepository : IAssetRepository
{
    private readonly SqliteDatabase _db;

    public SqliteAssetRepository(SqliteDatabase db) => _db = db;

    public async Task<AssetRecord?> GetByIdAsync(string assetId, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, AssetType, RelativePath, ThumbnailRelativePath, Sha256,
                   FileSizeBytes, PixelWidth, PixelHeight, CreatedAtUtc
            FROM Assets
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", assetId);

        await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapAsset(r) : null;
    }

    public async Task InsertAsync(AssetRecord asset, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Assets (
                Id, AssetType, RelativePath, ThumbnailRelativePath, Sha256,
                FileSizeBytes, PixelWidth, PixelHeight, CreatedAtUtc
            ) VALUES (
                @id, @type, @relPath, @thumbPath, @sha, @size, @w, @h, @created
            );
            """;
        cmd.Parameters.AddWithValue("@id", asset.Id);
        cmd.Parameters.AddWithValue("@type", asset.AssetType.ToString());
        cmd.Parameters.AddWithValue("@relPath", asset.RelativePath);
        cmd.Parameters.AddWithValue("@thumbPath", (object?)asset.ThumbnailRelativePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sha", asset.Sha256);
        cmd.Parameters.AddWithValue("@size", asset.FileSizeBytes);
        cmd.Parameters.AddWithValue("@w", asset.PixelWidth);
        cmd.Parameters.AddWithValue("@h", asset.PixelHeight);
        cmd.Parameters.AddWithValue("@created", asset.CreatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string assetId, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Assets WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", assetId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AssetRecord>> GetUnreferencedAssetsAsync(
        string? activeAppLogoAssetId,
        string? activeDefaultWallpaperAssetId = null,
        CancellationToken ct = default)
    {
        var list = new List<AssetRecord>();
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.Id, a.AssetType, a.RelativePath, a.ThumbnailRelativePath, a.Sha256,
                   a.FileSizeBytes, a.PixelWidth, a.PixelHeight, a.CreatedAtUtc
            FROM Assets a
            WHERE (@appLogoId IS NULL OR a.Id <> @appLogoId)
              AND (@defaultWpId IS NULL OR a.Id <> @defaultWpId)
              AND NOT EXISTS (
                  SELECT 1 FROM Games g
                  WHERE g.IconAssetId = a.Id
                     OR g.TitleLogoAssetId = a.Id
                     OR g.WallpaperAssetId = a.Id
                     OR g.CardCoverAssetId = a.Id
              );
            """;
        cmd.Parameters.AddWithValue("@appLogoId", (object?)activeAppLogoAssetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@defaultWpId", (object?)activeDefaultWallpaperAssetId ?? DBNull.Value);

        await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(MapAsset(r));
        }
        return list;
    }

    private static AssetRecord MapAsset(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        AssetType = Enum.TryParse<AssetType>(r.GetString(1), true, out var t) ? t : AssetType.Icon,
        RelativePath = r.GetString(2),
        ThumbnailRelativePath = r.IsDBNull(3) ? null : r.GetString(3),
        Sha256 = r.GetString(4),
        FileSizeBytes = r.GetInt64(5),
        PixelWidth = r.GetInt32(6),
        PixelHeight = r.GetInt32(7),
        CreatedAtUtc = DateTimeOffset.Parse(r.GetString(8))
    };
}

public sealed class SqliteSecurityRepository : ISecurityRepository
{
    private readonly SqliteDatabase _db;

    public SqliteSecurityRepository(SqliteDatabase db) => _db = db;

    public async Task<SecurityStateRecord> GetStateAsync(CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT IsPinConfigured, GenerationId, ProtectedStateBlob, UpdatedAtUtc
            FROM SecurityState
            WHERE Id = 1;
            """;

        await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            return new SecurityStateRecord
            {
                IsPinConfigured = r.GetInt32(0) != 0,
                GenerationId = r.IsDBNull(1) ? null : r.GetString(1),
                ProtectedStateBlob = r.IsDBNull(2) ? null : (byte[])r.GetValue(2),
                UpdatedAtUtc = DateTimeOffset.Parse(r.GetString(3))
            };
        }

        return new SecurityStateRecord { UpdatedAtUtc = DateTimeOffset.UtcNow };
    }

    public async Task SaveStateAsync(SecurityStateRecord state, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        BindSaveState(cmd, state);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAttemptAndOptionalAlertAsync(
        SecurityStateRecord updatedState,
        MailOutboxItem? newOutboxItem,
        CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using (SqliteCommand stateCmd = conn.CreateCommand())
            {
                stateCmd.Transaction = tx;
                BindSaveState(stateCmd, updatedState);
                await stateCmd.ExecuteNonQueryAsync(ct);
            }

            if (newOutboxItem is not null)
            {
                await using SqliteCommand outboxCmd = conn.CreateCommand();
                outboxCmd.Transaction = tx;
                SqliteMailRepository.BindInsertOutbox(outboxCmd, newOutboxItem);
                await outboxCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static void BindSaveState(SqliteCommand cmd, SecurityStateRecord state)
    {
        cmd.CommandText = """
            INSERT INTO SecurityState (Id, IsPinConfigured, GenerationId, ProtectedStateBlob, UpdatedAtUtc)
            VALUES (1, @configured, @genId, @blob, @updated)
            ON CONFLICT(Id) DO UPDATE SET
                IsPinConfigured = excluded.IsPinConfigured,
                GenerationId = excluded.GenerationId,
                ProtectedStateBlob = excluded.ProtectedStateBlob,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        cmd.Parameters.AddWithValue("@configured", state.IsPinConfigured ? 1 : 0);
        cmd.Parameters.AddWithValue("@genId", (object?)state.GenerationId ?? DBNull.Value);
        var blobParam = cmd.Parameters.Add("@blob", SqliteType.Blob);
        blobParam.Value = (object?)state.ProtectedStateBlob ?? DBNull.Value;
        cmd.Parameters.AddWithValue("@updated", state.UpdatedAtUtc.ToString("O"));
    }
}

public sealed class SqliteMailRepository : IMailRepository
{
    private readonly SqliteDatabase _db;

    public SqliteMailRepository(SqliteDatabase db) => _db = db;

    public async Task<MailSettingsRecord> GetSettingsAsync(CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SmtpHost, SmtpPort, TlsMode, SenderAddress, SenderDisplayName,
                   AuthMethod, CredentialRef, RecipientAddress, UpdatedAtUtc
            FROM MailSettings
            WHERE Id = 1;
            """;

        await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            return new MailSettingsRecord
            {
                SmtpHost = r.GetString(0),
                SmtpPort = r.GetInt32(1),
                TlsMode = Enum.TryParse<MailTlsMode>(r.GetString(2), true, out var tls) ? tls : MailTlsMode.StartTls,
                SenderAddress = r.GetString(3),
                SenderDisplayName = r.GetString(4),
                AuthMethod = Enum.TryParse<MailAuthMethod>(r.GetString(5), true, out var auth) ? auth : MailAuthMethod.PasswordOrAppPassword,
                CredentialRef = r.GetString(6),
                RecipientAddress = r.IsDBNull(7) ? string.Empty : r.GetString(7),
                UpdatedAtUtc = DateTimeOffset.Parse(r.GetString(8))
            };
        }

        return new MailSettingsRecord();
    }

    public async Task SaveSettingsAsync(MailSettingsRecord settings, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MailSettings (
                Id, SmtpHost, SmtpPort, TlsMode, SenderAddress, SenderDisplayName,
                AuthMethod, CredentialRef, RecipientAddress, UpdatedAtUtc
            ) VALUES (
                1, @host, @port, @tls, @sender, @displayName,
                @auth, @credRef, @recipient, @updated
            )
            ON CONFLICT(Id) DO UPDATE SET
                SmtpHost = excluded.SmtpHost,
                SmtpPort = excluded.SmtpPort,
                TlsMode = excluded.TlsMode,
                SenderAddress = excluded.SenderAddress,
                SenderDisplayName = excluded.SenderDisplayName,
                AuthMethod = excluded.AuthMethod,
                CredentialRef = excluded.CredentialRef,
                RecipientAddress = excluded.RecipientAddress,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        cmd.Parameters.AddWithValue("@host", settings.SmtpHost.Trim());
        cmd.Parameters.AddWithValue("@port", settings.SmtpPort);
        cmd.Parameters.AddWithValue("@tls", settings.TlsMode.ToString());
        cmd.Parameters.AddWithValue("@sender", settings.SenderAddress.Trim());
        cmd.Parameters.AddWithValue("@displayName", string.IsNullOrWhiteSpace(settings.SenderDisplayName) ? "PersonalLauncher" : settings.SenderDisplayName.Trim());
        cmd.Parameters.AddWithValue("@auth", settings.AuthMethod.ToString());
        cmd.Parameters.AddWithValue("@credRef", settings.CredentialRef);
        cmd.Parameters.AddWithValue("@recipient", settings.RecipientAddress.Trim());
        cmd.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EnqueueOutboxAsync(MailOutboxItem item, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        BindInsertOutbox(cmd, item);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MailOutboxItem>> GetDueOutboxItemsAsync(DateTimeOffset utcNow, CancellationToken ct = default)
    {
        var list = new List<MailOutboxItem>();
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EventId, CreatedAtUtc, AlertType, Status, MessageId, RetryCount,
                   NextRetryAtUtc, LastAttemptAtUtc, LastErrorSummary
            FROM MailOutbox
            WHERE Status IN ('Pending', 'Failed')
              AND (NextRetryAtUtc IS NULL OR NextRetryAtUtc <= @now)
            ORDER BY CreatedAtUtc ASC;
            """;
        cmd.Parameters.AddWithValue("@now", utcNow.ToString("O"));

        await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(MapOutbox(r));
        }
        return list;
    }

    public async Task<IReadOnlyList<MailOutboxItem>> GetAllOutboxItemsAsync(CancellationToken ct = default)
    {
        var list = new List<MailOutboxItem>();
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EventId, CreatedAtUtc, AlertType, Status, MessageId, RetryCount,
                   NextRetryAtUtc, LastAttemptAtUtc, LastErrorSummary
            FROM MailOutbox
            ORDER BY CreatedAtUtc DESC;
            """;
        await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(MapOutbox(r));
        }
        return list;
    }

    public async Task UpdateOutboxItemAsync(MailOutboxItem item, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE MailOutbox
            SET Status = @status,
                RetryCount = @retryCount,
                NextRetryAtUtc = @nextRetry,
                LastAttemptAtUtc = @lastAttempt,
                LastErrorSummary = @lastError
            WHERE EventId = @eventId;
            """;
        cmd.Parameters.AddWithValue("@eventId", item.EventId);
        cmd.Parameters.AddWithValue("@status", item.Status.ToString());
        cmd.Parameters.AddWithValue("@retryCount", item.RetryCount);
        cmd.Parameters.AddWithValue("@nextRetry", (object?)item.NextRetryAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastAttempt", (object?)item.LastAttemptAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastError", (object?)item.LastErrorSummary ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static void BindInsertOutbox(SqliteCommand cmd, MailOutboxItem item)
    {
        cmd.CommandText = """
            INSERT INTO MailOutbox (
                EventId, CreatedAtUtc, AlertType, Status, MessageId,
                RetryCount, NextRetryAtUtc, LastAttemptAtUtc, LastErrorSummary
            ) VALUES (
                @eventId, @created, @alertType, @status, @messageId,
                @retryCount, @nextRetry, @lastAttempt, @lastError
            );
            """;
        cmd.Parameters.AddWithValue("@eventId", item.EventId);
        cmd.Parameters.AddWithValue("@created", item.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@alertType", item.AlertType);
        cmd.Parameters.AddWithValue("@status", item.Status.ToString());
        cmd.Parameters.AddWithValue("@messageId", item.MessageId);
        cmd.Parameters.AddWithValue("@retryCount", item.RetryCount);
        cmd.Parameters.AddWithValue("@nextRetry", (object?)item.NextRetryAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastAttempt", (object?)item.LastAttemptAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastError", (object?)item.LastErrorSummary ?? DBNull.Value);
    }

    private static MailOutboxItem MapOutbox(SqliteDataReader r) => new()
    {
        EventId = r.GetString(0),
        CreatedAtUtc = DateTimeOffset.Parse(r.GetString(1)),
        AlertType = r.GetString(2),
        Status = Enum.TryParse<MailOutboxStatus>(r.GetString(3), true, out var s) ? s : MailOutboxStatus.Pending,
        MessageId = r.GetString(4),
        RetryCount = r.GetInt32(5),
        NextRetryAtUtc = r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6)),
        LastAttemptAtUtc = r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)),
        LastErrorSummary = r.IsDBNull(8) ? null : r.GetString(8)
    };
}
