using System.Collections.Concurrent;
using System.Diagnostics;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Infrastructure.Games;

public sealed class GameLaunchService : IGameLaunchService
{
    private readonly IGameLibraryService _gameLibrary;
    private readonly ISecretAccessService _secretAccess;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, GameLaunchState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _activeProcessIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _launchLocks = new(StringComparer.Ordinal);

    public event EventHandler<(string GameId, GameLaunchState State)>? GameStateChanged;

    public GameLaunchService(
        IGameLibraryService gameLibrary,
        ISecretAccessService secretAccess,
        IAppLogger logger)
    {
        _gameLibrary = gameLibrary;
        _secretAccess = secretAccess;
        _logger = logger;
    }

    public GameLaunchState GetState(GameEntry game)
    {
        if (_states.TryGetValue(game.Id, out GameLaunchState current) &&
            current is GameLaunchState.Launching or GameLaunchState.Running)
        {
            return current;
        }

        return File.Exists(game.ExePath) ? GameLaunchState.Ready : GameLaunchState.Missing;
    }

    public async Task<GameLaunchResult> LaunchAsync(string gameId, CancellationToken ct = default)
    {
        // Service-level Secret authorization check happens inside GetGameByIdAsync too
        GameEntry? game = await _gameLibrary.GetGameByIdAsync(gameId, ct);
        if (game is null)
        {
            return new GameLaunchResult { State = GameLaunchState.Missing, ErrorMessage = "Game not found." };
        }

        if (game.Visibility == GameVisibility.Secret && !_secretAccess.HasScope(SecretScope.Browse))
            throw new SecretUnauthorizedException(SecretScope.Browse);

        SemaphoreSlim gate = _launchLocks.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
        // Block rapid repeat clicks from spawning multiple instances
        if (!await gate.WaitAsync(0, ct))
        {
            return new GameLaunchResult { State = _states.GetValueOrDefault(gameId, GameLaunchState.Launching) };
        }

        try
        {
            if (_states.TryGetValue(gameId, out GameLaunchState existingState) &&
                existingState is GameLaunchState.Launching or GameLaunchState.Running)
            {
                return new GameLaunchResult { State = existingState };
            }

            if (string.IsNullOrWhiteSpace(game.ExePath) || !File.Exists(game.ExePath))
            {
                SetState(gameId, GameLaunchState.Missing);
                return new GameLaunchResult
                {
                    State = GameLaunchState.Missing,
                    ErrorMessage = "Executable file was not found."
                };
            }

            // Verify read access before starting process
            try
            {
                using FileStream _ = File.Open(game.ExePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex)
            {
                SetState(gameId, GameLaunchState.Error);
                return new GameLaunchResult
                {
                    State = GameLaunchState.Error,
                    ErrorMessage = $"Cannot access executable: {ex.Message}"
                };
            }

            SetState(gameId, GameLaunchState.Launching);

            string workingDir = !string.IsNullOrWhiteSpace(game.WorkingDirectory) && Directory.Exists(game.WorkingDirectory)
                ? game.WorkingDirectory
                : Path.GetDirectoryName(game.ExePath) ?? Environment.CurrentDirectory;

            // Security Rule 6: absolute path, direct ProcessStartInfo (NO cmd /c, NO PowerShell, NO runas elevation!)
            var psi = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(game.ExePath),
                WorkingDirectory = workingDir,
                UseShellExecute = false
            };

            foreach (string arg in game.Arguments)
            {
                if (!string.IsNullOrEmpty(arg))
                    psi.ArgumentList.Add(arg);
            }

            Process? process = Process.Start(psi);
            if (process is null)
            {
                SetState(gameId, GameLaunchState.Error);
                return new GameLaunchResult
                {
                    State = GameLaunchState.Error,
                    ErrorMessage = "Failed to start game process."
                };
            }

            int pid = process.Id;
            _activeProcessIds[gameId] = pid;
            SetState(gameId, GameLaunchState.Running);

            // Never log secret game name or path!
            if (game.Visibility == GameVisibility.Public)
                _logger.Info("Launch", $"Started public game id={game.Id} pid={pid}");
            else
                _logger.Info("Launch", $"Started secret game pid={pid}");

            // Track direct process exit cleanly; exiting launcher or locking Secret does NOT kill the game!
            _ = MonitorProcessExitAsync(gameId, process);

            return new GameLaunchResult
            {
                State = GameLaunchState.Running,
                ProcessId = pid
            };
        }
        catch (Exception ex)
        {
            SetState(gameId, GameLaunchState.Error);
            _logger.Error("Launch", $"Failed to start game id={gameId}.", ex);
            return new GameLaunchResult
            {
                State = GameLaunchState.Error,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task MonitorProcessExitAsync(string gameId, Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
            // Ignore access/exit monitoring errors (e.g., bootstrapper exiting immediately)
        }
        finally
        {
            _activeProcessIds.TryRemove(gameId, out _);
            SetState(gameId, GameLaunchState.Ready);
            process.Dispose();
        }
    }

    private void SetState(string gameId, GameLaunchState state)
    {
        _states[gameId] = state;
        GameStateChanged?.Invoke(this, (gameId, state));
    }
}
