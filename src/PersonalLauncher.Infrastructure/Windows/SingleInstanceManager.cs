using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace PersonalLauncher.Infrastructure.Windows;

public sealed class SingleInstanceManager : IDisposable
{
    private readonly string _pipeName;
    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private bool _ownsMutex;

    public event EventHandler? ActivationRequested;

    public SingleInstanceManager(string appIdentifier = "PersonalLauncher")
    {
        string userKey = $"{Environment.UserDomainName}\\{Environment.UserName}".ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userKey)))[..16];
        string mutexName = $@"Local\{appIdentifier}_SingleInstance_{hash}";
        _pipeName = $"{appIdentifier}_ActivatePipe_{hash}";

        _mutex = new Mutex(initiallyOwned: true, name: mutexName, out _ownsMutex);
    }

    public bool IsPrimaryInstance => _ownsMutex;

    public void StartActivationListener()
    {
        if (!_ownsMutex || _listenerTask is not null)
            return;

        _listenerCts = new CancellationTokenSource();
        CancellationToken ct = _listenerCts.Token;

        _listenerTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? command = await reader.ReadLineAsync(ct);
                    if (string.Equals(command, "ACTIVATE", StringComparison.Ordinal))
                    {
                        ActivationRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(250, ct);
                }
            }
        }, ct);
    }

    public void NotifyPrimaryInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(timeout: 1500);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine("ACTIVATE");
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
        }
        catch { }

        if (_ownsMutex && _mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
        _mutex = null;
    }
}
