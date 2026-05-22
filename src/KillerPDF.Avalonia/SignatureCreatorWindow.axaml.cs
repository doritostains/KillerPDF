using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace KillerPDF;

public partial class SignatureCreatorWindow : Window
{
    /// <summary>The strokes drawn by the user, or null if cancelled. List of strokes; each stroke is a list of points.</summary>
    public List<List<PointD>>? Strokes { get; private set; }
    public double CanvasWidth { get; private set; } = 400;
    public double CanvasHeight { get; private set; } = 150;

    private bool _isDrawing;
    private List<PointD>? _currentStroke;
    private Polyline? _currentVisual;
    private readonly List<List<PointD>> _allStrokes = new();

    public SignatureCreatorWindow() => InitializeComponent();

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void DrawCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(DrawCanvas).Properties.IsLeftButtonPressed) return;
        _isDrawing = true;
        var pos = e.GetPosition(DrawCanvas);
        _currentStroke = new List<PointD> { new(pos.X, pos.Y) };
        _currentVisual = new Polyline
        {
            Stroke = new SolidColorBrush(Colors.Black),
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        _currentVisual.Points.Add(pos);
        DrawCanvas.Children.Add(_currentVisual);
        e.Pointer.Capture(DrawCanvas);
    }

    private void DrawCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDrawing || _currentStroke is null || _currentVisual is null) return;
        var pos = e.GetPosition(DrawCanvas);
        _currentStroke.Add(new PointD(pos.X, pos.Y));
        _currentVisual.Points.Add(pos);
    }

    private void DrawCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDrawing) return;
        _isDrawing = false;
        e.Pointer.Capture(null);
        if (_currentStroke is { Count: > 1 }) _allStrokes.Add(_currentStroke);
        _currentStroke = null;
        _currentVisual = null;
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        _allStrokes.Clear();
        DrawCanvas.Children.Clear();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_allStrokes.Count == 0 || _allStrokes.All(s => s.Count < 2))
        {
            // Nothing meaningful drawn — treat as cancel
            Close();
            return;
        }
        Strokes = _allStrokes;
        CanvasWidth = DrawCanvas.Bounds.Width > 0 ? DrawCanvas.Bounds.Width : 400;
        CanvasHeight = DrawCanvas.Bounds.Height > 0 ? DrawCanvas.Bounds.Height : 150;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Strokes = null;
        Close();
    }
}
