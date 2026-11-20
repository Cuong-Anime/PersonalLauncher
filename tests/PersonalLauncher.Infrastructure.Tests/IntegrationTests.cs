using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;
using PersonalLauncher.Core.Services;
using PersonalLauncher.Infrastructure.Assets;
using PersonalLauncher.Infrastructure.Games;
using PersonalLauncher.Infrastructure.Mail;
using PersonalLauncher.Infrastructure.Persistence;
using PersonalLauncher.Infrastructure.Security;
using Xunit;

namespace PersonalLauncher.Infrastructure.Tests;

public sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    private long _ticks;

    public TestTimeProvider(DateTimeOffset startUtc)
    {
        _utcNow = startUtc;
        _ticks = 1_000_000;
    }

    public void Advance(TimeSpan delta)
    {
        _utcNow += delta;
        _ticks += (long)(delta.TotalSeconds * TimestampFrequency);
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override long GetTimestamp() => _ticks;
}

public sealed class NullLogger : IAppLogger
{
    public List<string> Lines { get; } = new();
    public void Info(string category, string message) => Lines.Add($"INFO:{category}:{message}");
    public void Warn(string category, string message) => Lines.Add($"WARN:{category}:{message}");
    public void Error(string category, string message, Exception? exception = null) => Lines.Add($"ERROR:{category}:{message}");
}

public sealed class FakeMailTransport : IMailTransport
{
    public Func<MailSettingsRecord, string, string, string, string, MailSendResult>? Handler { get; set; }
    public List<(string Recipient, string MessageId, string Subject, string Body)> SentMessages { get; } = new();

    public Task<MailSendResult> SendAsync(
        MailSettingsRecord settings,
        string secretCredential,
        string messageId,
        string subject,
        string bodyText,
        CancellationToken ct = default)
    {
        SentMessages.Add((settings.RecipientAddress, messageId, subject, bodyText));
        MailSendResult result = Handler?.Invoke(settings, secretCredential, messageId, subject, bodyText)
            ?? new MailSendResult { Outcome = MailOutboxStatus.Accepted };
        return Task.FromResult(result);
    }
}

public sealed class TestEnvironment : IDisposable
{
    public string TempRoot { get; }
    public string DataRoot { get; }
    public string DefaultsRoot { get; }
    public string GamesFixtureRoot { get; }

    public AppPaths Paths { get; }
    public NullLogger Logger { get; } = new();
    public TestTimeProvider Clock { get; }
    public SqliteDatabase Database { get; }
    public SqliteGameRepository GameRepo { get; }
    public SqliteAssetRepository AssetRepo { get; }
    public SqliteSecurityRepository SecurityRepo { get; }
    public SqliteMailRepository MailRepo { get; }
    public JsonSettingsService Settings { get; }
    public DpapiDataProtector Protector { get; } = new();
    public PasscodeStore PasscodeStore { get; }
    public MailCredentialStore MailCredStore { get; }
    public FakeMailTransport FakeTransport { get; } = new();
    public MailOutboxService MailOutbox { get; }
    public SecurityAttemptService AttemptService { get; }
    public SecretAccessService SecretAccess { get; }
    public AssetService AssetService { get; }
    public WindowsFileIdentityProvider FileIdentity { get; } = new();
    public GameLibraryService Library { get; }
    public SearchService Search { get; }
    public GameScanService Scanner { get; }

