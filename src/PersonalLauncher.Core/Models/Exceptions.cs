namespace PersonalLauncher.Core.Models;

public sealed class SecretUnauthorizedException : UnauthorizedAccessException
{
    public SecretScope RequiredScope { get; }

    public SecretUnauthorizedException(SecretScope requiredScope, string? message = null)
        : base(message ?? $"Secret access requires scope '{requiredScope}'.")
    {
        RequiredScope = requiredScope;
    }
}

public sealed class DuplicateGameException : InvalidOperationException
{
    public DuplicateGameException(string message) : base(message) { }
}

public sealed class AssetValidationException : InvalidOperationException
{
    public string ErrorCode { get; }

    public AssetValidationException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}
