using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalLauncher.App.Services;

/// <summary>
/// Bounded LRU BitmapSource loader that loads images with BitmapCacheOption.OnLoad + Freeze()
/// so files on disk are never locked and memory stays bounded across hundreds of games and wallpaper switches.
/// </summary>
public sealed class ImageCacheService
{
    private readonly int _maxEntries;
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, ImageSource Image)> _lru = new();

    public ImageCacheService(int maxEntries = 140)
    {
        _maxEntries = Math.Max(16, maxEntries);
    }

    public ImageSource? Load(string? filePath, int? decodePixelWidth = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        long ticks;
        try
        {
            ticks = File.GetLastWriteTimeUtc(filePath).Ticks;
        }
        catch
        {
            return null;
        }

        string key = $"{filePath}|{decodePixelWidth ?? 0}|{ticks}";
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existingNode))
            {
                _lru.Remove(existingNode);
                _lru.AddFirst(existingNode);
                return existingNode.Value.Image;
            }
        }

        ImageSource? loaded = LoadFromDisk(filePath, decodePixelWidth);
        if (loaded is null)
            return null;

        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existingNode))
            {
                _lru.Remove(existingNode);
                _lru.AddFirst(existingNode);
                return existingNode.Value.Image;
            }

            var node = new LinkedListNode<(string, ImageSource)>((key, loaded));
            _lru.AddFirst(node);
            _map[key] = node;

            while (_lru.Count > _maxEntries && _lru.Last is not null)
            {
                var tail = _lru.Last;
                _lru.RemoveLast();
                _map.Remove(tail.Value.Key);
            }
        }

        return loaded;
    }

    public Task<ImageSource?> LoadAsync(string? filePath, int? decodePixelWidth = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Task.FromResult<ImageSource?>(null);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Load(filePath, decodePixelWidth);
        }, ct);
    }

    public void Clear()
    {
        lock (_sync)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    private static ImageSource? LoadFromDisk(string filePath, int? decodePixelWidth)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            using var ms = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth.HasValue && decodePixelWidth.Value > 0)
                bitmap.DecodePixelWidth = decodePixelWidth.Value;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
