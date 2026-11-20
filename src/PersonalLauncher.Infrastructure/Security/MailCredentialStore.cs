using System.Security.Cryptography;
using System.Text;
using PersonalLauncher.Core.Contracts;

namespace PersonalLauncher.Infrastructure.Security;

public sealed class MailCredentialStore : IMailCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PersonalLauncher.MailCredentials.v1");

    private readonly IAppPaths _paths;
    private readonly IDataProtector _protector;

    public MailCredentialStore(IAppPaths paths, IDataProtector protector)
    {
        _paths = paths;
        _protector = protector;
    }

    public bool HasCredential()
        => File.Exists(_paths.MailCredentialsFilePath) && new FileInfo(_paths.MailCredentialsFilePath).Length > 0;

    public async Task SaveCredentialAsync(string secret, CancellationToken ct = default)
    {
        _paths.EnsureDirectoriesExist();
        byte[] plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            byte[] protectedBytes = _protector.Protect(plain, Entropy);
            string tempPath = Path.Combine(_paths.SecurityDirectory, $"mail-cred.tmp.{Guid.NewGuid():N}");
            await File.WriteAllBytesAsync(tempPath, protectedBytes, ct);
            if (File.Exists(_paths.MailCredentialsFilePath))
                File.Replace(tempPath, _paths.MailCredentialsFilePath, null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, _paths.MailCredentialsFilePath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async Task<string?> LoadCredentialAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_paths.MailCredentialsFilePath))
            return null;

        try
        {
            byte[] protectedBytes = await File.ReadAllBytesAsync(_paths.MailCredentialsFilePath, ct);
            byte[] plain = _protector.Unprotect(protectedBytes, Entropy);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch
        {
            return null;
        }
    }

    public Task ClearCredentialAsync(CancellationToken ct = default)
    {
        if (File.Exists(_paths.MailCredentialsFilePath))
            File.Delete(_paths.MailCredentialsFilePath);
        return Task.CompletedTask;
    }
}
