using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace KillerPDF;

public partial class MainWindow : Window
{
    private string? _originalPath;
    private string? _workingPath;
    private PdfDocument? _doc;
    private int _pageCount;
    private int _currentPageIndex;
    private double _zoom = 1.0;

    // ── Editing state ────────────────────────────────────────────────
    private EditTool _currentTool = EditTool.Select;
    private readonly Dictionary<int, List<PageAnnotation>> _annotations = new();
    private readonly Dictionary<int, (int w, int h)> _renderDims = new();

    private bool _isDrawing;
    private Avalonia.Point _drawStart;
    private Rectangle? _activePreview;

    // Default highlight color (yellow with low opacity)
    private static readonly ColorRgba HighlightColor = new(255, 255, 0, 80);

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        KeyDown += OnKeyDown;
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

        if (ctrl && e.Key == Key.O) { OpenBtn_Click(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.S) { SaveAsBtn_Click(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && !shift && e.Key == Key.S) { SaveBtn_Click(null, new RoutedEventArgs()); e.Handled = true; return; }

        if (_doc is null) return;

        if (e.Key == Key.PageDown || e.Key == Key.Right || e.Key == Key.Down) { GoToPage(_currentPageIndex + 1); e.Handled = true; return; }
        if (e.Key == Key.PageUp || e.Key == Key.Left || e.Key == Key.Up) { GoToPage(_currentPageIndex - 1); e.Handled = true; return; }
        if (e.Key == Key.Home) { GoToPage(0); e.Handled = true; return; }
        if (e.Key == Key.End) { GoToPage(_pageCount - 1); e.Handled = true; return; }

        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { ZoomIn_Click(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { ZoomOut_Click(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D0) { FitWidth_Click(null, new RoutedEventArgs()); e.Handled = true; return; }

        if (e.Key == Key.Delete) { DeletePage_Click(null, new RoutedEventArgs()); e.Handled = true; return; }

        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void GoToPage(int idx)
    {
        if (idx < 0 || idx >= _pageCount || idx == _currentPageIndex) return;
        _currentPageIndex = idx;
        PageList.SelectedIndex = idx;
    }

    // ── Mouse wheel ──────────────────────────────────────────────────
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_doc is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y > 0) ZoomIn_Click(null, new RoutedEventArgs());
            else if (e.Delta.Y < 0) ZoomOut_Click(null, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Delta.Y > 0) GoToPage(_currentPageIndex - 1);
        else if (e.Delta.Y < 0) GoToPage(_currentPageIndex + 1);
        e.Handled = true;
    }

    // ── Title bar chrome ──────────────────────────────────────────────
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
    }
    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ── Open ──────────────────────────────────────────────────────────
    private async void OpenBtn_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } } }
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) { StatusText.Text = "Could not resolve local path for selected file."; return; }
        OpenFile(path);
    }

    private void OpenFile(string path)
    {
        try
        {
            var workingPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"killerpdf_{Guid.NewGuid():N}.pdf");
            System.IO.File.Copy(path, workingPath, overwrite: true);

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
        _annotations.Clear();
        _renderDims.Clear();
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

    private void PageList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PageList.SelectedIndex < 0 || PageList.SelectedIndex == _currentPageIndex) return;
        _currentPageIndex = PageList.SelectedIndex;
        RenderCurrentPage();
    }

    private void PersistWorkingCopy()
    {
        if (_doc is null || _workingPath is null) return;
        _doc.Save(_workingPath);
        _doc.Close();
        _doc = PdfReader.Open(_workingPath, PdfDocumentOpenMode.Modify);
        _pageCount = _doc.PageCount;
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

            // Annotation canvas dimensions are independent of bitmap dimensions: it tracks
            // the unscaled canvas pixel size so annotation coordinates stay stable across
            // zoom re-renders. This is the same approach used by the WPF MainWindow.
            int dipW = (int)System.Math.Round(render.Width / System.Math.Max(1.0, _zoom));
            int dipH = (int)System.Math.Round(render.Height / System.Math.Max(1.0, _zoom));
            _renderDims[_currentPageIndex] = (dipW, dipH);

            var bitmap = new WriteableBitmap(
                new Avalonia.PixelSize(render.Width, render.Height),
                new Avalonia.Vector(96 * System.Math.Max(1.0, _zoom), 96 * System.Math.Max(1.0, _zoom)),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);
            using (var fb = bitmap.Lock())
                System.Runtime.InteropServices.Marshal.Copy(
                    render.BgraPixels, 0, fb.Address, render.BgraPixels.Length);

            PageImage.Source = bitmap;
            AnnotationCanvas.Width = dipW;
            AnnotationCanvas.Height = dipH;
            RenderAllAnnotations(_currentPageIndex);

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

    // ── Page ops ──────────────────────────────────────────────────────
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
        if (_doc is null) return;
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
        if (_doc is null) return;
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
        if (_doc is null || _originalPath is null) { StatusText.Text = "Open a PDF first."; return; }
        try
        {
            _doc.Save(_originalPath);
            StatusText.Text = $"Saved to {System.IO.Path.GetFileName(_originalPath)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private async void SaveAsBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null) { StatusText.Text = "Open a PDF first."; return; }
        var suggested = _originalPath is not null
            ? System.IO.Path.GetFileNameWithoutExtension(_originalPath)
            : "document";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save As",
            SuggestedFileName = suggested,
            DefaultExtension = "pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } } }
        });
        if (file is null) return;
        var target = file.TryGetLocalPath();
        if (target is null) { StatusText.Text = "Could not resolve save target path."; return; }
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

    private async void SaveFlattened_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null) { StatusText.Text = "Open a PDF first."; return; }
        var suggested = (_originalPath is not null
            ? System.IO.Path.GetFileNameWithoutExtension(_originalPath)
            : "document") + "-flattened";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Flattened (bake annotations into PDF)",
            SuggestedFileName = suggested,
            DefaultExtension = "pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } } }
        });
        if (file is null) return;
        var target = file.TryGetLocalPath();
        if (target is null) return;
        try
        {
            // Flatten annotations onto the working PdfSharp doc, save to target, then
            // reload the working copy from the pre-flatten state to keep further editing
            // possible. Mirrors what the WPF build does.
            var preFlatten = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"killerpdf_preflatten_{Guid.NewGuid():N}.pdf");
            _doc.Save(preFlatten);
            PdfAnnotationFlattener.FlattenInto(_doc, _annotations, _renderDims);
            _doc.Save(target);
            _doc.Close();
            // Restore from the pre-flatten snapshot
            System.IO.File.Copy(preFlatten, _workingPath!, overwrite: true);
            _doc = PdfReader.Open(_workingPath!, PdfDocumentOpenMode.Modify);
            System.IO.File.Delete(preFlatten);
            RenderCurrentPage();
            StatusText.Text = $"Saved flattened copy to {System.IO.Path.GetFileName(target)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save flattened failed: {ex.Message}";
        }
    }

    // ── Merge ─────────────────────────────────────────────────────────
    private async void MergeBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null) { StatusText.Text = "Open a PDF first."; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Merge PDFs into current document",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } } }
        });
        if (files.Count == 0) return;
        try
        {
            int merged = 0;
            foreach (var f in files)
            {
                var path = f.TryGetLocalPath();
                if (path is null) continue;
                PdfDocumentService.AppendDocument(_doc, path);
                merged++;
            }
            PersistWorkingCopy();
            RefreshPageList();
            RenderCurrentPage();
            StatusText.Text = $"Merged {merged} file(s) — {_pageCount} total pages";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Merge failed: {ex.Message}";
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

    // ── Tool selection ────────────────────────────────────────────────
    private void ToolSelect_Click(object? sender, RoutedEventArgs e) => SetTool(EditTool.Select);
    private void ToolHighlight_Click(object? sender, RoutedEventArgs e) => SetTool(EditTool.Highlight);

    private void SetTool(EditTool tool)
    {
        _currentTool = tool;
        ToolSelectBtn.IsChecked = tool == EditTool.Select;
        ToolHighlightBtn.IsChecked = tool == EditTool.Highlight;
        StatusText.Text = $"Tool: {tool}";
    }

    // ── Annotation canvas pointer events ──────────────────────────────
    private void AnnotationCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_doc is null) return;
        if (_currentTool != EditTool.Highlight) return;
        if (!e.GetCurrentPoint(AnnotationCanvas).Properties.IsLeftButtonPressed) return;

        _isDrawing = true;
        _drawStart = e.GetPosition(AnnotationCanvas);
        _activePreview = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(HighlightColor.A, HighlightColor.R, HighlightColor.G, HighlightColor.B)),
            Width = 0,
            Height = 0,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_activePreview, _drawStart.X);
        Canvas.SetTop(_activePreview, _drawStart.Y);
        AnnotationCanvas.Children.Add(_activePreview);
        e.Pointer.Capture(AnnotationCanvas);
        e.Handled = true;
    }

    private void AnnotationCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDrawing || _activePreview is null) return;
        var pos = e.GetPosition(AnnotationCanvas);
        double x = System.Math.Min(pos.X, _drawStart.X);
        double y = System.Math.Min(pos.Y, _drawStart.Y);
        Canvas.SetLeft(_activePreview, x);
        Canvas.SetTop(_activePreview, y);
        _activePreview.Width = System.Math.Abs(pos.X - _drawStart.X);
        _activePreview.Height = System.Math.Abs(pos.Y - _drawStart.Y);
    }

    private void AnnotationCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDrawing || _activePreview is null) return;
        _isDrawing = false;
        e.Pointer.Capture(null);

        if (_activePreview.Width > 3 && _activePreview.Height > 3)
        {
            var rect = new RectD(
                Canvas.GetLeft(_activePreview),
                Canvas.GetTop(_activePreview),
                _activePreview.Width,
                _activePreview.Height);
            var ha = new HighlightAnnotation
            {
                PageIndex = _currentPageIndex,
                Bounds = rect,
                ColorR = HighlightColor.R,
                ColorG = HighlightColor.G,
                ColorB = HighlightColor.B,
                ColorA = HighlightColor.A
            };
            if (!_annotations.ContainsKey(_currentPageIndex))
                _annotations[_currentPageIndex] = new List<PageAnnotation>();
            _annotations[_currentPageIndex].Add(ha);
            StatusText.Text = $"Added highlight on page {_currentPageIndex + 1}";
        }
        else
        {
            AnnotationCanvas.Children.Remove(_activePreview);
        }
        _activePreview = null;
    }

    // ── Annotation rendering ──────────────────────────────────────────
    private void RenderAllAnnotations(int pageIndex)
    {
        AnnotationCanvas.Children.Clear();
        if (!_annotations.TryGetValue(pageIndex, out var annots)) return;

        foreach (var annot in annots)
        {
            switch (annot)
            {
                case HighlightAnnotation ha:
                    var rect = new Rectangle
                    {
                        Fill = new SolidColorBrush(Color.FromArgb(ha.ColorA, ha.ColorR, ha.ColorG, ha.ColorB)),
                        Width = ha.Bounds.Width,
                        Height = ha.Bounds.Height,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(rect, ha.Bounds.X);
                    Canvas.SetTop(rect, ha.Bounds.Y);
                    AnnotationCanvas.Children.Add(rect);
                    break;
                // Future: TextAnnotation, InkAnnotation, etc.
            }
        }
    }
}
