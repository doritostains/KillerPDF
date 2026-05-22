namespace KillerPDF;

public struct PointD
{
    public double X { get; set; }
    public double Y { get; set; }

    public PointD(double x, double y) { X = x; Y = y; }

    public override string ToString() => $"({X}, {Y})";
}

public struct RectD
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public RectD(double x, double y, double width, double height)
    {
        X = x; Y = y; Width = width; Height = height;
    }

    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(PointD p) =>
        p.X >= X && p.X <= X + Width && p.Y >= Y && p.Y <= Y + Height;

    public bool Contains(double px, double py) =>
        px >= X && px <= X + Width && py >= Y && py <= Y + Height;
}

public struct ColorRgba
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte A { get; set; }

    public ColorRgba(byte r, byte g, byte b, byte a) { R = r; G = g; B = b; A = a; }

    public static ColorRgba FromArgb(byte a, byte r, byte g, byte b) => new(r, g, b, a);
    public static ColorRgba FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);
}
