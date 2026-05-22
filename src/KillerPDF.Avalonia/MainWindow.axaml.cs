using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;

namespace KillerPDF;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OpenBtn_Click(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } }
            }
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null)
        {
            StatusText.Text = "Could not resolve local path for selected file.";
            return;
        }

        try
        {
            // Use the cross-platform Core service we extracted in Phase 4.
            var render = PdfRenderer.RenderPage(path, pageIndex: 0, maxDimension: 2048);
            if (!render.IsValid)
            {
                StatusText.Text = "Could not render page.";
                return;
            }

            // Docnet returns BGRA8888. WriteableBitmap with Bgra8888 + premultiplied alpha.
            var bitmap = new WriteableBitmap(
                new Avalonia.PixelSize(render.Width, render.Height),
                new Avalonia.Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            using (var fb = bitmap.Lock())
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    render.BgraPixels, 0, fb.Address, render.BgraPixels.Length);
            }

            PageImage.Source = bitmap;
            FileLabel.Text = System.IO.Path.GetFileName(path);
            StatusText.Text = $"Rendered page 1 ({render.Width}×{render.Height}px) from {path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Render error: {ex.Message}";
        }
    }
}
