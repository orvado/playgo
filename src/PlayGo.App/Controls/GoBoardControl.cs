using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    // Illegal-move feedback: a red pulse over the rejected intersection with
    // the reason written beside it.
    private static readonly Brush IllegalFill = CreateFrozenBrush(Color.FromArgb(0x5A, 0xE0, 0x45, 0x2F));
    private static readonly Pen IllegalRing = CreateFrozenPen(Color.FromRgb(0xFF, 0x6B, 0x5A), 2.6);
    private static readonly Brush IllegalTextBg = CreateFrozenBrush(Color.FromArgb(0xE6, 0x2A, 0x12, 0x14));
    private static readonly Pen IllegalTextBorder = CreateFrozenPen(Color.FromRgb(0xE0, 0x45, 0x2F), 1.2);
    private static readonly Brush IllegalTextBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xE8, 0xE4));

    // Keyboard cursor: a marker you can drive with the arrow keys.
    private static readonly Pen CursorPen = CreateFrozenPen(Color.FromRgb(0xF5, 0xC5, 0x42), 2.2);

    /// <summary>How long the illegal-move flash and its message stay on screen.</summary>
    private const double FlashDurationMs = 1500;

    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private GoBoard? _board;
    private GoPoint? _hover;
    private bool _interactive;

    private readonly DispatcherTimer _flashTimer;
    private GoPoint? _flashPoint;
    private string? _flashMessage;
    private DateTime _flashStart;
    private double _flashAlpha;

    /// <summary>How far through the flash we are, 0 to 1.</summary>
    private double _flashProgress;

    private IReadOnlyList<GoMove>? _moveHistory;
    private Dictionary<GoPoint, (int Number, StoneColor Color)>? _moveNumbers;
    private int _moveNumbersForCount = -1;

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

        _flashTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(32),
        };
        _flashTimer.Tick += OnFlashTick;
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

    /// <summary>
    /// The keyboard cursor: where the arrow keys are pointing. Null when the
    /// board has not been navigated with the keyboard yet.
    /// </summary>
    public GoPoint? BoardCursor
    {
        get => _boardCursor;
        set
        {
            if (_boardCursor == value) return;
            _boardCursor = value;
            InvalidateVisual();
        }
    }

    private GoPoint? _boardCursor;

    /// <summary>Draws the move number on each stone (a standard review aid).</summary>
    public bool ShowMoveNumbers
    {
        get => _showMoveNumbers;
        set { _showMoveNumbers = value; InvalidateVisual(); }
    }

    private bool _showMoveNumbers;

    /// <summary>
    /// The game's move history, replayed to work out which move number placed
    /// each stone. Only needed when <see cref="ShowMoveNumbers"/> is on.
    /// </summary>
    public IReadOnlyList<GoMove>? MoveHistory
    {
        get => _moveHistory;
        set
        {
            _moveHistory = value;
            _moveNumbersForCount = -1;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Flashes an intersection red and writes <paramref name="message"/> next to
    /// it. Used to explain a rejected move on the board itself, where the player
    /// is looking, rather than only in the status bar.
    /// </summary>
    public void FlashIllegal(GoPoint point, string message)
    {
        _flashPoint = point;
        _flashMessage = message;
        _flashStart = DateTime.Now;
        _flashAlpha = 1.0;
        _flashTimer.Start();
        InvalidateVisual();
    }

    /// <summary>Drops the keyboard cursor (used when the game is reset).</summary>
    public void ClearCursor() => BoardCursor = null;

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
        if (ShowMoveNumbers)
            DrawMoveNumbers(dc, cell, ox, oy);
        DrawCursor(dc, cell, ox, oy);
        DrawHover(dc, cell, ox, oy);
        DrawIllegal(dc, cell, ox, oy);
    }

    // ---------- keyboard ----------

    /// <summary>
    /// Arrow keys drive a cursor around the board; Enter or Space plays there.
    /// This matters because clicking a 19×19 board precisely is fiddly, and
    /// keyboard placement is much faster once you know where you want to play.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_board is null) return;

        // Start from the middle of the board the first time.
        int mid = (_board.Size - 1) / 2;
        int row = BoardCursor?.Row ?? mid;
        int col = BoardCursor?.Col ?? mid;

        switch (e.Key)
        {
            case Key.Up:
                MoveCursor(row - 1, col);
                e.Handled = true;
                break;
            case Key.Down:
                MoveCursor(row + 1, col);
                e.Handled = true;
                break;
            case Key.Left:
                MoveCursor(row, col - 1);
                e.Handled = true;
                break;
            case Key.Right:
                MoveCursor(row, col + 1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                CommitCursor();
                e.Handled = true;
                break;
        }
    }

    private void MoveCursor(int row, int col)
    {
        if (_board is null) return;
        BoardCursor = new GoPoint(Math.Clamp(row, 0, _board.Size - 1), Math.Clamp(col, 0, _board.Size - 1));
    }

    /// <summary>Plays at the cursor, or toggles a dead stone during scoring.</summary>
    private void CommitCursor()
    {
        if (_board is null || BoardCursor is not GoPoint p) return;
        if (!_board.InBounds(p.Row, p.Col)) return;

        if (ScoringMode)
        {
            if (_board[p.Row, p.Col] != StoneColor.Empty)
                StoneMarked?.Invoke(this, p);
            return;
        }

        if (!_interactive) return;
        if (_board[p.Row, p.Col] != StoneColor.Empty) return;
        MoveRequested?.Invoke(this, p);
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

    /// <summary>
    /// Writes the number of the move that placed each stone. Replayed from the
    /// history rather than stored on the board, so it survives undo and review.
    /// </summary>
    private void DrawMoveNumbers(DrawingContext dc, double cell, double ox, double oy)
    {
        if (_board is null) return;
        if (_moveNumbers is null || _moveNumbersForCount != (_moveHistory?.Count ?? 0))
            RebuildMoveNumbers();
        if (_moveNumbers is null || _moveNumbers.Count == 0) return;

        double fontSize = Math.Clamp(cell * 0.34, 8.0, 15.0);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int row = 0; row < _board.Size; row++)
        {
            for (int col = 0; col < _board.Size; col++)
            {
                var stone = _board[row, col];
                if (stone == StoneColor.Empty) continue;
                if (!_moveNumbers.TryGetValue(new GoPoint(row, col), out var info)) continue;

                // Guard against replay and live board disagreeing (review mode).
                if (info.Color != stone) continue;

                var text = new FormattedText(info.Number.ToString(),
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    LabelTypeface, fontSize,
                    stone == StoneColor.Black ? Brushes.White : Brushes.Black, dpi);

                var center = new Point(ox + col * cell, oy + row * cell);
                dc.DrawText(text, new Point(center.X - text.WidthIncludingTrailingWhitespace / 2,
                                            center.Y - text.Height / 2));
            }
        }
    }

    private void RebuildMoveNumbers()
    {
        _moveNumbersForCount = _moveHistory?.Count ?? 0;
        _moveNumbers = null;
        if (_moveHistory is null || _board is null) return;

        var work = new GoBoard(_board.Size);
        var map = new Dictionary<GoPoint, (int Number, StoneColor Color)>();
        foreach (var move in _moveHistory)
        {
            if (move.Kind != MoveKind.Play || move.Point is not GoPoint p) continue;
            if (work.ApplyMove(p.Row, p.Col, move.Color).Success)
                map[new GoPoint(p.Row, p.Col)] = (move.MoveNumber, move.Color);
        }
        _moveNumbers = map;
    }

    /// <summary>
    /// Draws the keyboard cursor as four corner brackets. Brackets rather than
    /// a filled shape, so it reads as "you are here" and never as a stone.
    /// </summary>
    private void DrawCursor(DrawingContext dc, double cell, double ox, double oy)
    {
        if (_board is null || BoardCursor is not GoPoint p) return;
        if (!_board.InBounds(p.Row, p.Col)) return;

        // Nothing can be played, so don't draw a cursor that promises otherwise.
        if (!_interactive && !ScoringMode) return;

        double r = cell * 0.34;
        double len = r * 0.8;
        var center = new Point(ox + p.Col * cell, oy + p.Row * cell);

        foreach (var (dx, dy) in Corners)
        {
            var corner = new Point(center.X + dx * r, center.Y + dy * r);
            dc.DrawLine(CursorPen, corner, new Point(corner.X - dx * len, corner.Y));
            dc.DrawLine(CursorPen, corner, new Point(corner.X, corner.Y - dy * len));
        }
    }

    private static readonly (int dx, int dy)[] Corners = { (-1, -1), (1, -1), (-1, 1), (1, 1) };

    /// <summary>
    /// The rejected move: a red pulse over the intersection with the reason
    /// written beside it, so the feedback lands where the player is looking
    /// instead of only in the status bar.
    /// </summary>
    private void DrawIllegal(DrawingContext dc, double cell, double ox, double oy)
    {
        if (_board is null || _flashPoint is not GoPoint p || _flashAlpha <= 0) return;
        if (!_board.InBounds(p.Row, p.Col)) return;

        var center = new Point(ox + p.Col * cell, oy + p.Row * cell);

        dc.PushOpacity(_flashAlpha);
        dc.DrawEllipse(IllegalFill, IllegalRing, center, cell * 0.46, cell * 0.46);
        dc.Pop();

        if (string.IsNullOrEmpty(_flashMessage)) return;

        // The message holds full opacity for most of the flash, then fades.
        double textAlpha = Math.Clamp((1.0 - _flashProgress) * 3.0, 0, 1);
        if (textAlpha <= 0) return;

        dc.PushOpacity(textAlpha);
        DrawIllegalMessage(dc, cell, ox, oy, p);
        dc.Pop();
    }

    private void DrawIllegalMessage(DrawingContext dc, double cell, double ox, double oy, GoPoint p)
    {
        double fontSize = Math.Clamp(cell * 0.30, 11.0, 14.0);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var text = new FormattedText(_flashMessage,
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelTypeface, fontSize, IllegalTextBrush, dpi)
        {
            MaxTextWidth = Math.Max(140, ActualWidth * 0.45),
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        const double padX = 8, padY = 5;
        double w = Math.Min(text.Width + padX * 2, ActualWidth - 8);
        double h = text.Height + padY * 2;

        // Sit just below-right of the point, pulled back inside when that
        // would push the box off an edge.
        double x = Math.Clamp(ox + p.Col * cell + cell * 0.6, 4, Math.Max(4, ActualWidth - w - 4));
        double y = Math.Clamp(oy + p.Row * cell + cell * 0.6, 4, Math.Max(4, ActualHeight - h - 4));

        dc.DrawRoundedRectangle(IllegalTextBg, IllegalTextBorder, new Rect(x, y, w, h), 5, 5);
        dc.DrawText(text, new Point(x + padX, y + padY));
    }

    private void OnFlashTick(object? sender, EventArgs e)
    {
        double elapsed = (DateTime.Now - _flashStart).TotalMilliseconds;
        double t = elapsed / FlashDurationMs;

        if (t >= 1.0)
        {
            _flashTimer.Stop();
            _flashPoint = null;
            _flashMessage = null;
            _flashAlpha = 0;
            _flashProgress = 1;
            InvalidateVisual();
            return;
        }

        _flashProgress = t;
        // Two quick pulses, fading out across the life of the flash.
        double pulse = 0.5 + 0.5 * Math.Cos(t * Math.PI * 4);
        _flashAlpha = Math.Clamp((1.0 - t) * 1.8, 0, 1) * (0.35 + 0.65 * pulse);
        InvalidateVisual();
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

        // Take focus so the arrow keys work straight after clicking, and park
        // the keyboard cursor on whatever was clicked.
        Focus();

        var p = e.GetPosition(this);
        var (row, col) = CellFromPoint(p.X, p.Y);
        if (row < 0) return;

        BoardCursor = new GoPoint(row, col);

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

