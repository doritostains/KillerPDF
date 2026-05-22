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
    private Avalonia.Controls.Shapes.Shape? _activePreview;
    private InkAnnotation? _activeInk;
    private Polyline? _activeInkVisual;

    // Selection state
    private PageAnnotation? _selectedAnnotation;
    private Border? _selectionBorder;

    // Annotation drag state
    private bool _isDraggingAnnotation;
    private Avalonia.Point _dragAnnotStart;
    private PointD _dragAnnotOrigPos;
    private RectD _dragAnnotOrigBounds;
    private List<PointD>? _dragInkOrigPoints;

    // Active text box being edited
    private TextBox? _activeTextBox;
    private Avalonia.Point _activeTextPos;

    // Default colors
    private static readonly ColorRgba HighlightColor = new(255, 255, 0, 80);
    private static readonly ColorRgba InkColor = new(255, 0, 0, 255);
    private static readonly ColorRgba TextColor = new(0, 0, 0, 255);
    private const double InkStrokeWidth = 2.0;
    private const double TextFontSize = 14.0;

    // Search state
    private IReadOnlyList<PdfSearchService.PageHits> _searchResults = Array.Empty<PdfSearchService.PageHits>();
    private int _searchResultPageCursor = -1; // index into _searchResults
    private readonly List<Rectangle> _searchHighlights = new();

    // Simple linear undo stack of annotation removals (snapshots of _annotations per change)
    private readonly Stack<Action> _undoStack = new();

    // Pending crop rectangle (in canvas coords). Apply commits to PDF MediaBox.
    private Rectangle? _cropPreviewRect;
    private RectD _pendingCropCanvas;

    // Pending drawn signature waiting for placement
    private List<List<PointD>>? _pendingSigStrokes;
    private double _pendingSigW, _pendingSigH;

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
        if (ctrl && e.Key == Key.F) { OpenSearchBar(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Z) { Undo_Click(null, new RoutedEventArgs()); e.Handled = true; return; }

        if (_doc is null) return;

        if (e.Key == Key.PageDown || e.Key == Key.Right || e.Key == Key.Down) { GoToPage(_currentPageIndex + 1); e.Handled = true; return; }
        if (e.Key == Key.PageUp || e.Key == Key.Left || e.Key == Key.Up) { GoToPage(_currentPageIndex - 1); e.Handled = true; return; }
        if (e.Key == Key.Home) { GoToPage(0); e.Handled = true; return; }
        if (e.Key == Key.End) { GoToPage(_pageCount - 1); e.Handled = true; return; }

        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { ZoomIn_Click(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { ZoomOut_Click(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D0) { FitWidth_Click(null, new RoutedEventArgs()); e.Handled = true; return; }

        if (e.Key == Key.Delete)
        {
            if (_selectedAnnotation is not null) DeleteSelectedAnnotation();
            else DeletePage_Click(null, new RoutedEventArgs());
            e.Handled = true; return;
        }
        if (e.Key == Key.Escape) { ClearSelection(); e.Handled = true; return; }

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
        PushDocSnapshot();
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
        PushDocSnapshot();
        PdfDocumentService.RotatePages(_doc, new[] { _currentPageIndex }, delta);
        PersistWorkingCopy();
        RenderCurrentPage();
        StatusText.Text = $"Rotated page {_currentPageIndex + 1} by {delta}°";
    }

    private void MoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null || _currentPageIndex <= 0) return;
        PushDocSnapshot();
        PdfDocumentService.MovePage(_doc, _currentPageIndex, _currentPageIndex - 1);
        PersistWorkingCopy();
        _currentPageIndex -= 1;
        RefreshPageList();
        RenderCurrentPage();
    }

    private void MoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null || _currentPageIndex >= _pageCount - 1) return;
        PushDocSnapshot();
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
        PushDocSnapshot();
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
            PushDocSnapshot();
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
    private void ToolDraw_Click(object? sender, RoutedEventArgs e) => SetTool(EditTool.Draw);
    private void ToolText_Click(object? sender, RoutedEventArgs e) => SetTool(EditTool.Text);
    private void ToolCrop_Click(object? sender, RoutedEventArgs e) => SetTool(EditTool.Crop);

    private void SetTool(EditTool tool)
    {
        CommitActiveTextBox();
        CropCancel_Click(null, new RoutedEventArgs());
        _currentTool = tool;
        ToolSelectBtn.IsChecked = tool == EditTool.Select;
        ToolHighlightBtn.IsChecked = tool == EditTool.Highlight;
        ToolDrawBtn.IsChecked = tool == EditTool.Draw;
        ToolTextBtn.IsChecked = tool == EditTool.Text;
        ToolCropBtn.IsChecked = tool == EditTool.Crop;
        ClearSelection();
        StatusText.Text = $"Tool: {tool}";
    }

    private void ClearSelection()
    {
        _selectedAnnotation = null;
        if (_selectionBorder is not null)
        {
            AnnotationCanvas.Children.Remove(_selectionBorder);
            _selectionBorder = null;
        }
    }

    // ── Annotation canvas pointer events ──────────────────────────────
    private void AnnotationCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_doc is null) return;
        if (!e.GetCurrentPoint(AnnotationCanvas).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(AnnotationCanvas);

        // Any pending text edit gets committed before starting another tool action.
        CommitActiveTextBox();

        switch (_currentTool)
        {
            case EditTool.Select:
                HandleSelectClick(pos);
                e.Handled = true;
                break;

            case EditTool.Text:
                PlaceTextBox(pos);
                e.Handled = true;
                break;

            case EditTool.Signature when _pendingSigStrokes is not null:
                PlacePendingSignature(pos);
                e.Handled = true;
                break;

            case EditTool.Highlight:
                _isDrawing = true;
                _drawStart = pos;
                var rect = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(HighlightColor.A, HighlightColor.R, HighlightColor.G, HighlightColor.B)),
                    Width = 0, Height = 0,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, pos.X);
                Canvas.SetTop(rect, pos.Y);
                AnnotationCanvas.Children.Add(rect);
                _activePreview = rect;
                e.Pointer.Capture(AnnotationCanvas);
                e.Handled = true;
                break;

            case EditTool.Draw:
                _isDrawing = true;
                _drawStart = pos;
                _activeInk = new InkAnnotation
                {
                    PageIndex = _currentPageIndex,
                    StrokeWidth = InkStrokeWidth,
                    ColorR = InkColor.R, ColorG = InkColor.G, ColorB = InkColor.B, ColorA = InkColor.A
                };
                _activeInk.Points.Add(new PointD(pos.X, pos.Y));
                _activeInkVisual = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(InkColor.A, InkColor.R, InkColor.G, InkColor.B)),
                    StrokeThickness = InkStrokeWidth,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                _activeInkVisual.Points.Add(pos);
                AnnotationCanvas.Children.Add(_activeInkVisual);
                e.Pointer.Capture(AnnotationCanvas);
                e.Handled = true;
                break;

            case EditTool.Crop:
                _isDrawing = true;
                _drawStart = pos;
                CropCancel_Click(null, new RoutedEventArgs());
                var cropRect = new Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 0x1e, 0xa5, 0x4c)),
                    StrokeThickness = 2,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 5, 3 },
                    Fill = new SolidColorBrush(Color.FromArgb(20, 0x1e, 0xa5, 0x4c)),
                    Width = 0, Height = 0,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(cropRect, pos.X);
                Canvas.SetTop(cropRect, pos.Y);
                AnnotationCanvas.Children.Add(cropRect);
                _activePreview = cropRect;
                e.Pointer.Capture(AnnotationCanvas);
                e.Handled = true;
                break;
        }
    }

    private void HandleSelectClick(Avalonia.Point pos)
    {
        // If the click is on the already-selected annotation, start a drag without
        // re-selecting. Otherwise: clear, hit-test, select new if any.
        if (_selectedAnnotation is not null)
        {
            var existingBounds = GetAnnotationBounds(_selectedAnnotation);
            if (existingBounds.Contains(pos.X, pos.Y))
            {
                StartAnnotationDrag(pos);
                return;
            }
        }

        ClearSelection();
        if (!_annotations.TryGetValue(_currentPageIndex, out var annots)) return;

        for (int i = annots.Count - 1; i >= 0; i--)
        {
            var bounds = GetAnnotationBounds(annots[i]);
            if (bounds.Contains(pos.X, pos.Y))
            {
                SelectAnnotation(annots[i], bounds);
                StartAnnotationDrag(pos);
                return;
            }
        }
    }

    private void StartAnnotationDrag(Avalonia.Point pos)
    {
        if (_selectedAnnotation is null) return;
        _isDraggingAnnotation = true;
        _dragAnnotStart = pos;
        switch (_selectedAnnotation)
        {
            case HighlightAnnotation ha:
                _dragAnnotOrigBounds = ha.Bounds;
                break;
            case TextAnnotation ta:
                _dragAnnotOrigPos = ta.Position;
                break;
            case InkAnnotation ia:
                // Capture starting points so drag delta applies relative to the original
                _dragInkOrigPoints = ia.Points.Select(p => new PointD(p.X, p.Y)).ToList();
                break;
            case PlacedAnnotation pa:
                _dragAnnotOrigPos = pa.Position;
                break;
        }
    }

    private void UpdateAnnotationDrag(Avalonia.Point pos)
    {
        if (!_isDraggingAnnotation || _selectedAnnotation is null) return;
        double dx = pos.X - _dragAnnotStart.X;
        double dy = pos.Y - _dragAnnotStart.Y;

        switch (_selectedAnnotation)
        {
            case HighlightAnnotation ha:
                ha.Bounds = new RectD(
                    _dragAnnotOrigBounds.X + dx,
                    _dragAnnotOrigBounds.Y + dy,
                    _dragAnnotOrigBounds.Width,
                    _dragAnnotOrigBounds.Height);
                break;
            case TextAnnotation ta:
                ta.Position = new PointD(_dragAnnotOrigPos.X + dx, _dragAnnotOrigPos.Y + dy);
                break;
            case InkAnnotation ia when _dragInkOrigPoints is not null:
                ia.Points.Clear();
                foreach (var op in _dragInkOrigPoints)
                    ia.Points.Add(new PointD(op.X + dx, op.Y + dy));
                break;
            case PlacedAnnotation pa:
                pa.Position = new PointD(_dragAnnotOrigPos.X + dx, _dragAnnotOrigPos.Y + dy);
                break;
        }

        RenderAllAnnotations(_currentPageIndex);
        // Re-show selection border at new position
        var newBounds = GetAnnotationBounds(_selectedAnnotation);
        if (_selectionBorder is not null) AnnotationCanvas.Children.Remove(_selectionBorder);
        SelectAnnotation(_selectedAnnotation, newBounds);
    }

    private void EndAnnotationDrag()
    {
        _isDraggingAnnotation = false;
        _dragInkOrigPoints = null;
    }

    private static RectD GetAnnotationBounds(PageAnnotation annot) => annot switch
    {
        HighlightAnnotation ha => ha.Bounds,
        InkAnnotation ia when ia.Points.Count > 0 => new RectD(
            ia.Points.Min(p => p.X),
            ia.Points.Min(p => p.Y),
            System.Math.Max(4, ia.Points.Max(p => p.X) - ia.Points.Min(p => p.X)),
            System.Math.Max(4, ia.Points.Max(p => p.Y) - ia.Points.Min(p => p.Y))),
        TextAnnotation ta => new RectD(ta.Position.X, ta.Position.Y,
            System.Math.Max(60, ta.Content.Length * ta.FontSize * 0.6),
            ta.FontSize * 1.4),
        ImageAnnotation iaa => new RectD(iaa.Position.X, iaa.Position.Y,
            iaa.SourceWidth * iaa.Scale, iaa.SourceHeight * iaa.Scale),
        SignatureAnnotation sa => new RectD(sa.Position.X, sa.Position.Y,
            sa.SourceWidth * sa.Scale, sa.SourceHeight * sa.Scale),
        _ => new RectD(0, 0, 0, 0)
    };

    private void SelectAnnotation(PageAnnotation annot, RectD bounds)
    {
        _selectedAnnotation = annot;
        _selectionBorder = new Border
        {
            Width = bounds.Width + 8,
            Height = bounds.Height + 8,
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x1e, 0xa5, 0x4c)),
            BorderThickness = new Avalonia.Thickness(2),
            CornerRadius = new Avalonia.CornerRadius(2),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_selectionBorder, bounds.X - 4);
        Canvas.SetTop(_selectionBorder, bounds.Y - 4);
        AnnotationCanvas.Children.Add(_selectionBorder);
        StatusText.Text = $"Selected — press Delete to remove";
    }

    private void DeleteSelectedAnnotation()
    {
        if (_selectedAnnotation is null) return;
        if (_annotations.TryGetValue(_currentPageIndex, out var annots))
        {
            annots.Remove(_selectedAnnotation);
        }
        ClearSelection();
        RenderAllAnnotations(_currentPageIndex);
        StatusText.Text = "Annotation deleted";
    }

    private void AnnotationCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(AnnotationCanvas);
        if (_isDraggingAnnotation)
        {
            UpdateAnnotationDrag(pos);
            return;
        }
        if (!_isDrawing) return;

        if (_activePreview is Rectangle hl)
        {
            double x = System.Math.Min(pos.X, _drawStart.X);
            double y = System.Math.Min(pos.Y, _drawStart.Y);
            Canvas.SetLeft(hl, x);
            Canvas.SetTop(hl, y);
            hl.Width = System.Math.Abs(pos.X - _drawStart.X);
            hl.Height = System.Math.Abs(pos.Y - _drawStart.Y);
        }
        else if (_activeInk is not null && _activeInkVisual is not null)
        {
            _activeInk.Points.Add(new PointD(pos.X, pos.Y));
            _activeInkVisual.Points.Add(pos);
        }
    }

    private void AnnotationCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingAnnotation)
        {
            EndAnnotationDrag();
            e.Pointer.Capture(null);
            return;
        }
        if (!_isDrawing) return;
        _isDrawing = false;
        e.Pointer.Capture(null);

        if (_activePreview is Rectangle hl)
        {
            if (_currentTool == EditTool.Crop)
            {
                if (hl.Width > 10 && hl.Height > 10)
                {
                    _pendingCropCanvas = new RectD(Canvas.GetLeft(hl), Canvas.GetTop(hl), hl.Width, hl.Height);
                    _cropPreviewRect = hl;
                    CropBar.IsVisible = true;
                    StatusText.Text = "Crop pending — click Apply to commit, or X to cancel";
                }
                else
                {
                    AnnotationCanvas.Children.Remove(hl);
                }
            }
            else if (hl.Width > 3 && hl.Height > 3)
            {
                var rect = new RectD(Canvas.GetLeft(hl), Canvas.GetTop(hl), hl.Width, hl.Height);
                AddAnnotation(new HighlightAnnotation
                {
                    PageIndex = _currentPageIndex,
                    Bounds = rect,
                    ColorR = HighlightColor.R, ColorG = HighlightColor.G, ColorB = HighlightColor.B, ColorA = HighlightColor.A
                }, "Added highlight");
            }
            else AnnotationCanvas.Children.Remove(hl);
            _activePreview = null;
        }
        else if (_activeInk is not null)
        {
            if (_activeInk.Points.Count > 2)
            {
                AddAnnotation(_activeInk, "Added drawing");
            }
            else if (_activeInkVisual is not null)
            {
                AnnotationCanvas.Children.Remove(_activeInkVisual);
            }
            _activeInk = null;
            _activeInkVisual = null;
        }
    }

    private void AddAnnotation(PageAnnotation annot, string statusVerb)
    {
        if (!_annotations.ContainsKey(_currentPageIndex))
            _annotations[_currentPageIndex] = new List<PageAnnotation>();
        _annotations[_currentPageIndex].Add(annot);
        int page = _currentPageIndex;
        _undoStack.Push(() =>
        {
            if (_annotations.TryGetValue(page, out var list))
                list.Remove(annot);
        });
        StatusText.Text = $"{statusVerb} on page {_currentPageIndex + 1}";
    }

    // ── Undo ──────────────────────────────────────────────────────────
    private void Undo_Click(object? sender, RoutedEventArgs e)
    {
        if (_undoStack.Count == 0) { StatusText.Text = "Nothing to undo"; return; }
        var action = _undoStack.Pop();
        action();
        ClearSelection();
        StatusText.Text = "Undid last edit";
    }

    /// <summary>
    /// Captures the current document as a byte[] snapshot and pushes a restore action
    /// onto the undo stack. Call BEFORE any page-level mutation so the user can rewind.
    /// </summary>
    private void PushDocSnapshot()
    {
        if (_doc is null || _workingPath is null) return;
        using var ms = new System.IO.MemoryStream();
        _doc.Save(ms);
        var bytes = ms.ToArray();
        int beforePageIdx = _currentPageIndex;
        _undoStack.Push(() =>
        {
            try
            {
                if (_workingPath is null) return;
                System.IO.File.WriteAllBytes(_workingPath, bytes);
                _doc?.Close();
                _doc = PdfReader.Open(_workingPath, PdfDocumentOpenMode.Modify);
                _pageCount = _doc.PageCount;
                _annotations.Clear();   // simple approach: annotations on undo'd page state are dropped
                _renderDims.Clear();
                _currentPageIndex = System.Math.Min(beforePageIdx, _pageCount - 1);
                RefreshPageList();
                RenderCurrentPage();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Undo failed: {ex.Message}";
            }
        });
    }

    // ── Search ────────────────────────────────────────────────────────
    private void SearchOpen_Click(object? sender, RoutedEventArgs e) => OpenSearchBar();

    private void OpenSearchBar()
    {
        SearchBar.IsVisible = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void SearchClose_Click(object? sender, RoutedEventArgs e) => CloseSearchBar();

    private void CloseSearchBar()
    {
        SearchBar.IsVisible = false;
        ClearSearchHighlights();
        _searchResults = Array.Empty<PdfSearchService.PageHits>();
        _searchResultPageCursor = -1;
        SearchStatus.Text = "";
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) SearchPrev_Click(null, new RoutedEventArgs());
            else RunSearch(SearchBox.Text ?? "");
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearchBar();
            e.Handled = true;
        }
    }

    private void RunSearch(string query)
    {
        ClearSearchHighlights();
        if (_workingPath is null || string.IsNullOrWhiteSpace(query))
        {
            _searchResults = Array.Empty<PdfSearchService.PageHits>();
            SearchStatus.Text = "";
            return;
        }
        try
        {
            _searchResults = PdfSearchService.SearchDocument(_workingPath, query);
            int totalHits = _searchResults.Sum(ph => ph.Hits.Count);
            if (totalHits == 0)
            {
                SearchStatus.Text = "No matches";
                _searchResultPageCursor = -1;
                return;
            }
            SearchStatus.Text = totalHits == 1
                ? $"1 match on {_searchResults.Count} page"
                : $"{totalHits} matches on {_searchResults.Count} page{(_searchResults.Count != 1 ? "s" : "")}";

            // Jump to the first result at or after the current page.
            int startIdx = _searchResults.ToList().FindIndex(ph => ph.PageIndex >= _currentPageIndex);
            _searchResultPageCursor = startIdx >= 0 ? startIdx : 0;
            JumpToCurrentSearchPage();
        }
        catch (Exception ex)
        {
            SearchStatus.Text = $"Search error: {ex.Message}";
        }
    }

    private void SearchNext_Click(object? sender, RoutedEventArgs e)
    {
        if (_searchResults.Count == 0) return;
        _searchResultPageCursor = (_searchResultPageCursor + 1) % _searchResults.Count;
        JumpToCurrentSearchPage();
    }

    private void SearchPrev_Click(object? sender, RoutedEventArgs e)
    {
        if (_searchResults.Count == 0) return;
        _searchResultPageCursor = (_searchResultPageCursor - 1 + _searchResults.Count) % _searchResults.Count;
        JumpToCurrentSearchPage();
    }

    private void JumpToCurrentSearchPage()
    {
        if (_searchResultPageCursor < 0 || _searchResultPageCursor >= _searchResults.Count) return;
        var ph = _searchResults[_searchResultPageCursor];
        if (ph.PageIndex != _currentPageIndex)
        {
            GoToPage(ph.PageIndex);
            // PageList SelectionChanged calls RenderCurrentPage; HighlightSearchHits has to be
            // called after that, deferred to layout.
            Avalonia.Threading.Dispatcher.UIThread.Post(HighlightSearchHits,
                Avalonia.Threading.DispatcherPriority.Background);
        }
        else
        {
            HighlightSearchHits();
        }
    }

    private void HighlightSearchHits()
    {
        ClearSearchHighlights();
        if (_workingPath is null || !_renderDims.TryGetValue(_currentPageIndex, out var dims)) return;
        var ph = _searchResults.FirstOrDefault(x => x.PageIndex == _currentPageIndex);
        if (ph.Hits is null || ph.Hits.Count == 0) return;

        // Scale PDF user-space → canvas. PdfPig coords are bottom-left origin.
        try
        {
            using var pigDoc = UglyToad.PdfPig.PdfDocument.Open(_workingPath);
            var page = pigDoc.GetPage(_currentPageIndex + 1);
            double sx = dims.w / page.Width;
            double sy = dims.h / page.Height;
            foreach (var hit in ph.Hits)
            {
                double x = hit.Left * sx;
                double y = (page.Height - hit.Top) * sy;
                double w = (hit.Right - hit.Left) * sx;
                double h = (hit.Top - hit.Bottom) * sy;
                var rect = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(80, 0x1e, 0xa5, 0x4c)),
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 0x1e, 0xa5, 0x4c)),
                    StrokeThickness = 1,
                    Width = w,
                    Height = h,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                AnnotationCanvas.Children.Add(rect);
                _searchHighlights.Add(rect);
            }
        }
        catch
        {
            // best-effort highlight; no status spam
        }
    }

    private void ClearSearchHighlights()
    {
        foreach (var r in _searchHighlights) AnnotationCanvas.Children.Remove(r);
        _searchHighlights.Clear();
    }

    // ── Text tool ─────────────────────────────────────────────────────
    private void PlaceTextBox(Avalonia.Point pos)
    {
        var tb = new TextBox
        {
            FontSize = TextFontSize,
            FontFamily = new FontFamily("Inter, sans-serif"),
            Foreground = new SolidColorBrush(Color.FromArgb(TextColor.A, TextColor.R, TextColor.G, TextColor.B)),
            Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x1e, 0xa5, 0x4c)),
            BorderThickness = new Avalonia.Thickness(2),
            Padding = new Avalonia.Thickness(2, 0),
            MinWidth = 100,
            Text = ""
        };
        Canvas.SetLeft(tb, pos.X);
        Canvas.SetTop(tb, pos.Y);
        AnnotationCanvas.Children.Add(tb);
        _activeTextBox = tb;
        _activeTextPos = pos;
        tb.KeyDown += TextBox_KeyDown;
        tb.LostFocus += (_, _) => CommitActiveTextBox();
        tb.Focus();
    }

    private void TextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelActiveTextBox();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            CommitActiveTextBox();
            e.Handled = true;
        }
        // Shift+Enter lets the user add a newline (handled by the default TextBox behavior).
    }

    private void CancelActiveTextBox()
    {
        if (_activeTextBox is null) return;
        AnnotationCanvas.Children.Remove(_activeTextBox);
        _activeTextBox = null;
    }

    private void CommitActiveTextBox()
    {
        if (_activeTextBox is null) return;
        var tb = _activeTextBox;
        var content = tb.Text ?? "";
        AnnotationCanvas.Children.Remove(tb);
        _activeTextBox = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            AddAnnotation(new TextAnnotation
            {
                PageIndex = _currentPageIndex,
                Position = new PointD(_activeTextPos.X, _activeTextPos.Y),
                Content = content,
                FontSize = TextFontSize,
                ColorR = TextColor.R, ColorG = TextColor.G, ColorB = TextColor.B, ColorA = TextColor.A
            }, "Added text");
            RenderAllAnnotations(_currentPageIndex);
        }
    }

    private void PlacePendingSignature(Avalonia.Point pos)
    {
        if (_pendingSigStrokes is null) return;
        const double scale = 0.5;
        var sa = new SignatureAnnotation
        {
            PageIndex = _currentPageIndex,
            Position = new PointD(pos.X, pos.Y),
            Scale = scale,
            SourceWidth = _pendingSigW,
            SourceHeight = _pendingSigH,
            ImageData = null,  // drawn (not image-based)
            Strokes = _pendingSigStrokes.Select(s => s.Select(p => new PointD(p.X, p.Y)).ToList()).ToList()
        };
        AddAnnotation(sa, "Placed signature");
        _pendingSigStrokes = null;
        SetTool(EditTool.Select);
        RenderAllAnnotations(_currentPageIndex);
    }

    // ── Image tool ────────────────────────────────────────────────────
    private async void InsertImage_Click(object? sender, RoutedEventArgs e)
        => await InsertImageOrSignature(asSignature: false);

    private async void InsertSignature_Click(object? sender, RoutedEventArgs e)
        => await InsertImageOrSignature(asSignature: true);

    private async void DrawSignature_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null) { StatusText.Text = "Open a PDF first."; return; }
        var dlg = new SignatureCreatorWindow();
        await dlg.ShowDialog(this);
        if (dlg.Strokes is null || dlg.Strokes.Count == 0) return;

        _pendingSigStrokes = dlg.Strokes;
        _pendingSigW = dlg.CanvasWidth;
        _pendingSigH = dlg.CanvasHeight;
        SetTool(EditTool.Signature);
        StatusText.Text = "Click on the page to place the signature";
    }

    private async System.Threading.Tasks.Task InsertImageOrSignature(bool asSignature)
    {
        if (_doc is null) { StatusText.Text = "Open a PDF first."; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = asSignature ? "Import Signature Image" : "Insert Image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image files")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tiff", "*.tif" }
                }
            }
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) { StatusText.Text = "Could not resolve image path."; return; }
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            double srcW = 400, srcH = 300;
            using (var ms = new System.IO.MemoryStream(bytes))
            {
                try
                {
                    var bmp = new Bitmap(ms);
                    srcW = bmp.PixelSize.Width;
                    srcH = bmp.PixelSize.Height;
                }
                catch { }
            }
            const double MaxCanvasDim = 250;
            double scale = System.Math.Min(1.0, System.Math.Min(MaxCanvasDim / srcW, MaxCanvasDim / srcH));
            double cx = (AnnotationCanvas.Width - srcW * scale) / 2;
            double cy = (AnnotationCanvas.Height - srcH * scale) / 2;

            PlacedAnnotation annot = asSignature
                ? new SignatureAnnotation
                {
                    PageIndex = _currentPageIndex,
                    Position = new PointD(cx, cy),
                    Scale = scale,
                    SourceWidth = srcW,
                    SourceHeight = srcH,
                    ImageData = Convert.ToBase64String(bytes)
                }
                : new ImageAnnotation
                {
                    PageIndex = _currentPageIndex,
                    Position = new PointD(cx, cy),
                    Scale = scale,
                    SourceWidth = srcW,
                    SourceHeight = srcH,
                    ImageData = Convert.ToBase64String(bytes)
                };
            AddAnnotation(annot, asSignature ? "Inserted signature" : "Inserted image");
            RenderAllAnnotations(_currentPageIndex);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Insert failed: {ex.Message}";
        }
    }

    // ── Crop ──────────────────────────────────────────────────────────
    private void CropCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_cropPreviewRect is not null)
        {
            AnnotationCanvas.Children.Remove(_cropPreviewRect);
            _cropPreviewRect = null;
        }
        CropBar.IsVisible = false;
    }

    private void CropApply_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null || _cropPreviewRect is null || !_renderDims.TryGetValue(_currentPageIndex, out var dims))
        {
            CropCancel_Click(null, new RoutedEventArgs());
            return;
        }
        try
        {
            PushDocSnapshot();
            var page = _doc.Pages[_currentPageIndex];
            double sx = page.Width.Point / dims.w;
            double sy = page.Height.Point / dims.h;
            double pdfLeft = page.MediaBox.X1 + _pendingCropCanvas.X * sx;
            double pdfRight = page.MediaBox.X1 + (_pendingCropCanvas.X + _pendingCropCanvas.Width) * sx;
            double pdfTop = page.MediaBox.Y2 - _pendingCropCanvas.Y * sy;
            double pdfBottom = page.MediaBox.Y2 - (_pendingCropCanvas.Y + _pendingCropCanvas.Height) * sy;
            page.MediaBox = new PdfSharpCore.Pdf.PdfRectangle(
                new PdfSharpCore.Drawing.XPoint(pdfLeft, pdfBottom),
                new PdfSharpCore.Drawing.XPoint(pdfRight, pdfTop));
            PersistWorkingCopy();
            CropCancel_Click(null, new RoutedEventArgs());
            RenderCurrentPage();
            StatusText.Text = $"Cropped page {_currentPageIndex + 1}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Crop failed: {ex.Message}";
        }
    }

    // ── Extract current page ──────────────────────────────────────────
    private async void ExtractPage_Click(object? sender, RoutedEventArgs e)
    {
        if (_doc is null || _workingPath is null) { StatusText.Text = "Open a PDF first."; return; }
        var suggested = (_originalPath is not null
            ? System.IO.Path.GetFileNameWithoutExtension(_originalPath)
            : "document") + $"-page{_currentPageIndex + 1}";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Extract current page",
            SuggestedFileName = suggested,
            DefaultExtension = "pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } } }
        });
        if (file is null) return;
        var target = file.TryGetLocalPath();
        if (target is null) return;
        try
        {
            PdfDocumentService.ExtractPages(_workingPath, new[] { _currentPageIndex }, target);
            StatusText.Text = $"Extracted page {_currentPageIndex + 1} to {System.IO.Path.GetFileName(target)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Extract failed: {ex.Message}";
        }
    }

    private void RenderImageBitmap(string base64, PointD position, double srcW, double srcH, double scale)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var ms = new System.IO.MemoryStream(bytes);
            var img = new Image
            {
                Source = new Bitmap(ms),
                Width = srcW * scale,
                Height = srcH * scale,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(img, position.X);
            Canvas.SetTop(img, position.Y);
            AnnotationCanvas.Children.Add(img);
        }
        catch { /* skip broken image */ }
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

                case InkAnnotation ia when ia.Points.Count >= 2:
                    var poly = new Polyline
                    {
                        Stroke = new SolidColorBrush(Color.FromArgb(ia.ColorA, ia.ColorR, ia.ColorG, ia.ColorB)),
                        StrokeThickness = ia.StrokeWidth,
                        StrokeLineCap = PenLineCap.Round,
                        StrokeJoin = PenLineJoin.Round,
                        IsHitTestVisible = false
                    };
                    foreach (var p in ia.Points) poly.Points.Add(new Avalonia.Point(p.X, p.Y));
                    AnnotationCanvas.Children.Add(poly);
                    break;

                case TextAnnotation ta:
                    var tb = new TextBlock
                    {
                        Text = ta.Content,
                        FontSize = ta.FontSize,
                        Foreground = new SolidColorBrush(Color.FromArgb(ta.ColorA, ta.ColorR, ta.ColorG, ta.ColorB)),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(tb, ta.Position.X);
                    Canvas.SetTop(tb, ta.Position.Y);
                    AnnotationCanvas.Children.Add(tb);
                    break;

                case ImageAnnotation iaa:
                    RenderImageBitmap(iaa.ImageData, iaa.Position, iaa.SourceWidth, iaa.SourceHeight, iaa.Scale);
                    break;

                case SignatureAnnotation sa when sa.ImageData is not null:
                    RenderImageBitmap(sa.ImageData, sa.Position, sa.SourceWidth, sa.SourceHeight, sa.Scale);
                    break;

                case SignatureAnnotation sa when sa.Strokes.Count > 0:
                    // Drawn signature: render each stroke as a polyline at scaled position
                    foreach (var stroke in sa.Strokes)
                    {
                        if (stroke.Count < 2) continue;
                        var sigPoly = new Polyline
                        {
                            Stroke = new SolidColorBrush(Colors.Black),
                            StrokeThickness = 2 * sa.Scale,
                            StrokeLineCap = PenLineCap.Round,
                            StrokeJoin = PenLineJoin.Round,
                            IsHitTestVisible = false
                        };
                        foreach (var p in stroke)
                            sigPoly.Points.Add(new Avalonia.Point(sa.Position.X + p.X * sa.Scale, sa.Position.Y + p.Y * sa.Scale));
                        AnnotationCanvas.Children.Add(sigPoly);
                    }
                    break;
            }
        }
    }
}
