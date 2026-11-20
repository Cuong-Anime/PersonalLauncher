using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace PersonalLauncher.App.Controls;

/// <summary>
/// Shared singleton LibVLC host so native VLC plugins are initialized once and disposed cleanly on app exit.
/// Works on ALL Windows 11 systems without legacy Windows Media Player (wmp.dll) and supports
/// H.264 (8/10-bit, 4K 60fps), H.265/HEVC, VP8/VP9 (.webm), AV1, MKV, MOV, WMV, AVI.
/// </summary>
public static class LiveVideoEngine
{
    private static readonly object InitLock = new();
    private static LibVLC? _instance;
    private static bool _initFailed;
    private static bool _shutdown;

    public static LibVLC? GetOrCreate(Action<string>? logError = null)
    {
        if (_shutdown || _initFailed)
            return null;

        lock (InitLock)
        {
            if (_shutdown || _initFailed)
                return null;
            if (_instance is not null)
                return _instance;

            try
            {
                LibVLCSharp.Shared.Core.Initialize();
                _instance = new LibVLC(
                    "--no-audio",
                    "--no-osd",
                    "--no-snapshot-preview",
                    "--no-sub-autodetect-file",
                    "--avcodec-hw=any",
                    "--quiet");
                return _instance;
            }
            catch (Exception ex)
            {
                _initFailed = true;
                logError?.Invoke($"LibVLC initialization failed: {ex.Message}");
                return null;
            }
        }
    }

    public static void Shutdown()
    {
        lock (InitLock)
        {
            _shutdown = true;
            try { _instance?.Dispose(); } catch { }
            _instance = null;
        }
    }
}

/// <summary>
/// Zero-airspace WPF live video renderer backed by LibVLC SetVideoCallbacks -> WriteableBitmap.
/// Renders inside a standard WPF Image (Stretch=UniformToFill), so translucent sidebars,
/// rounded corner clips, drop shadows, overlays, and popups composite smoothly on top!
/// </summary>
public sealed class LiveVideoSurface : Border, IDisposable
{
    private readonly Image _image;
    private readonly object _frameLock = new();

    private LibVLCSharp.Shared.MediaPlayer? _player;
    private Media? _currentMedia;
    private string? _currentPath;
    private WriteableBitmap? _bitmap;
    private IntPtr _decodeBuffer = IntPtr.Zero;
    private byte[]? _frontBuffer;
    private int _videoWidth;
    private int _videoHeight;
    private int _videoStride;
    private int _dispatchPending;
    private volatile bool _isPlaying;
    private volatile bool _isDisposed;
    private bool _firstFrameRaised;
    private long _playSessionId;

    // Root callback delegates as instance fields so GC never collects them while LibVLC holds native pointers!
    private readonly LibVLCSharp.Shared.MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly LibVLCSharp.Shared.MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly LibVLCSharp.Shared.MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly LibVLCSharp.Shared.MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly LibVLCSharp.Shared.MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    public int MaxDecodeWidth { get; set; } = 1280;
    public int MaxDecodeHeight { get; set; } = 720;
    public bool HasRenderedFrame => _firstFrameRaised;

    public bool IsPlayingPath(string videoFilePath) =>
        !_isDisposed && _isPlaying && !string.IsNullOrWhiteSpace(videoFilePath) &&
        string.Equals(_currentPath, videoFilePath, StringComparison.OrdinalIgnoreCase);

    public event EventHandler? FirstFrameRendered;
    public event EventHandler<string>? PlaybackError;

    public LiveVideoSurface()
    {
        Background = Brushes.Transparent;
        _image = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Child = _image;

        _formatCb = OnVideoFormat;
        _cleanupCb = OnVideoCleanup;
        _lockCb = OnVideoLock;
        _unlockCb = OnVideoUnlock;
        _displayCb = OnVideoDisplay;
    }