    public TestEnvironment()
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "PersonalLauncherTests", Guid.NewGuid().ToString("N"));
        DataRoot = Path.Combine(TempRoot, "AppData");
        DefaultsRoot = Path.Combine(TempRoot, "Defaults");
        GamesFixtureRoot = Path.Combine(TempRoot, "Games");

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(DefaultsRoot);
        Directory.CreateDirectory(GamesFixtureRoot);

        CreateSamplePng(Path.Combine(DefaultsRoot, "app_logo.png"), 64, 64, Colors.Gold);
        CreateSamplePng(Path.Combine(DefaultsRoot, "logo_default.png"), 64, 64, Colors.DeepSkyBlue);
        CreateSampleJpg(Path.Combine(DefaultsRoot, "wallpaper_default.jpg"), 320, 180, Colors.MidnightBlue);

        Paths = new AppPaths(DataRoot, DefaultsRoot);
        Paths.EnsureDirectoriesExist();

        Clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
        Database = new SqliteDatabase(Paths, Logger);
        Database.InitializeAsync().GetAwaiter().GetResult();

        GameRepo = new SqliteGameRepository(Database);
        AssetRepo = new SqliteAssetRepository(Database);
        SecurityRepo = new SqliteSecurityRepository(Database);
        MailRepo = new SqliteMailRepository(Database);
        Settings = new JsonSettingsService(Paths, Logger);
        Settings.LoadAsync().GetAwaiter().GetResult();

        PasscodeStore = new PasscodeStore(Paths, Protector, Logger);
        MailCredStore = new MailCredentialStore(Paths, Protector);
        MailOutbox = new MailOutboxService(MailRepo, MailCredStore, FakeTransport, Logger, Clock, () => 0.5);
        AttemptService = new SecurityAttemptService(SecurityRepo, Protector, Logger, Clock);
        SecretAccess = new SecretAccessService(SecurityRepo, GameRepo, PasscodeStore, AttemptService, Logger, Clock);
        AssetService = new AssetService(Paths, AssetRepo, Logger);
        Library = new GameLibraryService(GameRepo, AssetService, SecretAccess, Settings, FileIdentity, Logger, Clock);
        Search = new SearchService(Library);
        Scanner = new GameScanService(GameRepo, FileIdentity, Logger);
    }

    public string CreateDummyExe(string relativePath)
    {
        string full = Path.Combine(GamesFixtureRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "MZ-DUMMY-EXE-CONTENT-" + Guid.NewGuid().ToString("N"));
        return full;
    }

    public static string CreateSamplePng(string path, int w, int h, Color color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }
        wb.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(wb));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    public static string CreateSampleJpg(string path, int w, int h, Color color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
        byte[] pixels = new byte[w * h * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
        }
        wb.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, w * 3, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(wb));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    public void Dispose()
    {
        MailOutbox.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(TempRoot, recursive: true); } catch { }
    }
}

public sealed class IntegrationTests
{
    [Fact]
    public async Task T03_T05_T26_AddEditDeleteAndCorruptSettingsRecovery()
    {
        using var env = new TestEnvironment();

        string exe1 = env.CreateDummyExe(@"Game Tiếng Việt Có Dấu\my game.exe");
        string exe2 = env.CreateDummyExe(@"Game Tiếng Việt Có Dấu\updated.exe");
        string iconFile = TestEnvironment.CreateSamplePng(Path.Combine(env.TempRoot, "icon.png"), 128, 128, Colors.Crimson);
        string wpFile = TestEnvironment.CreateSampleJpg(Path.Combine(env.TempRoot, "wp.jpg"), 640, 360, Colors.DarkSlateBlue);

        GameEntry created = await env.Library.AddManualGameAsync(new GameCreateRequest
        {
            DisplayName = "  Đấu Trường Ảo  ",
            ExePath = exe1,
            IconSourceFilePath = iconFile,
            WallpaperSourceFilePath = wpFile,
            Arguments = new[] { "-fullscreen" }
        });

        Assert.Equal("Đấu Trường Ảo", created.DisplayName);
        Assert.NotNull(created.IconAssetId);
        Assert.NotNull(created.WallpaperAssetId);

        // Duplicate EXE (different casing) MUST fail
        await Assert.ThrowsAsync<DuplicateGameException>(() => env.Library.AddManualGameAsync(new GameCreateRequest
        {
            DisplayName = "Duplicate",
            ExePath = exe1.ToUpperInvariant()
        }));

        // Update location updates reference ONLY, never deletes or moves original EXE!
        GameEntry relocated = await env.Library.UpdateGameLocationAsync(new GameUpdateLocationRequest
        {
            GameId = created.Id,
            ExePath = exe2,
            InstallDirectory = Path.GetDirectoryName(exe2)!,
            WorkingDirectory = Path.GetDirectoryName(exe2)!,
            Arguments = new[] { "-windowed" }
        });
        Assert.Equal(exe2, relocated.ExePath);
        Assert.True(File.Exists(exe1));
        Assert.True(File.Exists(exe2));

        // Corrupt image file MUST fail validation cleanly and preserve existing media!
        string fakePng = Path.Combine(env.TempRoot, "corrupt.png");
        await File.WriteAllTextAsync(fakePng, "NOT A REAL PNG FILE");
        await Assert.ThrowsAsync<AssetValidationException>(() => env.Library.UpdateGameMediaAsync(new GameUpdateMediaRequest
        {
            GameId = created.Id,
            WallpaperSourceFilePath = fakePng
        }));
        GameEntry? afterFailedMedia = await env.Library.GetGameByIdAsync(created.Id);
        Assert.Equal(created.WallpaperAssetId, afterFailedMedia!.WallpaperAssetId);

        // Delete from library removes record & launcher assets ONLY, real game EXE stays untouched!
        await env.Library.DeleteGameFromLibraryAsync(created.Id);
        Assert.Empty(await env.Library.GetPublicGamesAsync());
        Assert.True(File.Exists(exe2));

        // Corrupt settings.json creates backup and recovers cleanly!
        await File.WriteAllTextAsync(env.Paths.SettingsFilePath, "{ broken json !!!");
        AppSettings recovered = await env.Settings.LoadAsync();
        Assert.Equal("vi-VN", recovered.Language);
        Assert.True(Directory.GetFiles(env.DataRoot, "settings.json.corrupt.*.bak").Length >= 1);
    }

