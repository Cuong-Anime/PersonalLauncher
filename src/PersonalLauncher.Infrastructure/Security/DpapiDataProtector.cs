using System.Security.Cryptography;
using PersonalLauncher.Core.Contracts;

namespace PersonalLauncher.Infrastructure.Security;

public sealed class DpapiDataProtector : IDataProtector
{
    public byte[] Protect(byte[] plaintext, byte[]? optionalEntropy = null)
        => ProtectedData.Protect(plaintext, optionalEntropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData, byte[]? optionalEntropy = null)
        => ProtectedData.Unprotect(protectedData, optionalEntropy, DataProtectionScope.CurrentUser);
}