    public bool Play(string videoFilePath)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(videoFilePath) || !File.Exists(videoFilePath))
            return false;

        if (string.Equals(_currentPath, videoFilePath, StringComparison.OrdinalIgnoreCase) && _isPlaying)
        {
            Resume();
            return true;
        }

        LibVLC? libVlc = LiveVideoEngine.GetOrCreate(err => PlaybackError?.Invoke(this, err));
        if (libVlc is null)
            return false;

        try
        {
            EnsurePlayer(libVlc);
            if (_player is null)
                return false;

            long session = Interlocked.Increment(ref _playSessionId);
            _currentPath = videoFilePath;
            _firstFrameRaised = false;
            _isPlaying = true;

            Media? oldMedia = _currentMedia;
            var media = new Media(libVlc, new Uri(videoFilePath, UriKind.Absolute));
            media.AddOption(":input-repeat=65535");
            media.AddOption(":no-audio");
            _currentMedia = media;

            // Start playback on threadpool so UI thread never blocks during demuxer open/seek
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (_isDisposed || Interlocked.Read(ref _playSessionId) != session)
                    return;

                try
                {
                    _player?.Play(media);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() => PlaybackError?.Invoke(this, ex.Message));
                }
                finally
                {
                    try { oldMedia?.Dispose(); } catch { }
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke(this, ex.Message);
            StopAndClear();
            return false;
        }
    }

    public void Pause()
    {
        if (_isDisposed || _player is null || !_isPlaying)
            return;

        LibVLCSharp.Shared.MediaPlayer p = _player;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { p.SetPause(true); } catch { }
        });
    }

    public void Resume()
    {
        if (_isDisposed || _player is null || !_isPlaying)
            return;

        LibVLCSharp.Shared.MediaPlayer p = _player;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { p.SetPause(false); } catch { }
        });
    }

    public void StopAndClear()
    {
        Interlocked.Increment(ref _playSessionId);
        _isPlaying = false;
        _currentPath = null;
        _firstFrameRaised = false;

        if (Dispatcher.CheckAccess())
        {
            _image.Source = null;
            _bitmap = null;
        }
        else
        {
            Dispatcher.BeginInvoke(() =>
            {
                _image.Source = null;
                _bitmap = null;
            });
        }

        LibVLCSharp.Shared.MediaPlayer? p = _player;
        Media? m = _currentMedia;
        _currentMedia = null;

        if (p is not null)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { p.Stop(); } catch { }
                try { m?.Dispose(); } catch { }
            });
        }
    }

    private void EnsurePlayer(LibVLC libVlc)
    {
        if (_player is not null)
            return;

        _player = new LibVLCSharp.Shared.MediaPlayer(libVlc)
        {
            Mute = true,
            Volume = 0
        };
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
        _player.EndReached += OnEndReached;
        _player.EncounteredError += OnEncounteredError;
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // Safety fallback if input-repeat limit ever completes
        long session = Interlocked.Read(ref _playSessionId);
        string? path = _currentPath;
        if (_isDisposed || !_isPlaying || string.IsNullOrWhiteSpace(path))
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_isDisposed || !_isPlaying || Interlocked.Read(ref _playSessionId) != session)
                return;
            try
            {
                if (_currentMedia is not null)
                    _player?.Play(_currentMedia);
            }
            catch { }
        });
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => PlaybackError?.Invoke(this, "LibVLC encountered a playback error."));
    }

    private uint OnVideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // Force 32-bit BGRA/BGRX ("RV32") matching WPF PixelFormats.Bgr32
        Marshal.WriteByte(chroma, 0, (byte)'R');
        Marshal.WriteByte(chroma, 1, (byte)'V');
        Marshal.WriteByte(chroma, 2, (byte)'3');
        Marshal.WriteByte(chroma, 3, (byte)'2');

        uint srcW = width == 0 ? 1280u : width;
        uint srcH = height == 0 ? 720u : height;

        // Preserve exact native aspect ratio while scaling down 4K/2K to fit MaxDecodeWidth/MaxDecodeHeight!
        double scale = Math.Min(1.0, Math.Min((double)MaxDecodeWidth / srcW, (double)MaxDecodeHeight / srcH));
        // If video is ultrawide/tall, keep cover resolution high enough for crisp UniformToFill
        if (srcW > MaxDecodeWidth && srcH > MaxDecodeHeight)
        {
            double coverScale = Math.Max((double)MaxDecodeWidth / srcW, (double)MaxDecodeHeight / srcH);
            scale = Math.Min(1.0, coverScale);
        }

        uint targetW = Math.Max(16u, ((uint)Math.Round(srcW * scale) / 4u) * 4u);
        uint targetH = Math.Max(16u, ((uint)Math.Round(srcH * scale) / 4u) * 4u);
        uint stride = targetW * 4u;

        width = targetW;
        height = targetH;
        pitches = stride;
        lines = targetH;

        lock (_frameLock)
        {
            FreeNativeBufferNoLock();
            _videoWidth = (int)targetW;
            _videoHeight = (int)targetH;
            _videoStride = (int)stride;
            int totalBytes = _videoStride * _videoHeight;
            _decodeBuffer = Marshal.AllocHGlobal(totalBytes);
            _frontBuffer = new byte[totalBytes];
        }

        return 1;
    }

    private void OnVideoCleanup(ref IntPtr opaque)
    {
        lock (_frameLock)
        {
            FreeNativeBufferNoLock();
        }
    }

    private IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
    {
        lock (_frameLock)
        {
            if (_decodeBuffer != IntPtr.Zero)
                Marshal.WriteIntPtr(planes, _decodeBuffer);
        }
        return IntPtr.Zero;
    }

    private unsafe void OnVideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        if (!_isPlaying || _isDisposed)
            return;

        lock (_frameLock)
        {
            if (_decodeBuffer == IntPtr.Zero || _frontBuffer is null)
                return;

            int len = _frontBuffer.Length;
            fixed (byte* dst = _frontBuffer)
            {
                Buffer.MemoryCopy((void*)_decodeBuffer, dst, len, len);
            }
        }
    }

    private void OnVideoDisplay(IntPtr opaque, IntPtr picture)
    {
        if (!_isPlaying || _isDisposed)
            return;

        if (Interlocked.CompareExchange(ref _dispatchPending, 1, 0) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, PresentFrameOnUiThread);
        }
    }

    private unsafe void PresentFrameOnUiThread()
    {
        Interlocked.Exchange(ref _dispatchPending, 0);
        if (_isDisposed || !_isPlaying)
            return;

        lock (_frameLock)
        {
            if (_frontBuffer is null || _videoWidth <= 0 || _videoHeight <= 0)
                return;

            if (_bitmap is null || _bitmap.PixelWidth != _videoWidth || _bitmap.PixelHeight != _videoHeight)
            {
                _bitmap = new WriteableBitmap(_videoWidth, _videoHeight, 96, 96, PixelFormats.Bgr32, null);
                _image.Source = _bitmap;
            }

            int expectedBytes = _videoStride * _videoHeight;
            if (_frontBuffer.Length < expectedBytes)
                return;

            _bitmap.Lock();
            try
            {
                fixed (byte* src = _frontBuffer)
                {
                    Buffer.MemoryCopy(src, (void*)_bitmap.BackBuffer, expectedBytes, expectedBytes);
                }
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, _videoWidth, _videoHeight));
            }
            finally
            {
                _bitmap.Unlock();
            }
        }

        if (!_firstFrameRaised)
        {
            _firstFrameRaised = true;
            FirstFrameRendered?.Invoke(this, EventArgs.Empty);
        }
    }

    private void FreeNativeBufferNoLock()
    {
        if (_decodeBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_decodeBuffer);
            _decodeBuffer = IntPtr.Zero;
        }
        _frontBuffer = null;
        _videoWidth = 0;
        _videoHeight = 0;
        _videoStride = 0;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;
        _isPlaying = false;
        Interlocked.Increment(ref _playSessionId);

        LibVLCSharp.Shared.MediaPlayer? p = _player;
        Media? m = _currentMedia;
        _player = null;
        _currentMedia = null;

        if (p is not null)
        {
            p.EndReached -= OnEndReached;
            p.EncounteredError -= OnEncounteredError;
            try { p.Stop(); } catch { }
            try { p.Dispose(); } catch { }
        }
        try { m?.Dispose(); } catch { }

        lock (_frameLock)
        {
            FreeNativeBufferNoLock();
        }
    }
}