    [Fact]
    public async Task T08_T09_T10_AutoScanNamesDirectParentHandlesMultiExeHelpersAndSecretDedup()
    {
        using var env = new TestEnvironment();

        string gameA = env.CreateDummyExe(@"GameA\GameA.exe");
        string gameAHelper = env.CreateDummyExe(@"GameA\unins000.exe");
        string gameB = env.CreateDummyExe(@"GameB\Binaries\Win64\MyShooter.exe");
        string gameBAlt = env.CreateDummyExe(@"GameB\Binaries\Win64\AltEntry.exe");
        string secretGameExe = env.CreateDummyExe(@"SecretTitle\secret.exe");

        // Configure Secret PIN & add a Secret game directly
        var (ok, _) = await env.SecretAccess.SetupFirstPinAsync("0042", "0042");
        Assert.True(ok);
        await env.SecretAccess.UnlockAsync("0042", SecretScope.Manage);
        await env.Library.AddManualGameAsync(new GameCreateRequest
        {
            DisplayName = "My Hidden Secret",
            ExePath = secretGameExe,
            Visibility = GameVisibility.Secret
        });
        env.SecretAccess.Lock("test");

        // Scan directory while Secret is LOCKED
        ScanResult scan = await env.Scanner.ScanDirectoryAsync(new ScanOptions
        {
            RootDirectory = env.GamesFixtureRoot,
            IncludeSubdirectories = true
        });

        ScanCandidate candA = Assert.Single(scan.Candidates, c => c.ExePath.Equals(gameA, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("GameA", candA.SuggestedName);
        Assert.True(candA.IsSuggestedSelected);
        Assert.False(candA.IsLikelyHelper);

        ScanCandidate candHelper = Assert.Single(scan.Candidates, c => c.ExePath.Equals(gameAHelper, StringComparison.OrdinalIgnoreCase));
        Assert.True(candHelper.IsLikelyHelper);
        Assert.False(candHelper.IsSuggestedSelected);

        ScanCandidate candB = Assert.Single(scan.Candidates, c => c.ExePath.Equals(gameB, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Win64", candB.SuggestedName); // Direct parent rule 5.2 #7!
        Assert.True(candB.HasMultipleExesInFolder);

        // Secret EXE is marked IsAlreadyInLibrary=true and NOT selected, without exposing Secret name!
        ScanCandidate candSecret = Assert.Single(scan.Candidates, c => c.ExePath.Equals(secretGameExe, StringComparison.OrdinalIgnoreCase));
        Assert.True(candSecret.IsAlreadyInLibrary);
        Assert.False(candSecret.IsSuggestedSelected);
        Assert.DoesNotContain("My Hidden Secret", candSecret.SuggestedName);

        // Import selected candidates in single transaction -> default icon & wallpaper (null IDs)
        var toImport = scan.Candidates
            .Where(c => c.IsSuggestedSelected)
            .Select(c => new BatchImportItem
            {
                DisplayName = c.SuggestedName,
                ExePath = c.ExePath,
                InstallDirectory = c.DirectParentDirectory
            }).ToList();

        BatchImportResult import1 = await env.Library.ImportScannedGamesAsync(toImport, GameVisibility.Public);
        Assert.Equal(2, import1.AddedCount);
        Assert.All(import1.AddedGames, g =>
        {
            Assert.Null(g.IconAssetId);
            Assert.Null(g.WallpaperAssetId);
        });

        // Re-importing same items adds 0 duplicates and keeps Secret hidden!
        BatchImportResult import2 = await env.Library.ImportScannedGamesAsync(toImport, GameVisibility.Public);
        Assert.Equal(0, import2.AddedCount);
        Assert.Equal(2, import2.SkippedDuplicateCount);
        Assert.DoesNotContain(await env.Library.GetPublicGamesAsync(), g => g.ExePath.Equals(secretGameExe, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task T15_T17_T18_T19_SecretPasscodeFileScopesTimeoutAndIsolation()
    {
        using var env = new TestEnvironment();

        // Setup PIN with leading zeros "0042"
        var (setupOk, err) = await env.SecretAccess.SetupFirstPinAsync("0042", "0042");
        Assert.True(setupOk, err);

        // Verify exact filename "passcode" (NOT passcode.txt) and ZERO plaintext PIN inside!
        Assert.True(File.Exists(env.Paths.PasscodeFilePath));
        Assert.Equal("passcode", Path.GetFileName(env.Paths.PasscodeFilePath));
        string rawFile = await File.ReadAllTextAsync(env.Paths.PasscodeFilePath);
        Assert.DoesNotContain("0042", rawFile);
        Assert.Contains("dpapi-current-user", rawFile);

        // Locked: service-level calls to Secret MUST throw SecretUnauthorizedException
        await Assert.ThrowsAsync<SecretUnauthorizedException>(() => env.Library.GetSecretGamesAsync());

        // Unlock Browse scope
        PinVerificationResult browseUnlock = await env.SecretAccess.UnlockAsync("0042", SecretScope.Browse);
        Assert.Equal(PinVerificationStatus.Success, browseUnlock.Status);
        Assert.True(env.SecretAccess.HasScope(SecretScope.Browse));
        Assert.False(env.SecretAccess.HasScope(SecretScope.Manage));

        // Browse scope CANNOT add directly to Secret or change PIN (requires Manage!)
        string secretExe = env.CreateDummyExe(@"Hidden\h.exe");
        await Assert.ThrowsAsync<SecretUnauthorizedException>(() => env.Library.AddManualGameAsync(new GameCreateRequest
        {
            DisplayName = "Secret Game",
            ExePath = secretExe,
            Visibility = GameVisibility.Secret
        }));

        // Unlock Manage scope -> succeeds
        PinVerificationResult manageUnlock = await env.SecretAccess.UnlockAsync("0042", SecretScope.Manage);
        Assert.Equal(PinVerificationStatus.Success, manageUnlock.Status);

        GameEntry secretGame = await env.Library.AddManualGameAsync(new GameCreateRequest
        {
            DisplayName = "Top Secret Title",
            ExePath = secretExe,
            Visibility = GameVisibility.Secret
        });

        // Public list & public search NEVER return Secret game
        Assert.Empty(await env.Library.GetPublicGamesAsync());
        Assert.Empty(await env.Search.SearchAsync("Top Secret", GameVisibility.Public));
        Assert.Single(await env.Search.SearchAsync("Top Secret", GameVisibility.Secret));

        // Logs NEVER contain Secret game name or PIN!
        Assert.All(env.Logger.Lines, line =>
        {
            Assert.DoesNotContain("0042", line);
            Assert.DoesNotContain("Top Secret Title", line);
        });

        // Inactivity timeout (5 min) locks automatically!
        env.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.False(env.SecretAccess.HasScope(SecretScope.Browse));
        await Assert.ThrowsAsync<SecretUnauthorizedException>(() => env.Library.GetSecretGamesAsync());
    }

    [Fact]
    public async Task T20_T21_T22_T23_T25_FiveFailedPinsCooldownOutboxRetryAndTamperProtection()
    {
        using var env = new TestEnvironment();

        await env.SecretAccess.SetupFirstPinAsync("0042", "0042");

        // Malformed PIN must NOT count as failed attempt
        PinVerificationResult badFormat = await env.SecretAccess.UnlockAsync("12", SecretScope.Browse);
        Assert.Equal(PinVerificationStatus.InvalidFormat, badFormat.Status);
        Assert.Equal(0, (await env.SecretAccess.GetAttemptSnapshotAsync()).FailedAttempts);

        // 4 failed attempts across Browse & Manage
        for (int i = 1; i <= 4; i++)
        {
            SecretScope scope = i % 2 == 0 ? SecretScope.Manage : SecretScope.Browse;
            PinVerificationResult res = await env.SecretAccess.UnlockAsync("9999", scope);
            Assert.Equal(PinVerificationStatus.WrongPin, res.Status);
            Assert.Equal(i, res.FailedAttempts);
            Assert.Equal(5 - i, res.RemainingAttempts);
        }

        // Simulate restart: new AttemptService + SecretAccessService reading same SQLite DB!
        var attemptAfterRestart = new SecurityAttemptService(env.SecurityRepo, env.Protector, env.Logger, env.Clock);
        var secretAfterRestart = new SecretAccessService(env.SecurityRepo, env.GameRepo, env.PasscodeStore, attemptAfterRestart, env.Logger, env.Clock);

        // 5th wrong attempt triggers 5-min cooldown + queues EXACTLY 1 MailOutbox alert!
        PinVerificationResult fifth = await secretAfterRestart.UnlockAsync("1111", SecretScope.Browse);
        Assert.Equal(PinVerificationStatus.LockedOut, fifth.Status);
        Assert.NotNull(fifth.QueuedAlertEventId);
        Assert.NotNull(fifth.CooldownUntilUtc);

        IReadOnlyList<MailOutboxItem> outbox = await env.MailRepo.GetAllOutboxItemsAsync();
        Assert.Single(outbox);
        Assert.Equal(MailOutboxStatus.Pending, outbox[0].Status);

        // Attempt during cooldown must stay locked and NOT queue another alert!
        PinVerificationResult duringCooldown = await secretAfterRestart.UnlockAsync("0042", SecretScope.Browse);
        Assert.Equal(PinVerificationStatus.LockedOut, duringCooldown.Status);
        Assert.Single(await env.MailRepo.GetAllOutboxItemsAsync());

        // Offline / unconfigured SMTP keeps event Pending (never claims sent!)
        await env.MailOutbox.ProcessPendingAsync();
        Assert.Equal(MailOutboxStatus.Pending, (await env.MailRepo.GetAllOutboxItemsAsync())[0].Status);

        // Configure SMTP & simulate temporary failure -> schedules retry backoff, then succeeds once online!
        await env.MailOutbox.SaveSettingsAsync(new MailSettingsRecord
        {
            SmtpHost = "smtp.example.com",
            SmtpPort = 587,
            TlsMode = MailTlsMode.StartTls,
            SenderAddress = "sender@example.com",
            SenderDisplayName = "PersonalLauncher",
            RecipientAddress = "user-alert@example.com"
        }, "super-secret-app-password");

        env.FakeTransport.Handler = (_, _, _, _, _) => new MailSendResult
        {
            Outcome = MailOutboxStatus.Failed,
            ErrorSummary = "Network unreachable"
        };

        await env.MailOutbox.ProcessPendingAsync();
        MailOutboxItem failedItem = (await env.MailRepo.GetAllOutboxItemsAsync())[0];
        Assert.Equal(MailOutboxStatus.Failed, failedItem.Status);
        Assert.Equal(1, failedItem.RetryCount);
        Assert.NotNull(failedItem.NextRetryAtUtc);

        // Advance clock past 1-minute retry backoff and allow transport to accept!
        env.Clock.Advance(TimeSpan.FromMinutes(2));
        env.FakeTransport.Handler = (_, _, _, _, _) => new MailSendResult { Outcome = MailOutboxStatus.Accepted };
        await env.MailOutbox.ProcessPendingAsync();

        MailOutboxItem acceptedItem = (await env.MailRepo.GetAllOutboxItemsAsync())[0];
        Assert.Equal(MailOutboxStatus.Accepted, acceptedItem.Status);
        var sent = env.FakeTransport.SentMessages.Last();
        Assert.Equal("user-alert@example.com", sent.Recipient);
        Assert.Contains(acceptedItem.EventId, sent.Body);
        Assert.DoesNotContain("0042", sent.Body);

        // After cooldown expires, valid PIN succeeds and resets streak without deleting past alerts!
        env.Clock.Advance(TimeSpan.FromMinutes(6));
        PinVerificationResult validAfterCooldown = await secretAfterRestart.UnlockAsync("0042", SecretScope.Browse);
        Assert.Equal(PinVerificationStatus.Success, validAfterCooldown.Status);
        Assert.Single(await env.MailRepo.GetAllOutboxItemsAsync());

        // T25: Corrupting passcode file MUST keep Secret locked and refuse first-time setup bypass!
        await File.WriteAllTextAsync(env.Paths.PasscodeFilePath, "corrupted-content");
        secretAfterRestart.Lock("test");
        Assert.Equal(PasscodeHealthStatus.MissingOrCorrupted, await secretAfterRestart.GetPasscodeHealthAsync());
        var (bypassOk, _) = await secretAfterRestart.SetupFirstPinAsync("1234", "1234");
        Assert.False(bypassOk);
        PinVerificationResult unlockCorrupt = await secretAfterRestart.UnlockAsync("0042", SecretScope.Browse);
        Assert.Equal(PinVerificationStatus.PasscodeCorrupted, unlockCorrupt.Status);
    }

    [Fact]
    public async Task T28_SearchAcross500GamesCompletesUnder200Ms()
    {
        using var env = new TestEnvironment();
        var batch = new List<BatchImportItem>(500);
        for (int i = 0; i < 500; i++)
        {
            string name = i == 420 ? "Đế Chế Huyền Thoại 420" : $"Sample Game Title {i:D3}";
            batch.Add(new BatchImportItem
            {
                DisplayName = name,
                ExePath = Path.Combine(env.GamesFixtureRoot, $"G{i}", $"game{i}.exe"),
                InstallDirectory = Path.Combine(env.GamesFixtureRoot, $"G{i}")
            });
        }

        BatchImportResult res = await env.Library.ImportScannedGamesAsync(batch, GameVisibility.Public);
        Assert.Equal(500, res.AddedCount);

        IReadOnlyList<GameEntry> all = await env.Library.GetPublicGamesAsync();
        var sw = Stopwatch.StartNew();
        IReadOnlyList<GameEntry> matches = env.Search.FilterInMemory(all, "de che huyen thoai");
        sw.Stop();

        Assert.Single(matches);
        Assert.Equal("Đế Chế Huyền Thoại 420", matches[0].DisplayName);
        Assert.True(sw.ElapsedMilliseconds < 200, $"Search took {sw.ElapsedMilliseconds}ms, expected <200ms.");
    }

    [Fact]
    public async Task LiveVideoWallpaper_PlaysVideoAsWallpaperWhileLibraryCardAlwaysUsesStaticImage()
    {
        using var env = new TestEnvironment();
        string exe = env.CreateDummyExe(@"LiveGame\live.exe");

        // Create a valid MP4 header file (ISO BMFF 'ftypisom') and a static cover PNG
        string fakeValidMp4 = Path.Combine(env.TempRoot, "live_bg.mp4");
        byte[] mp4Header = new byte[]
        {
            0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0x00, 0x00, 0x02, 0x00,
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'1'
        };
        await File.WriteAllBytesAsync(fakeValidMp4, mp4Header);

        string staticCover = TestEnvironment.CreateSamplePng(Path.Combine(env.TempRoot, "cover.png"), 256, 256, Colors.MediumPurple);

        GameEntry game = await env.Library.AddManualGameAsync(new GameCreateRequest
        {
            DisplayName = "Live Wallpaper Game",
            ExePath = exe,
            WallpaperSourceFilePath = fakeValidMp4
        });

        // Main wallpaper resolves to the managed .mp4 video
        string resolvedWp = await env.AssetService.ResolveAssetPathAsync(game.WallpaperAssetId, AssetType.Wallpaper, preferThumbnail: false);
        Assert.True(MediaFormats.IsVideoPath(resolvedWp));
        Assert.True(File.Exists(resolvedWp));

        // Library card cover MUST ALWAYS resolve to a static image (NEVER a video path!)
        string resolvedCardAuto = await env.AssetService.ResolveCardCoverImagePathAsync(game);
        Assert.False(MediaFormats.IsVideoPath(resolvedCardAuto));
        Assert.True(File.Exists(resolvedCardAuto));

        // Setting a dedicated static library cover keeps the live video wallpaper intact!
        GameEntry updated = await env.Library.UpdateGameMediaAsync(new GameUpdateMediaRequest
        {
            GameId = game.Id,
            CardCoverSourceFilePath = staticCover
        });
        Assert.Equal(game.WallpaperAssetId, updated.WallpaperAssetId);
        Assert.NotNull(updated.CardCoverAssetId);

        string resolvedCardCustom = await env.AssetService.ResolveCardCoverImagePathAsync(updated);
        Assert.False(MediaFormats.IsVideoPath(resolvedCardCustom));
        Assert.Contains(updated.CardCoverAssetId!, resolvedCardCustom);

        // Invalid/corrupt MP4 file MUST be rejected
        string corruptMp4 = Path.Combine(env.TempRoot, "not_a_video.mp4");
        await File.WriteAllTextAsync(corruptMp4, "THIS IS NOT AN MP4 FILE AT ALL");
        await Assert.ThrowsAsync<AssetValidationException>(() => env.Library.UpdateGameMediaAsync(new GameUpdateMediaRequest
        {
            GameId = game.Id,
            WallpaperSourceFilePath = corruptMp4
        }));
    }
}
