using Microsoft.Data.Sqlite;
using PersonalLauncher.Core.Contracts;

namespace PersonalLauncher.Infrastructure.Persistence;

public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private readonly IAppLogger _logger;

    public string DatabasePath { get; }

    public SqliteDatabase(IAppPaths paths, IAppLogger logger)
    {
        DatabasePath = paths.DatabaseFilePath;
        _logger = logger;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        _connectionString = builder.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        string? dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using SqliteConnection conn = OpenConnection();

        await using (var walCmd = conn.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode = WAL;";
            await walCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var schemaTableCmd = conn.CreateCommand())
        {
            schemaTableCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS SchemaVersion (
                    Version INTEGER PRIMARY KEY,
                    AppliedAtUtc TEXT NOT NULL,
                    Description TEXT NOT NULL
                );
                """;
            await schemaTableCmd.ExecuteNonQueryAsync(ct);
        }

        int currentVersion = await GetCurrentVersionAsync(conn, ct);
        if (currentVersion < 1)
        {
            await ApplyMigrationV1Async(conn, ct);
            currentVersion = 1;
        }
        if (currentVersion < 2)
        {
            await ApplyMigrationV2Async(conn, ct);
        }
    }

    private async Task ApplyMigrationV2Async(SqliteConnection conn, CancellationToken ct)
    {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // Check if CardCoverAssetId already exists to stay idempotent
            bool hasColumn = false;
            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.Transaction = tx;
                checkCmd.CommandText = "PRAGMA table_info(Games);";
                await using var r = await checkCmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    if (string.Equals(r.GetString(1), "CardCoverAssetId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasColumn = true;
                        break;
                    }
                }
            }

            if (!hasColumn)
            {
                await using var alterCmd = conn.CreateCommand();
                alterCmd.Transaction = tx;
                alterCmd.CommandText = "ALTER TABLE Games ADD COLUMN CardCoverAssetId TEXT NULL;";
                await alterCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var verCmd = conn.CreateCommand())
            {
                verCmd.Transaction = tx;
                verCmd.CommandText = """
                    INSERT INTO SchemaVersion (Version, AppliedAtUtc, Description)
                    VALUES (2, @now, 'Add CardCoverAssetId for static library covers with live wallpapers');
                    """;
                verCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                await verCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _logger.Info("Database", "Applied SQLite migration v2.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task<int> GetCurrentVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;";
        object? result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    private async Task ApplyMigrationV1Async(SqliteConnection conn, CancellationToken ct)
    {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Assets (
                    Id TEXT PRIMARY KEY,
                    AssetType TEXT NOT NULL,
                    RelativePath TEXT NOT NULL,
                    ThumbnailRelativePath TEXT NULL,
                    Sha256 TEXT NOT NULL,
                    FileSizeBytes INTEGER NOT NULL,
                    PixelWidth INTEGER NOT NULL,
                    PixelHeight INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Games (
                    Id TEXT PRIMARY KEY,
                    DisplayName TEXT NOT NULL,
                    SearchKey TEXT NOT NULL,
                    ExePath TEXT NOT NULL,
                    NormalizedExeKey TEXT NOT NULL UNIQUE,
                    FileIdentity TEXT NULL,
                    InstallDirectory TEXT NOT NULL,
                    WorkingDirectory TEXT NOT NULL,
                    ArgumentsJson TEXT NOT NULL,
                    IconAssetId TEXT NULL,
                    TitleLogoAssetId TEXT NULL,
                    WallpaperAssetId TEXT NULL,
                    Visibility TEXT NOT NULL CHECK(Visibility IN ('public', 'secret')),
                    SortOrder INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_Games_Visibility_SortOrder ON Games(Visibility, SortOrder);
                CREATE INDEX IF NOT EXISTS IX_Games_FileIdentity ON Games(FileIdentity);

                CREATE TABLE IF NOT EXISTS SecurityState (
                    Id INTEGER PRIMARY KEY CHECK(Id = 1),
                    IsPinConfigured INTEGER NOT NULL DEFAULT 0,
                    GenerationId TEXT NULL,
                    ProtectedStateBlob BLOB NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                INSERT OR IGNORE INTO SecurityState (Id, IsPinConfigured, GenerationId, ProtectedStateBlob, UpdatedAtUtc)
                VALUES (1, 0, NULL, NULL, @now);

                CREATE TABLE IF NOT EXISTS MailSettings (
                    Id INTEGER PRIMARY KEY CHECK(Id = 1),
                    SmtpHost TEXT NOT NULL DEFAULT '',
                    SmtpPort INTEGER NOT NULL DEFAULT 587,
                    TlsMode TEXT NOT NULL DEFAULT 'StartTls',
                    SenderAddress TEXT NOT NULL DEFAULT '',
                    SenderDisplayName TEXT NOT NULL DEFAULT 'PersonalLauncher',
                    AuthMethod TEXT NOT NULL DEFAULT 'PasswordOrAppPassword',
                    CredentialRef TEXT NOT NULL DEFAULT 'mail-credentials.dat',
                    RecipientAddress TEXT NOT NULL DEFAULT '',
                    UpdatedAtUtc TEXT NOT NULL
                );

                INSERT OR IGNORE INTO MailSettings (
                    Id, SmtpHost, SmtpPort, TlsMode, SenderAddress, SenderDisplayName, AuthMethod, CredentialRef, RecipientAddress, UpdatedAtUtc
                ) VALUES (
                    1, '', 587, 'StartTls', '', 'PersonalLauncher', 'PasswordOrAppPassword', 'mail-credentials.dat', '', @now
                );

                CREATE TABLE IF NOT EXISTS MailOutbox (
                    EventId TEXT PRIMARY KEY,
                    CreatedAtUtc TEXT NOT NULL,
                    AlertType TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    MessageId TEXT NOT NULL,
                    RetryCount INTEGER NOT NULL DEFAULT 0,
                    NextRetryAtUtc TEXT NULL,
                    LastAttemptAtUtc TEXT NULL,
                    LastErrorSummary TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_MailOutbox_Status_NextRetry ON MailOutbox(Status, NextRetryAtUtc);

                INSERT INTO SchemaVersion (Version, AppliedAtUtc, Description)
                VALUES (1, @now, 'Initial PersonalLauncher schema');
                """;
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            _logger.Info("Database", "Applied SQLite migration v1.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
