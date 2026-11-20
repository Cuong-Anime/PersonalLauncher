using System.Text;
using PersonalLauncher.Core.Contracts;

namespace PersonalLauncher.Infrastructure.Persistence;

public sealed class RollingFileLogger : IAppLogger
{
    private const long MaxFileSizeBytes = 2 * 1024 * 1024; // 2 MiB
    private const int MaxRetainedFiles = 5;

    private readonly string _logsDirectory;
    private readonly object _sync = new();

    public RollingFileLogger(string logsDirectory)
    {
        _logsDirectory = logsDirectory;
    }

    public void Info(string category, string message) => Write("INFO", category, message, null);
    public void Warn(string category, string message) => Write("WARN", category, message, null);
    public void Error(string category, string message, Exception? exception = null) => Write("ERROR", category, message, exception);

    private void Write(string level, string category, string message, Exception? exception)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_logsDirectory);
                string fileName = $"app-{DateTimeOffset.UtcNow:yyyyMMdd}.log";
                string filePath = Path.Combine(_logsDirectory, fileName);

                if (File.Exists(filePath) && new FileInfo(filePath).Length >= MaxFileSizeBytes)
                {
                    string rotated = Path.Combine(_logsDirectory, $"app-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");
                    File.Move(filePath, rotated, overwrite: true);
                    PruneOldLogs();
                }

                var sb = new StringBuilder();
                sb.Append(DateTimeOffset.UtcNow.ToString("O"))
                  .Append(" [").Append(level).Append("] ")
                  .Append('[').Append(category).Append("] ")
                  .Append(message);

                if (exception is not null)
                {
                    sb.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
                }
                sb.AppendLine();

                File.AppendAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the launcher
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            var files = Directory.GetFiles(_logsDirectory, "app-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxRetainedFiles);

            foreach (FileInfo file in files)
            {
                try { file.Delete(); } catch { }
            }
        }
        catch
        {
        }
    }
}
