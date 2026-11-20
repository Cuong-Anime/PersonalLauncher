using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PersonalLauncher.Infrastructure.Assets;

/// <summary>
/// Extracts a crisp static JPEG thumbnail frame from a video file using Windows Shell's
/// native Media Foundation thumbnail provider (IShellItemImageFactory), with zero external binaries.
/// </summary>
public static class VideoThumbnailExtractor
{
    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const uint SIIGBF_THUMBNAILONLY = 0x00000008;

    public static (bool Success, int Width, int Height) TryExtractThumbnailJpeg(
        string videoFilePath,
        string destinationJpegPath,
        int maxDimension = 640)
    {
        if (string.IsNullOrWhiteSpace(videoFilePath) || !File.Exists(videoFilePath))
            return (false, 0, 0);

        bool success = false;
        int width = 0;
        int height = 0;

        // Run COM Shell call on a dedicated STA thread so it works reliably from ThreadPool workers
        var thread = new Thread(() =>
        {
            IntPtr hBitmap = IntPtr.Zero;
            IShellItemImageFactory? factory = null;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                int hr = SHCreateItemFromParsingName(videoFilePath, IntPtr.Zero, ref iid, out factory);
                if (hr != 0 || factory is null)
                    return;

                var size = new SIZE { cx = maxDimension, cy = maxDimension };
                hr = factory.GetImage(size, SIIGBF_BIGGERSIZEOK | SIIGBF_THUMBNAILONLY, out hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                    return;

                BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();

                width = source.PixelWidth;
                height = source.PixelHeight;

                var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var fs = new FileStream(destinationJpegPath, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(fs);
                success = true;
            }
            catch
            {
                success = false;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                    DeleteObject(hBitmap);
                if (factory is not null)
                    Marshal.ReleaseComObject(factory);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));

        return (success, width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
