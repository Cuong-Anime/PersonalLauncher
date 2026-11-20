using Microsoft.Win32;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Infrastructure.Windows;

public sealed class StartupService : IStartupService
{
    public const string RegistryValueName = "PersonalLauncher";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly IAppLogger _logger;
    private readonly Func<string> _exePathProvider;

    public StartupService(IAppLogger logger, Func<string>? exePathProvider = null)
    {
        _logger = logger;
        _exePathProvider = exePathProvider ?? ResolveCurrentExecutablePath;
    }

    public StartupStatus GetStatus()
    {
        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            string? value = runKey?.GetValue(RegistryValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
                return StartupStatus.Disabled;

            // Check if user or Windows Task Manager disabled the startup entry in StartupApproved
            if (IsDisabledInStartupApproved())
                return StartupStatus.DisabledByWindows;

            return StartupStatus.Enabled;
        }
        catch (Exception ex)
        {
            _logger.Error("Startup", "Failed to read Windows startup status.", ex);
            return StartupStatus.Error;
        }
    }

    public (bool Success, StartupStatus ResultingStatus, string? ErrorMessage) SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (runKey is null)
            {
                StartupStatus actual = GetStatus();
                return (false, actual, "Unable to open HKCU Run registry key.");
            }

            if (enabled)
            {
                string exePath = _exePathProvider();
                if (string.IsNullOrWhiteSpace(exePath))
                    return (false, GetStatus(), "Could not resolve executable path.");

                // Properly quote executable path and pass --background to start minimized to tray
                string command = $"\"{exePath}\" --background";
                runKey.SetValue(RegistryValueName, command, RegistryValueKind.String);

                // Do NOT silently override StartupApproved if Windows explicitly disabled it; report actual status!
                StartupStatus status = GetStatus();
                _logger.Info("Startup", $"Updated startup registration (enabled=true, status={status}).");
                return (true, status, null);
            }
            else
            {
                if (runKey.GetValue(RegistryValueName) is not null)
                    runKey.DeleteValue(RegistryValueName, throwOnMissingValue: false);

                StartupStatus status = GetStatus();
                _logger.Info("Startup", $"Removed startup registration (status={status}).");
                return (true, status, null);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Startup", $"Failed to update startup registration (enabled={enabled}).", ex);
            return (false, GetStatus(), ex.Message);
        }
    }

    public static string BuildStartupCommand(string executablePath)
        => $"\"{Path.GetFullPath(executablePath)}\" --background";

    private static bool IsDisabledInStartupApproved()
    {
        try
        {
            using RegistryKey? approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: false);
            if (approvedKey?.GetValue(RegistryValueName) is byte[] bytes && bytes.Length >= 1)
            {
                // Per Windows StartupApproved format: 0x02 or 0x06 = enabled; 0x03 or odd values = disabled by user/OS
                byte state = bytes[0];
                return (state & 0x01) == 1 || state == 0x03;
            }
        }
        catch
        {
        }
        return false;
    }

    private static string ResolveCurrentExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }
        return Path.Combine(AppContext.BaseDirectory, "PersonalLauncher.exe");
    }
}
