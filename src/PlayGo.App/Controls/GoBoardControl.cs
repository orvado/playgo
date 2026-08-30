using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PlayGo.Core;

namespace PlayGo.App.Controls;

/// <summary>
/// A custom-rendered Go board: wooden surface, grid lines, star points,
/// coordinate labels, gradient-shaded stones, last-move marker, dead-stone
/// marks (scoring mode) and a ghost stone for the hovered intersection.
/// </summary>
public sealed class GoBoardControl : FrameworkElement
{
    private static readonly Brush WoodBrush = CreateWoodBrush();
    private static readonly Brush WoodBorderBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x4E, 0x23));
    private static readonly Pen WoodBorderPen = new(WoodBorderBrush, 2.0);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(0x33, 0x23, 0x0E)), 1.0);
    private static readonly Pen OuterGridPen = new(new SolidColorBrush(Color.FromRgb(0x2A, 0x1B, 0x09)), 2.4);
    private static readonly Brush StarPointBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x1B, 0x09));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x4C, 0x22));
    private static readonly Brush KoMarkerBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x45, 0x2F));
    private static readonly Brush DeadXBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x45, 0x2F));
    private static readonly Pen DeadXPen = new(DeadXBrush, 2.4);

    private static readonly Brush BlackStone = CreateStoneBrush(Color.FromRgb(0x6F, 0x6F, 0x72), Color.FromRgb(0x08, 0x08, 0x0A));
    private static readonly Pen BlackStoneOutline = new(new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)), 1.0);
    private static readonly Brush WhiteStone = CreateStoneBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xC2, 0xC2, 0xC2));
    private static readonly Pen WhiteStoneOutline = new(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)), 1.0);

    // Ghost stones for the hovered intersection. Kept pre-built: the old code
    // cloned a brush on every mouse move, which allocated on every frame.
    private static readonly Brush BlackGhost = CreateStoneBrush(Color.FromRgb(0x6F, 0x6F, 0x72), Color.FromRgb(0x08, 0x08, 0x0A), 0.35);
    private static readonly Brush WhiteGhost = CreateStoneBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xC2, 0xC2, 0xC2), 0.35);

    // Territory shading shown while marking dead stones.
    private static readonly Brush BlackTerritoryBrush = CreateFrozenBrush(Color.FromArgb(0x77, 0x12, 0x14, 0x1A));
    private static readonly Brush WhiteTerritoryBrush = CreateFrozenBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

    private static readonly Pen WoodGrainPen = CreateFrozenPen(Color.FromArgb(0x20, 0x00, 0x00, 0x00), 1.0);
    private static readonly (Point From, Point To)[] WoodGrainLines = CreateWoodGrain();

    private GoBoard? _board;
    private GoPoint? _hover;
    private bool _interactive;

    public GoBoardControl()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        MouseMove += (_, _) => UpdateHover();
        MouseLeave += (_, _) =>
        {
            _hover = null;
            InvalidateVisual();
        };
        MouseLeftButtonDown += OnMouseLeftDown;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public GoBoard? Board
    {
        get => _board;
        set { _board = value; InvalidateVisual(); }
    }

    public GoPoint? LastMove { get; set; }

    public StoneColor CurrentPlayer { get; set; } = StoneColor.Black;

    public bool Interactive
    {
        get => _interactive;
        set { _interactive = value; InvalidateVisual(); }
    }

    public bool ScoringMode { get; set; }

    public IReadOnlyCollection<GoPoint>? DeadStones { get; set; }

    /// <summary>
    /// Territory ownership used to shade empty points during scoring. Set to
    /// null outside the scoring phase.
    /// </summary>
    public TerritoryMap? Territory { get; set; }

    public bool ShowCoordinates { get; set; } = true;

    /// <summary>Raised when the user clicks an empty intersection during play.</summary>
    public event EventHandler<GoPoint>? MoveRequested;

    /// <summary>Raised when the user clicks a stone while marking dead stones.</summary>
    public event EventHandler<GoPoint>? StoneMarked;

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_board is null || ActualWidth < 40 || ActualHeight < 40) return;

        var (cell, ox, oy, boardLen) = ComputeLayout();

        DrawWood(dc);
        DrawGrid(dc, cell, ox, oy, boardLen);
        DrawStarPoints(dc, cell, ox, oy);
        if (ShowCoordinates)
            DrawCoordinates(dc, cell, ox, oy);
        DrawTerritory(dc, cell, ox, oy);
        DrawStones(dc, cell, ox, oy);
        DrawHover(dc, cell, ox, oy);
    }

    private (double cell, double ox, double oy, double boardLen) ComputeLayout()
    {
        double margin = Math.Min(ActualWidth, ActualHeight) * 0.06;
        double avail = Math.Min(ActualWidth, ActualHeight) - margin * 2;
        double cell = avail / (_board!.Size - 1);
        double boardLen = cell * (_board.Size - 1);
        double ox = (ActualWidth - boardLen) / 2.0;
        double oy = (ActualHeight - boardLen) / 2.0;
        return (cell, ox, oy, boardLen);
    }

    private void DrawWood(DrawingContext dc)
    {
        var rect = new Rect(2, 2, ActualWidth - 4, ActualHeight - 4);
        dc.DrawRoundedRectangle(WoodBrush, WoodBorderPen, rect, 10, 10);

        // Subtle wood grain. The streaks are generated once and stretched to the
        // current size, so nothing is allocated per frame.
        foreach (var (from, to) in WoodGrainLines)
        {
            dc.DrawLine(WoodGrainPen,
                new Point(from.X * ActualWidth, from.Y * ActualHeight),
                new Point(to.X * ActualWidth, to.Y * ActualHeight));
        }
    }

    private static (Point From, Point To)[] CreateWoodGrain()
    {
        var rng = new Random(7);
        var lines = new (Point, Point)[14];
        for (int i = 0; i < lines.Length; i++)
        {
            double y = rng.NextDouble();
            lines[i] = (new Point(0, y), new Point(1, y + (rng.NextDouble() * 6 - 3) / 800.0));
        }
        return lines;
    }

    private void DrawGrid(DrawingContext dc, double cell, double ox, double oy, double boardLen)
    {
        int n = _board!.Size;
        for (int i = 0; i < n; i++)
        {
            bool outer = i == 0 || i == n - 1;
            var pen = outer ? OuterGridPen : GridPen;
            double t = i * cell;
            dc.DrawLine(pen, new Point(ox + t, oy), new Point(ox + t, oy + boardLen));
            dc.DrawLine(pen, new Point(ox, oy + t), new Point(ox + boardLen, oy + t));
        }
    }

    private void DrawStarPoints(DrawingContext dc, double cell, double ox, double oy)
    {
        double r = Math.Max(1.6, cell * 0.12);
        foreach (var (sr, sc) in StarPoints(_board!.Size))
            dc.DrawEllipse(StarPointBrush, null, new Point(ox + sc * cell, oy + sr * cell), r, r);
    }

    private static (int, int)[] StarPoints(int size) => size switch
    {
        9 => new[] { (2, 2), (2, 6), (6, 2), (6, 6), (4, 4) },
        13 => new[] { (3, 3), (3, 9), (9, 3), (9, 9), (6, 6), (3, 6), (6, 3), (6, 9), (9, 6) },
        _ => new[] { (3, 3), (3, 9), (3, 15), (9, 3), (9, 9), (9, 15), (15, 3), (15, 9), (15, 15) },
    };

    private void DrawCoordinates(DrawingContext dc, double cell, double ox, double oy)
    {
        int n = _board!.Size;
        double fontSize = Math.Clamp(cell * 0.30, 8.0, 13.0);
        var typeface = new Typeface("Segoe UI");
        double textY = oy - fontSize - 3;
        double textBottomY = oy + (n - 1) * cell + 3;
        double textX = ox - fontSize - 3;
        double textRightX = ox + (n - 1) * cell + 3;

        for (int i = 0; i < n; i++)
        {
            char letter = i >= 8 ? (char)('A' + i + 1) : (char)('A' + i);
            var ftTop = new FormattedText(letter.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, fontSize, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ftTop, new Point(ox + i * cell - ftTop.WidthIncludingTrailingWhitespace / 2, textY));
            dc.DrawText(ftTop, new Point(ox + i * cell - ftTop.WidthIncludingTrailingWhitespace / 2, textBottomY));

            string num = (i + 1).ToString();
            var ftLeft = new FormattedText(num, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, fontSize, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ftLeft, new Point(textX, oy + i * cell - ftLeft.Height / 2));
            dc.DrawText(ftLeft, new Point(textRightX, oy + i * cell - ftLeft.Height / 2));
        }
    }

    /// <summary>
    /// Shades each empty point according to who encloses it. Only drawn during
    /// scoring, where it turns "mark the dead stones" from guesswork into
    /// something you can see.
    /// </summary>
    private void DrawTerritory(DrawingContext dc, double cell, double ox, double oy)
    {
        if (Territory is null || Territory.Size != _board!.Size) return;

        int n = _board.Size;
        double r = cell * 0.24;

        for (int row = 0; row < n; row++)
        {
            for (int col = 0; col < n; col++)
            {
                if (_board[row, col] != StoneColor.Empty) continue;

                var owner = Territory[row, col];
                if (owner is not (PointOwner.Black or PointOwner.White)) continue;

                var brush = owner == PointOwner.Black ? BlackTerritoryBrush : WhiteTerritoryBrush;
                dc.DrawRectangle(brush, null,
                    new Rect(ox + col * cell - r, oy + row * cell - r, r * 2, r * 2));
            }
        }
    }

    private void DrawStones(DrawingContext dc, double cell, double ox, double oy)
    {
        int n = _board!.Size;
        double r = cell * 0.465;

        for (int row = 0; row < n; row++)
        {
            for (int col = 0; col < n; col++)
            {
                var stone = _board![row, col];
                if (stone == StoneColor.Empty) continue;

                var center = new Point(ox + col * cell, oy + row * cell);
                if (stone == StoneColor.Black)
                    dc.DrawEllipse(BlackStone, BlackStoneOutline, center, r, r);
                else
                    dc.DrawEllipse(WhiteStone, WhiteStoneOutline, center, r, r);

                // Last-move marker: a small contrasting dot.
                if (LastMove is GoPoint lm && lm.Row == row && lm.Col == col)
                {
                    double mr = Math.Max(1.4, cell * 0.14);
                    var markerBrush = stone == StoneColor.Black
                        ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5A))
                        : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
                    dc.DrawEllipse(markerBrush, null, center, mr, mr);
                }

                // Dead-stone marker (scoring mode): red ring + cross.
                if (ScoringMode && DeadStones?.Contains(new GoPoint(row, col)) == true)
                {
                    double dr = cell * 0.34;
                    dc.DrawEllipse(null, DeadXPen, center, dr, dr);
                    dc.DrawLine(DeadXPen, new Point(center.X - dr, center.Y - dr), new Point(center.X + dr, center.Y + dr));
                    dc.DrawLine(DeadXPen, new Point(center.X - dr, center.Y + dr), new Point(center.X + dr, center.Y - dr));
                }
            }
        }
    }


    private void DrawHover(DrawingContext dc, double cell, double ox, double oy)
    {
        if (!_interactive || _hover is not GoPoint hp) return;
        if (!_board!.InBounds(hp.Row, hp.Col)) return;
        if (_board[hp.Row, hp.Col] != StoneColor.Empty) return;

        double r = cell * 0.465;
        var brush = CurrentPlayer == StoneColor.Black ? BlackGhost : WhiteGhost;
        dc.DrawEllipse(brush, null, new Point(ox + hp.Col * cell, oy + hp.Row * cell), r, r);
    }

    private void UpdateHover()
    {
        var p = Mouse.GetPosition(this);
        var (row, col) = CellFromPoint(p.X, p.Y);
        var next = row >= 0 ? new GoPoint(row, col) : (GoPoint?)null;
        if (next != _hover)
        {
            _hover = next;
            InvalidateVisual();
        }
    }

    private void OnMouseLeftDown(object? sender, MouseButtonEventArgs e)
    {
        if (_board is null) return;
        var p = e.GetPosition(this);
        var (row, col) = CellFromPoint(p.X, p.Y);
        if (row < 0) return;

        if (ScoringMode)
        {
            if (_board[row, col] != StoneColor.Empty)
                StoneMarked?.Invoke(this, new GoPoint(row, col));
            return;
        }

        if (!_interactive) return;
        if (_board[row, col] != StoneColor.Empty) return;
        MoveRequested?.Invoke(this, new GoPoint(row, col));
    }

    /// <summary>Maps a pixel position to the nearest intersection, or (-1,-1) if too far from any.</summary>
    private (int row, int col) CellFromPoint(double x, double y)
    {
        if (_board is null) return (-1, -1);
        var (cell, ox, oy, _) = ComputeLayout();
        double fx = (x - ox) / cell;
        double fy = (y - oy) / cell;
        int col = (int)Math.Round(fx);
        int row = (int)Math.Round(fy);
        if (Math.Abs(fx - col) > 0.55 || Math.Abs(fy - row) > 0.55) return (-1, -1);
        if (!_board.InBounds(row, col)) return (-1, -1);
        return (row, col);
    }

    private static Brush CreateWoodBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE9, 0xC8, 0x8A), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xD9, 0xAF, 0x6C), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xC8, 0x9B, 0x55), 1.0));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateStoneBrush(Color highlight, Color shadow, double opacity = 1.0)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.3),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            Opacity = opacity,
        };
        brush.GradientStops.Add(new GradientStop(highlight, 0.0));
        brush.GradientStops.Add(new GradientStop(highlight, 0.15));
        brush.GradientStops.Add(new GradientStop(shadow, 1.0));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}

