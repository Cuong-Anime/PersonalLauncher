using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;
using PersonalLauncher.Core.Services;

namespace PersonalLauncher.Infrastructure.Security;

public sealed class PasscodeStore : IPasscodeStore
{
    private const int CurrentSchemaVersion = 1;
    private const int DefaultMemoryKiB = 19 * 1024; // 19 MiB (OWASP baseline)
    private const int DefaultIterations = 2;
    private const int DefaultParallelism = 1;
    private const int SaltBytesLength = 16;
    private const int HashBytesLength = 32;

    private static readonly byte[] PasscodeEntropy = Encoding.UTF8.GetBytes("PersonalLauncher.Passcode.Argon2id.v1");

    private readonly IAppPaths _paths;
    private readonly IDataProtector _protector;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public PasscodeStore(IAppPaths paths, IDataProtector protector, IAppLogger logger)
    {
        _paths = paths;
        _protector = protector;
        _logger = logger;
    }

    public async Task<PasscodeHealthStatus> CheckHealthAsync(
        string? expectedGenerationId,
        bool hasSecretGamesOrConfigured,
        CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct);
        try
        {
            if (!hasSecretGamesOrConfigured && !File.Exists(_paths.PasscodeFilePath))
                return PasscodeHealthStatus.NotConfigured;

            if (TryLoadVerifiedPayload(_paths.PasscodeFilePath, expectedGenerationId, out _))
                return PasscodeHealthStatus.Healthy;

            // Crash recovery: if a crash occurred mid-rotation before DB committed,
            // recover from passcode.bak ONLY if its generationId matches the committed DB generationId.
            if (!string.IsNullOrEmpty(expectedGenerationId) &&
                TryLoadVerifiedPayload(_paths.PasscodeBackupFilePath, expectedGenerationId, out _))
            {
                try
                {
                    File.Copy(_paths.PasscodeBackupFilePath, _paths.PasscodeFilePath, overwrite: true);
                    _logger.Warn("Security", "Recovered passcode file from matching backup generationId.");
                    return PasscodeHealthStatus.Healthy;
                }
                catch (Exception ex)
                {
                    _logger.Error("Security", "Failed to restore matching passcode backup.", ex);
                }
            }

            return PasscodeHealthStatus.MissingOrCorrupted;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<string> CreatePasscodeAsync(string pin, CancellationToken ct = default)
    {
        if (!PinValidation.IsValidPin(pin))
            throw new ArgumentException("PIN must be exactly 4 ASCII digits.", nameof(pin));

        await _ioGate.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();
            string generationId = Guid.NewGuid().ToString("N");
            PasscodeEnvelope envelope = BuildEnvelope(pin, generationId);
            await WriteAtomicallyAsync(envelope, createBackup: false, ct);
            return generationId;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<bool> VerifyPasscodeAsync(string pin, string? expectedGenerationId, CancellationToken ct = default)
    {
        if (!PinValidation.IsValidPin(pin))
            return false;

        PasscodeInnerPayload? payload;
        await _ioGate.WaitAsync(ct);
        try
        {
            if (!TryLoadVerifiedPayload(_paths.PasscodeFilePath, expectedGenerationId, out payload))
            {
                // Fallback to backup only if it matches committed DB generationId
                if (string.IsNullOrEmpty(expectedGenerationId) ||
                    !TryLoadVerifiedPayload(_paths.PasscodeBackupFilePath, expectedGenerationId, out payload))
                {
                    return false;
                }
            }
        }
        finally
        {
            _ioGate.Release();
        }

        if (payload is null)
            return false;

        return await Task.Run(() =>
        {
            byte[] salt = Convert.FromBase64String(payload.SaltBase64);
            byte[] expectedHash = Convert.FromBase64String(payload.HashBase64);
            byte[] computed = ComputeArgon2id(pin, salt, payload.MemoryKiB, payload.Iterations, payload.Parallelism, expectedHash.Length);
            try
            {
                return CryptographicOperations.FixedTimeEquals(computed, expectedHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(computed);
                CryptographicOperations.ZeroMemory(expectedHash);
            }
        }, ct);
    }

    public async Task<string> RotatePasscodeAsync(string newPin, string? previousGenerationId, CancellationToken ct = default)
    {
        if (!PinValidation.IsValidPin(newPin))
            throw new ArgumentException("PIN must be exactly 4 ASCII digits.", nameof(newPin));

        await _ioGate.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();
            string newGenId = Guid.NewGuid().ToString("N");
            PasscodeEnvelope envelope = BuildEnvelope(newPin, newGenId);
            await WriteAtomicallyAsync(envelope, createBackup: true, ct);
            return newGenId;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task RestoreBackupAfterFailedRotationAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct);
        try
        {
            if (File.Exists(_paths.PasscodeBackupFilePath))
            {
                File.Copy(_paths.PasscodeBackupFilePath, _paths.PasscodeFilePath, overwrite: true);
                _logger.Warn("Security", "Restored previous passcode backup after failed PIN rotation.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Security", "Failed to restore passcode backup after rotation error.", ex);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private PasscodeEnvelope BuildEnvelope(string pin, string generationId)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytesLength);
        byte[] hash = ComputeArgon2id(pin, salt, DefaultMemoryKiB, DefaultIterations, DefaultParallelism, HashBytesLength);
        try
        {
            var inner = new PasscodeInnerPayload
            {
                Algorithm = "Argon2id",
                MemoryKiB = DefaultMemoryKiB,
                Iterations = DefaultIterations,
                Parallelism = DefaultParallelism,
                SaltBase64 = Convert.ToBase64String(salt),
                HashBase64 = Convert.ToBase64String(hash),
                GenerationId = generationId
            };

            byte[] plainJson = JsonSerializer.SerializeToUtf8Bytes(inner);
            try
            {
                byte[] protectedBytes = _protector.Protect(plainJson, PasscodeEntropy);
                return new PasscodeEnvelope
                {
                    SchemaVersion = CurrentSchemaVersion,
                    GenerationId = generationId,
                    Protection = "dpapi-current-user",
                    Payload = Convert.ToBase64String(protectedBytes)
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainJson);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private static byte[] ComputeArgon2id(string pin, byte[] salt, int memoryKiB, int iterations, int parallelism, int outputLen)
    {
        byte[] pinBytes = Encoding.UTF8.GetBytes(pin);
        try
        {
            using var argon2 = new Argon2id(pinBytes)
            {
                Salt = salt,
                MemorySize = memoryKiB,
                Iterations = iterations,
                DegreeOfParallelism = parallelism
            };
            return argon2.GetBytes(outputLen);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinBytes);
        }
    }

    private bool TryLoadVerifiedPayload(string filePath, string? expectedGenerationId, out PasscodeInnerPayload? payload)
    {
        payload = null;
        if (!File.Exists(filePath))
            return false;

        try
        {
            string json = File.ReadAllText(filePath, Encoding.UTF8);
            PasscodeEnvelope? envelope = JsonSerializer.Deserialize<PasscodeEnvelope>(json);
            if (envelope is null ||
                envelope.SchemaVersion != CurrentSchemaVersion ||
                !string.Equals(envelope.Protection, "dpapi-current-user", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(envelope.GenerationId) ||
                string.IsNullOrWhiteSpace(envelope.Payload))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(expectedGenerationId) &&
                !string.Equals(envelope.GenerationId, expectedGenerationId, StringComparison.Ordinal))
            {
                return false;
            }

            byte[] protectedBytes = Convert.FromBase64String(envelope.Payload);
            byte[] plainBytes = _protector.Unprotect(protectedBytes, PasscodeEntropy);
            try
            {
                PasscodeInnerPayload? inner = JsonSerializer.Deserialize<PasscodeInnerPayload>(plainBytes);
                if (inner is null ||
                    !string.Equals(inner.Algorithm, "Argon2id", StringComparison.Ordinal) ||
                    !string.Equals(inner.GenerationId, envelope.GenerationId, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(inner.SaltBase64) ||
                    string.IsNullOrWhiteSpace(inner.HashBase64))
                {
                    return false;
                }

                payload = inner;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        catch
        {
            return false;
        }
    }

    private async Task WriteAtomicallyAsync(PasscodeEnvelope envelope, bool createBackup, CancellationToken ct)
    {
        string tempPath = Path.Combine(_paths.SecurityDirectory, $"passcode.tmp.{Guid.NewGuid():N}");
        string json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });

        await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(fs, Encoding.UTF8))
        {
            await writer.WriteAsync(json.AsMemory(), ct);
            await writer.FlushAsync(ct);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(_paths.PasscodeFilePath))
        {
            string? backupPath = createBackup ? _paths.PasscodeBackupFilePath : null;
            File.Replace(tempPath, _paths.PasscodeFilePath, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, _paths.PasscodeFilePath);
        }
    }

    private sealed class PasscodeEnvelope
    {
        public int SchemaVersion { get; set; }
        public string GenerationId { get; set; } = string.Empty;
        public string Protection { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }

    private sealed class PasscodeInnerPayload
    {
        public string Algorithm { get; set; } = string.Empty;
        public int MemoryKiB { get; set; }
        public int Iterations { get; set; }
        public int Parallelism { get; set; }
        public string SaltBase64 { get; set; } = string.Empty;
        public string HashBase64 { get; set; } = string.Empty;
        public string GenerationId { get; set; } = string.Empty;
    }
}
