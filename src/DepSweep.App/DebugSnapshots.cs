#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DepSweep.App;

/// <summary>
/// Debug-only helper for automated UI checks: when DEPSWEEP_SNAPSHOT_DIR is set, every "&lt;name&gt;.request"
/// file dropped in that folder is answered with "&lt;name&gt;.png", a rendering of the main window.
/// </summary>
internal static class DebugSnapshots
{
    public static void StartIfRequested(Window window)
    {
        var dir = Environment.GetEnvironmentVariable("DEPSWEEP_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            foreach (var request in Directory.GetFiles(dir, "*.request"))
            {
                Save(window, Path.ChangeExtension(request, ".png"));
                var i = 1;
                foreach (var other in Application.Current.Windows.OfType<Window>().Where(w => w != window && w.IsVisible))
                {
                    Save(other, Path.ChangeExtension(request, null) + $"-dialog{i++}.png");
                }

                File.Delete(request);
            }
        };
        timer.Start();
    }

    private static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = window.Background is SolidColorBrush { Color.A: 255 } solid ? solid : SystemColors.WindowBrush;
            dc.DrawRectangle(background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
#endif
