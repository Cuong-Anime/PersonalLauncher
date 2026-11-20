namespace PersonalLauncher.Core.Contracts;

public interface IFileIdentityProvider
{
    string? TryGetFileIdentity(string filePath);
}

public interface IDataProtector
{
    byte[] Protect(byte[] plaintext, byte[]? optionalEntropy = null);
    byte[] Unprotect(byte[] protectedData, byte[]? optionalEntropy = null);
}
