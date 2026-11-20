using System.Drawing;
using System.Windows.Forms;
using PersonalLauncher.Core.Contracts;

namespace PersonalLauncher.Infrastructure.Windows;

public sealed class TrayService : ITrayService
{
    private readonly IAppLogger _logger;
    private readonly string _iconFilePath;
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _menu;
    private ToolStripMenuItem? _openItem;
    private ToolStripMenuItem? _lockSecretItem;
    private ToolStripMenuItem? _exitItem;
    private Icon? _loadedIcon;
    private bool _disposed;

    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }

    public event EventHandler? OpenRequested;
    public event EventHandler? LockSecretRequested;
    public event EventHandler? ExitRequested;

    public TrayService(IAppLogger logger, string iconFilePath)
    {
        _logger = logger;
        _iconFilePath = iconFilePath;
    }

    public bool Initialize()
    {
        if (_disposed)
            return false;
        if (_notifyIcon is not null && IsAvailable)
            return true;

        try
        {
            _menu = new ContextMenuStrip();

            _openItem = new ToolStripMenuItem("Mở launcher");
            _openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

            _lockSecretItem = new ToolStripMenuItem("Khóa Secret") { Visible = false };
            _lockSecretItem.Click += (_, _) => LockSecretRequested?.Invoke(this, EventArgs.Empty);

            _exitItem = new ToolStripMenuItem("Thoát");
            _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

            // Strictly ONLY Open, Lock Secret (when unlocked), Exit — NO recent games list!
            _menu.Items.Add(_openItem);
            _menu.Items.Add(_lockSecretItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_exitItem);

            _loadedIcon = LoadTrayIcon();
            _notifyIcon = new NotifyIcon
            {
                Icon = _loadedIcon,
                Text = "PersonalLauncher",
                ContextMenuStrip = _menu,
                Visible = true
            };
            _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

            IsAvailable = true;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            LastError = ex.Message;
            _logger.Error("Tray", "Failed to initialize system tray icon.", ex);
            return false;
        }
    }

    public void UpdateState(bool secretUnlocked, string openLabel, string lockSecretLabel, string exitLabel, string tooltip)
    {
        if (!IsAvailable || _notifyIcon is null)
            return;

        try
        {
            if (_openItem is not null) _openItem.Text = openLabel;
            if (_lockSecretItem is not null)
            {
                _lockSecretItem.Text = lockSecretLabel;
                _lockSecretItem.Visible = secretUnlocked;
            }
            if (_exitItem is not null) _exitItem.Text = exitLabel;

            string safeTip = string.IsNullOrWhiteSpace(tooltip) ? "PersonalLauncher" : tooltip;
            _notifyIcon.Text = safeTip.Length > 63 ? safeTip[..63] : safeTip;
        }
        catch (Exception ex)
        {
            _logger.Warn("Tray", $"Could not update tray menu labels: {ex.Message}");
        }
    }

    public void Hide()
    {
        if (_notifyIcon is not null)
            _notifyIcon.Visible = false;
    }

    private Icon LoadTrayIcon()
    {
        if (File.Exists(_iconFilePath))
        {
            return new Icon(_iconFilePath);
        }

        string exePath = Environment.ProcessPath ?? string.Empty;
        if (File.Exists(exePath))
        {
            Icon? extracted = Icon.ExtractAssociatedIcon(exePath);
            if (extracted is not null)
                return extracted;
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        IsAvailable = false;

        try
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            _menu?.Dispose();
            _menu = null;
            _loadedIcon?.Dispose();
            _loadedIcon = null;
        }
        catch
        {
        }
    }
}
