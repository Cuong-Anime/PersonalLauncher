using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Infrastructure.Persistence;

public sealed class SqliteGameRepository : IGameRepository
{
    private readonly SqliteDatabase _db;

    public SqliteGameRepository(SqliteDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<GameEntry>> GetByVisibilityAsync(GameVisibility visibility, CancellationToken ct = default)
    {
        var list = new List<GameEntry>();
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, DisplayName, SearchKey, ExePath, NormalizedExeKey, FileIdentity,
                   InstallDirectory, WorkingDirectory, ArgumentsJson,
                   IconAssetId, TitleLogoAssetId, WallpaperAssetId, CardCoverAssetId,
                   Visibility, SortOrder, CreatedAtUtc, UpdatedAtUtc
            FROM Games
            WHERE Visibility = @visibility
            ORDER BY SortOrder ASC, CreatedAtUtc ASC;
            """;
        cmd.Parameters.AddWithValue("@visibility", ToDbVisibility(visibility));

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapGame(reader));
        }
        return list;
    }

    public async Task<GameEntry?> GetByIdAsync(string gameId, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, DisplayName, SearchKey, ExePath, NormalizedExeKey, FileIdentity,
                   InstallDirectory, WorkingDirectory, ArgumentsJson,
                   IconAssetId, TitleLogoAssetId, WallpaperAssetId, CardCoverAssetId,
                   Visibility, SortOrder, CreatedAtUtc, UpdatedAtUtc
            FROM Games
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", gameId);

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapGame(reader) : null;
    }

    public async Task<bool> ExistsByNormalizedExeKeyOrIdentityAsync(
        string normalizedExeKey,
        string? fileIdentity,
        string? excludeGameId = null,
        CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1)
            FROM Games
            WHERE (NormalizedExeKey = @exeKey OR (@fileId IS NOT NULL AND FileIdentity = @fileId))
              AND (@excludeId IS NULL OR Id <> @excludeId);
            """;
        cmd.Parameters.AddWithValue("@exeKey", normalizedExeKey);
        cmd.Parameters.AddWithValue("@fileId", (object?)fileIdentity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@excludeId", (object?)excludeGameId ?? DBNull.Value);

        object? result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) > 0;
    }

    public async Task<HashSet<string>> GetAllNormalizedExeKeysAsync(CancellationToken ct = default)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NormalizedExeKey FROM Games;";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            keys.Add(reader.GetString(0));
        }
        return keys;
    }

    public async Task<HashSet<string>> GetAllFileIdentitiesAsync(CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FileIdentity FROM Games WHERE FileIdentity IS NOT NULL;";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }
        return ids;
    }

    public async Task<bool> HasAnySecretGamesAsync(CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM Games WHERE Visibility = 'secret';";
        object? result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) > 0;
    }

    public async Task<int> GetNextSortOrderAsync(GameVisibility visibility, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Games WHERE Visibility = @visibility;";
        cmd.Parameters.AddWithValue("@visibility", ToDbVisibility(visibility));
        object? result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    public async Task InsertAsync(GameEntry game, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = CreateInsertCommand(conn, null, game);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertBatchAsync(IReadOnlyList<GameEntry> games, CancellationToken ct = default)
    {
        if (games.Count == 0)
            return;

        await using SqliteConnection conn = _db.OpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (GameEntry game in games)
            {
                await using SqliteCommand cmd = CreateInsertCommand(conn, tx, game);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task UpdateAsync(GameEntry game, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Games
            SET DisplayName = @displayName,
                SearchKey = @searchKey,
                ExePath = @exePath,
                NormalizedExeKey = @normalizedExeKey,
                FileIdentity = @fileIdentity,
                InstallDirectory = @installDir,
                WorkingDirectory = @workingDir,
                ArgumentsJson = @argsJson,
                IconAssetId = @iconAssetId,
                TitleLogoAssetId = @titleLogoAssetId,
                WallpaperAssetId = @wallpaperAssetId,
                CardCoverAssetId = @cardCoverAssetId,
                Visibility = @visibility,
                SortOrder = @sortOrder,
                UpdatedAtUtc = @updatedAtUtc
            WHERE Id = @id;
            """;
        BindGameParameters(cmd, game);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateVisibilityAsync(
        string gameId,
        GameVisibility visibility,
        int sortOrder,
        DateTimeOffset updatedAtUtc,
        CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Games
            SET Visibility = @visibility,
                SortOrder = @sortOrder,
                UpdatedAtUtc = @updatedAtUtc
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", gameId);
        cmd.Parameters.AddWithValue("@visibility", ToDbVisibility(visibility));
        cmd.Parameters.AddWithValue("@sortOrder", sortOrder);
        cmd.Parameters.AddWithValue("@updatedAtUtc", updatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string gameId, CancellationToken ct = default)
    {
        await using SqliteConnection conn = _db.OpenConnection();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Games WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", gameId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static SqliteCommand CreateInsertCommand(SqliteConnection conn, SqliteTransaction? tx, GameEntry game)
    {
        SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Games (
                Id, DisplayName, SearchKey, ExePath, NormalizedExeKey, FileIdentity,
                InstallDirectory, WorkingDirectory, ArgumentsJson,
                IconAssetId, TitleLogoAssetId, WallpaperAssetId, CardCoverAssetId,
                Visibility, SortOrder, CreatedAtUtc, UpdatedAtUtc
            ) VALUES (
                @id, @displayName, @searchKey, @exePath, @normalizedExeKey, @fileIdentity,
                @installDir, @workingDir, @argsJson,
                @iconAssetId, @titleLogoAssetId, @wallpaperAssetId, @cardCoverAssetId,
                @visibility, @sortOrder, @createdAtUtc, @updatedAtUtc
            );
            """;
        BindGameParameters(cmd, game);
        cmd.Parameters.AddWithValue("@createdAtUtc", game.CreatedAtUtc.ToString("O"));
        return cmd;
    }

    private static void BindGameParameters(SqliteCommand cmd, GameEntry game)
    {
        cmd.Parameters.AddWithValue("@id", game.Id);
        cmd.Parameters.AddWithValue("@displayName", game.DisplayName);
        cmd.Parameters.AddWithValue("@searchKey", game.SearchKey);
        cmd.Parameters.AddWithValue("@exePath", game.ExePath);
        cmd.Parameters.AddWithValue("@normalizedExeKey", game.NormalizedExeKey);
        cmd.Parameters.AddWithValue("@fileIdentity", (object?)game.FileIdentity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@installDir", game.InstallDirectory);
        cmd.Parameters.AddWithValue("@workingDir", game.WorkingDirectory);
        cmd.Parameters.AddWithValue("@argsJson", JsonSerializer.Serialize(game.Arguments));
        cmd.Parameters.AddWithValue("@iconAssetId", (object?)game.IconAssetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@titleLogoAssetId", (object?)game.TitleLogoAssetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@wallpaperAssetId", (object?)game.WallpaperAssetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cardCoverAssetId", (object?)game.CardCoverAssetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@visibility", ToDbVisibility(game.Visibility));
        cmd.Parameters.AddWithValue("@sortOrder", game.SortOrder);
        cmd.Parameters.AddWithValue("@updatedAtUtc", game.UpdatedAtUtc.ToString("O"));
    }

    private static GameEntry MapGame(SqliteDataReader r)
    {
        string argsJson = r.GetString(8);
        string[] args;
        try
        {
            args = JsonSerializer.Deserialize<string[]>(argsJson) ?? Array.Empty<string>();
        }
        catch
        {
            args = Array.Empty<string>();
        }

        return new GameEntry
        {
            Id = r.GetString(0),
            DisplayName = r.GetString(1),
            SearchKey = r.GetString(2),
            ExePath = r.GetString(3),
            NormalizedExeKey = r.GetString(4),
            FileIdentity = r.IsDBNull(5) ? null : r.GetString(5),
            InstallDirectory = r.GetString(6),
            WorkingDirectory = r.GetString(7),
            Arguments = args,
            IconAssetId = r.IsDBNull(9) ? null : r.GetString(9),
            TitleLogoAssetId = r.IsDBNull(10) ? null : r.GetString(10),
            WallpaperAssetId = r.IsDBNull(11) ? null : r.GetString(11),
            CardCoverAssetId = r.IsDBNull(12) ? null : r.GetString(12),
            Visibility = string.Equals(r.GetString(13), "secret", StringComparison.OrdinalIgnoreCase)
                ? GameVisibility.Secret
                : GameVisibility.Public,
            SortOrder = r.GetInt32(14),
            CreatedAtUtc = DateTimeOffset.Parse(r.GetString(15)),
            UpdatedAtUtc = DateTimeOffset.Parse(r.GetString(16))
        };
    }

    private static string ToDbVisibility(GameVisibility v) => v == GameVisibility.Secret ? "secret" : "public";
}
