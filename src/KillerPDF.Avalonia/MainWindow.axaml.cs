using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace KillerPDF;

public partial class MainWindow : Window
{
    private string? _originalPath;     // path user opened
    private string? _workingPath;      // temp-file working copy (mutable)
    private PdfDocument? _doc;         // PdfSharp document for mutating ops
    private int _pageCount;
    private int _currentPageIndex;
    private double _zoom = 1.0;

    public MainWindow()
    {
        InitializeComponent();

        // Drag-and-drop file open
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Keyboard shortcuts wired window-wide
        KeyDown += OnKeyDown;

        // Mouse-wheel page navigation in the preview area
        PointerWheelChanged += OnPointerWheelChanged;
    }

    // ── Drag and drop ─────────────────────────────────────────────────
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;
        var files = e.Data.GetFiles();
        if (files is null) return;
        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (path is not null && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                OpenFile(path);
                return;
            }
        }
        StatusText.Text = "Drop a PDF file to open.";
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────
    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // File ops
        if (ctrl && e.Key == Key.O) { OpenBtn_Click(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.S) { SaveAsBtn_Click(null, new RoutedEventArgs()); e.Handled = true; return; }

        // Document-required shortcuts below this point
        if (_doc is null) return;

        // Page navigation
        if (e.Key == Key.PageDown || e.Key == Key.Right || e.Key == Key.Down)
        {
            GoToPage(_currentPageIndex + 1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.PageUp || e.Key == Key.Left || e.Key == Key.Up)
        {
            GoToPage(_currentPageIndex - 1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Home)
        {
            GoToPage(0);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.End)
        {
            GoToPage(_pageCount - 1);
            e.Handled = true;
            return;
        }

        // Zoom
        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            ZoomIn_Click(null, new RoutedEventArgs());
            e.Handled = true; return;
        }
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            ZoomOut_Click(null, new RoutedEventArgs());
            e.Handled = true; return;
        }
        if (ctrl && e.Key == Key.D0)
        {
            FitWidth_Click(null, new RoutedEventArgs());
            e.Handled = true; return;
        }

        // Page ops
        if (e.Key == Key.Delete)
        {
            DeletePage_Click(null, new RoutedEventArgs());
            e.Handled = true; return;
        }

        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void GoToPage(int idx)
    {
        if (idx < 0 || idx >= _pageCount || idx == _currentPageIndex) return;
        _currentPageIndex = idx;
        PageList.SelectedIndex = idx;
        // Selection change handler will trigger render — but if PageList already had idx
        // selected (unlikely here), call render directly.
        // RenderCurrentPage is called via PageList_SelectionChanged.
    }

    // ── Mouse wheel page navigation ───────────────────────────────────
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_doc is null) return;
        // Ctrl+wheel = zoom, plain wheel = page nav
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y > 0) ZoomIn_Click(null, new RoutedEventArgs());
            else if (e.Delta.Y < 0) ZoomOut_Click(null, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        // Plain wheel navigates pages, but only when scroll viewer can't scroll further.
        // For simplicity here: any wheel changes the page.
        if (e.Delta.Y > 0) GoToPage(_currentPageIndex - 1);
        else if (e.Delta.Y < 0) GoToPage(_currentPageIndex + 1);
        e.Handled = true;
    }

    // ── Custom chrome ─────────────────────────────────────────────────
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // Double-click toggles maximize, single click drags.
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            else
            {
                BeginMoveDrag(e);
            }
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ── Open ──────────────────────────────────────────────────────────
    private async void OpenBtn_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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

        OpenFile(path);
    }

    private void OpenFile(string path)
    {
        try
        {
            // Copy to a temp working file so the original stays unlocked and we can
            // safely modify pages via PdfSharp (which opens with a lock in Modify mode).
            var workingPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"killerpdf_{Guid.NewGuid():N}.pdf");
            System.IO.File.Copy(path, workingPath, overwrite: true);

            // Close any previously-open document
            _doc?.Close();

            _doc = PdfReader.Open(workingPath, PdfDocumentOpenMode.Modify);
            _originalPath = path;
            _workingPath = workingPath;
            _pageCount = _doc.PageCount;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Open error: {ex.Message}";
            return;
        }

        _currentPageIndex = 0;
        FileNameLabel.Text = System.IO.Path.GetFileName(path);
        EmptyHint.IsVisible = false;

        RefreshPageList();
        RenderCurrentPage();
    }

    private void RefreshPageList()
    {
        var labels = new List<string>(_pageCount);
        for (int i = 0; i < _pageCount; i++) labels.Add($"Page {i + 1}");
        PageList.ItemsSource = labels;
        if (_pageCount > 0)
        {
            int idx = System.Math.Min(_currentPageIndex, _pageCount - 1);
            PageList.SelectedIndex = idx;
            _currentPageIndex = idx;
        }
    }

    /// <summary>
    /// Saves the in-memory PdfSharp document back to the working temp file so the next
    /// PdfRenderer call (which reads from disk via Docnet) sees the mutated state.
    /// </summary>
    private void PersistWorkingCopy()
    {
        if (_doc is null || _workingPath is null) return;
        _doc.Save(_workingPath);
        // Re-open from disk because PdfSharp's in-memory state diverges from the saved
        // file once we've saved. Cheaper than tracking dirty internals manually.
        _doc.Close();
        _doc = PdfReader.Open(_workingPath, PdfDocumentOpenMode.Modify);
        _pageCount = _doc.PageCount;
    }

    private void PageList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PageList.SelectedIndex < 0 || PageList.SelectedIndex == _currentPageIndex) return;
        _currentPageIndex = PageList.SelectedIndex;
        RenderCurrentPage();
    }

    private void RenderCurrentPage()
    {
        if (_workingPath is null) return;
        try
        {
            int maxDim = (int)System.Math.Min(6144, 2048 * System.Math.Max(1.0, _zoom));
            var render = PdfRenderer.RenderPage(_workingPath, _currentPageIndex, maxDim);
            if (!render.IsValid)
            {
                StatusText.Text = $"Could not render page {_currentPageIndex + 1}";
                return;
            }

            var bitmap = new WriteableBitmap(
                new Avalonia.PixelSize(render.Width, render.Height),
                new Avalonia.Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);
            using (var fb = bitmap.Lock())
                System.Runtime.InteropServices.Marshal.Copy(
                    render.BgraPixels, 0, fb.Address, render.BgraPixels.Length);

            PageImage.Source = bitmap;
            var nameOnDisk = _originalPath is not null
                ? System.IO.Path.GetFileName(_originalPath)
                : "(untitled)";
            StatusText.Text = $"{nameOnDisk} — page {_currentPageIndex + 1} of {_pageCount}";
            PageCounter.Text = $"Page {_currentPageIndex + 1} / {_pageCount}";
            ZoomLabel.Text = $"{(int)(_zoom * 100)}%";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Render error: {ex.Message}";
        }
    }

    // ── Page operations ───────────────────────────────────────────────
    private void InsertBlank_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null) { StatusText.Text = "Open a PDF first."; return; }
        int insertedAt = PdfDocumentService.InsertBlankPage(_doc, _currentPageIndex);
        PersistWorkingCopy();
        _currentPageIndex = insertedAt;
        RefreshPageList();
        RenderCurrentPage();
        StatusText.Text = $"Inserted blank page at position {insertedAt + 1}";
    }

    private void RotateLeft_Click(object? sender, RoutedEventArgs e) => RotateBy(-90);
    private void RotateRight_Click(object? sender, RoutedEventArgs e) => RotateBy(90);

    private void RotateBy(int delta)
    {
        if (_doc is null) { StatusText.Text = "Open a PDF first."; return; }
        PdfDocumentService.RotatePages(_doc, new[] { _currentPageIndex }, delta);
        PersistWorkingCopy();
        RenderCurrentPage();
        StatusText.Text = $"Rotated page {_currentPageIndex + 1} by {delta}°";
    }

    private void MoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null || _currentPageIndex <= 0) return;
        PdfDocumentService.MovePage(_doc, _currentPageIndex, _currentPageIndex - 1);
        PersistWorkingCopy();
        _currentPageIndex -= 1;
        RefreshPageList();
        RenderCurrentPage();
    }

    private void MoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null || _currentPageIndex >= _pageCount - 1) return;
        PdfDocumentService.MovePage(_doc, _currentPageIndex, _currentPageIndex + 1);
        PersistWorkingCopy();
        _currentPageIndex += 1;
        RefreshPageList();
        RenderCurrentPage();
    }

    private void DeletePage_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null) { StatusText.Text = "Open a PDF first."; return; }
        if (_pageCount <= 1) { StatusText.Text = "Cannot delete the last remaining page."; return; }
        PdfDocumentService.DeletePages(_doc, new[] { _currentPageIndex });
        PersistWorkingCopy();
        if (_currentPageIndex >= _pageCount) _currentPageIndex = _pageCount - 1;
        RefreshPageList();
        RenderCurrentPage();
        StatusText.Text = $"Deleted page — {_pageCount} remaining";
    }

    // ── Save ──────────────────────────────────────────────────────────
    private void SaveBtn_Click(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "Save in place: not yet implemented (no editing tools yet)";
    }

    private async void SaveAsBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null || _workingPath is null)
        {
            StatusText.Text = "Open a PDF first.";
            return;
        }
        var suggested = _originalPath is not null
            ? System.IO.Path.GetFileNameWithoutExtension(_originalPath)
            : "document";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save As",
            SuggestedFileName = suggested,
            DefaultExtension = "pdf",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } }
            }
        });
        if (file is null) return;
        var target = file.TryGetLocalPath();
        if (target is null)
        {
            StatusText.Text = "Could not resolve save target path.";
            return;
        }
        try
        {
            _doc.Save(target);
            StatusText.Text = $"Saved to {System.IO.Path.GetFileName(target)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    // ── Zoom ──────────────────────────────────────────────────────────
    private void ZoomIn_Click(object? sender, RoutedEventArgs e)
    {
        _zoom = System.Math.Min(4.0, _zoom * 1.25);
        RenderCurrentPage();
    }

    private void ZoomOut_Click(object? sender, RoutedEventArgs e)
    {
        _zoom = System.Math.Max(0.25, _zoom / 1.25);
        RenderCurrentPage();
    }

    private void FitWidth_Click(object? sender, RoutedEventArgs e)
    {
        _zoom = 1.0;
        RenderCurrentPage();
    }
}
