using System.Text;

namespace PlayGo.Core;

/// <summary>Who an empty region of the board belongs to.</summary>
public enum PointOwner
{
    /// <summary>No stone borders this region (an empty or fully open board).</summary>
    None,

    Black,
    White,

    /// <summary>The region borders both colours — contested, or a neutral dame point.</summary>
    Both,
}

/// <summary>
/// The result of flood-filling every empty region of a board: an owner per
/// intersection, the size of each region, and the resulting territory counts.
/// </summary>
public sealed class TerritoryMap
{
    private readonly PointOwner[] _owners;

    internal TerritoryMap(int size, PointOwner[] owners, IReadOnlyList<Region> regions,
        int blackTerritory, int whiteTerritory, int neutral)
    {
        Size = size;
        _owners = owners;
        Regions = regions;
        BlackTerritory = blackTerritory;
        WhiteTerritory = whiteTerritory;
        Neutral = neutral;
    }

    public int Size { get; }

    /// <summary>Ownership of each intersection. Occupied points report <see cref="PointOwner.None"/>.</summary>
    public PointOwner this[int row, int col] => _owners[row * Size + col];

    public IReadOnlyList<Region> Regions { get; }

    public int BlackTerritory { get; }

    public int WhiteTerritory { get; }

    /// <summary>Empty points in regions that are open or touch both colours (dame).</summary>
    public int Neutral { get; }
}

/// <summary>A maximal connected group of empty points, plus who surrounds it.</summary>
public sealed class Region
{
    internal Region(PointOwner owner, IReadOnlyList<GoPoint> points)
    {
        Owner = owner;
        Points = points;
    }

    public PointOwner Owner { get; }

    public IReadOnlyList<GoPoint> Points { get; }

    public int Count => Points.Count;
}

/// <summary>
/// A Go board plus the low-level rules: placing stones, capturing groups,
/// suicide detection, group/liberty queries and area scoring.
/// The board itself is intentionally free of game flow concerns (turns,
/// passes, ko-history) – see <see cref="GameManager"/> for those.
/// </summary>
public sealed class GoBoard
{
    private readonly StoneColor[,] _cells;
    private int _blackPrisoners; // white stones removed from the board
    private int _whitePrisoners; // black stones removed from the board

    public GoBoard(int size)
    {
        if (size is not (9 or 13 or 19))
            throw new ArgumentOutOfRangeException(nameof(size), "Board size must be 9, 13 or 19.");
        Size = size;
        _cells = new StoneColor[size, size];
    }

    private GoBoard(int size, StoneColor[,] cells, int blackPrisoners, int whitePrisoners)
    {
        Size = size;
        _cells = cells;
        _blackPrisoners = blackPrisoners;
        _whitePrisoners = whitePrisoners;
    }

    public int Size { get; }

    public StoneColor this[int row, int col] => _cells[row, col];

    public int BlackPrisoners => _blackPrisoners;

    public int WhitePrisoners => _whitePrisoners;

    public bool InBounds(int row, int col) =>
        (uint)row < (uint)Size && (uint)col < (uint)Size;

    public bool IsEmpty(int row, int col) => InBounds(row, col) && _cells[row, col] == StoneColor.Empty;

    public int StoneCount(StoneColor color)
    {
        int count = 0;
        for (int r = 0; r < Size; r++)
            for (int c = 0; c < Size; c++)
                if (_cells[r, c] == color) count++;
        return count;
    }

    public GoBoard Clone() => new(Size, (StoneColor[,])_cells.Clone(), _blackPrisoners, _whitePrisoners);

    /// <summary>Returns a fresh copy of the cell array (used for undo snapshots).</summary>
    public StoneColor[,] CopyCells() => (StoneColor[,])_cells.Clone();

    /// <summary>Rebuilds a board from raw cells and prisoner counts (used for undo snapshots).</summary>
    public static GoBoard FromCells(int size, StoneColor[,] cells, int blackPrisoners, int whitePrisoners) =>
        new(size, (StoneColor[,])cells.Clone(), blackPrisoners, whitePrisoners);

    public IEnumerable<GoPoint> Neighbors(int row, int col)
    {
        if (row > 0) yield return new GoPoint(row - 1, col);
        if (row < Size - 1) yield return new GoPoint(row + 1, col);
        if (col > 0) yield return new GoPoint(row, col - 1);
        if (col < Size - 1) yield return new GoPoint(row, col + 1);
    }

    /// <summary>
    /// Returns the maximal connected group of same-colored stones containing (row, col),
    /// together with the set of empty points adjacent to the group (its liberties).
    /// </summary>
    public (List<GoPoint> Group, HashSet<GoPoint> Liberties) GetGroupInfo(int row, int col)
    {
        StoneColor color = _cells[row, col];
        if (color == StoneColor.Empty)
            return (new List<GoPoint>(), new HashSet<GoPoint>());

        var group = new List<GoPoint>();
        var liberties = new HashSet<GoPoint>();
        var seen = new HashSet<GoPoint>();
        var queue = new Queue<GoPoint>();
        queue.Enqueue(new GoPoint(row, col));
        seen.Add(new GoPoint(row, col));

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            group.Add(p);
            foreach (var n in Neighbors(p.Row, p.Col))
            {
                var c = _cells[n.Row, n.Col];
                if (c == StoneColor.Empty)
                    liberties.Add(n);
                else if (c == color && seen.Add(n))
                    queue.Enqueue(n);
            }
        }

        return (group, liberties);
    }

    public int LibertyCount(int row, int col) => GetGroupInfo(row, col).Liberties.Count;

    /// <summary>
    /// True when the empty point at (row, col) is a true eye for
    /// <paramref name="color"/>: all four orthogonal neighbours are that
    /// colour, and at most one of the diagonals (if present) holds an
    /// opponent stone. Corner and edge eyes have fewer diagonals and so
    /// require none. This is the standard shape check.
    /// </summary>
    public bool IsEye(int row, int col, StoneColor color)
        => IsEyeAt(row, col, color, extraPoint: null);

    /// <summary>
    /// True when (row, col) would become a true eye for
    /// <paramref name="placedColor"/> if a stone of that colour were
    /// virtually placed at <paramref name="placedAt"/>. Used by the engine
    /// to reward moves that create eyes.
    /// </summary>
    public bool WouldCreateEye(int row, int col, GoPoint placedAt, StoneColor placedColor)
        => IsEyeAt(row, col, placedColor, placedAt);

    private bool IsEyeAt(int row, int col, StoneColor color, GoPoint? extraPoint)
    {
        if (!InBounds(row, col)) return false;

        // Treat the hypothetical stone as if it were already on the board.
        StoneColor ColorAt(int r, int c) =>
            extraPoint is GoPoint e && e.Row == r && e.Col == c ? color : _cells[r, c];

        // An eye must be empty.
        if (ColorAt(row, col) != StoneColor.Empty) return false;

        // Every orthogonal neighbour must belong to the eye's colour.
        foreach (var n in Neighbors(row, col))
            if (ColorAt(n.Row, n.Col) != color) return false;

        // Interior points tolerate one opponent diagonal; edge / corner
        // eyes have missing diagonals and so tolerate none.
        int oppDiag = 0;
        bool interior = true;
        for (int dr = -1; dr <= 1; dr += 2)
            for (int dc = -1; dc <= 1; dc += 2)
            {
                int nr = row + dr, nc = col + dc;
                if (!InBounds(nr, nc)) { interior = false; continue; }
                if (ColorAt(nr, nc) == color.Opponent()) oppDiag++;
            }
        return interior ? oppDiag <= 1 : oppDiag == 0;
    }

    /// <summary>
    /// Evaluates a move without mutating this board (works on a scratch copy),
    /// so callers can validate legality cheaply before committing.
    /// </summary>
    public MoveResult EvaluateMove(int row, int col, StoneColor color)
    {
        var clone = Clone();
        return clone.ApplyMove(row, col, color);
    }

    /// <summary>
    /// Places a stone, removing any captured opponent groups.
    /// Fails (without mutating) on occupied points, out-of-bounds and suicide.
    /// </summary>
    public MoveResult ApplyMove(int row, int col, StoneColor color)
    {
        if (!InBounds(row, col))
            return MoveResult.Fail("That point is outside the board.");
        if (color is StoneColor.Empty)
            return MoveResult.Fail("Internal error: empty color move.");
        if (_cells[row, col] != StoneColor.Empty)
            return MoveResult.Fail("That intersection is already occupied.");

        StoneColor opponent = color.Opponent();
        var captured = new List<GoPoint>();
        var seen = new HashSet<GoPoint>();

        foreach (var n in Neighbors(row, col))
        {
            if (_cells[n.Row, n.Col] != opponent || !seen.Add(n)) continue;
            var (group, liberties) = GetGroupInfo(n.Row, n.Col);
            // Liberties are computed before our stone lands on (row, col), so
            // a group with a single liberty is about to lose it and be captured.
            if (liberties.Count == 1)
            {
                foreach (var p in group)
                {
                    captured.Add(p);
                    seen.Add(p);
                }
            }
        }

        _cells[row, col] = color;
        foreach (var p in captured)
            _cells[p.Row, p.Col] = StoneColor.Empty;

        if (captured.Count > 0)
        {
            if (color == StoneColor.Black) _blackPrisoners += captured.Count;
            else _whitePrisoners += captured.Count;
        }
        else
        {
            // Suicide check: the new stone's group must keep at least one liberty.
            var (_, ownLiberties) = GetGroupInfo(row, col);
            if (ownLiberties.Count == 0)
            {
                _cells[row, col] = StoneColor.Empty;
                return MoveResult.Fail("Suicide is not allowed (the stone would have no liberties).");
            }
        }

        int libertiesAfter = LibertyCount(row, col);
        return MoveResult.Ok(captured, libertiesAfter);
    }

    /// <summary>
    /// Deterministic board fingerprint used for ko / superko detection.
    /// </summary>
    public string PositionHash()
    {
        var sb = new StringBuilder(Size * Size + 8);
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                sb.Append(_cells[r, c] switch
                {
                    StoneColor.Black => 'B',
                    StoneColor.White => 'W',
                    _ => '.',
                });
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Flood-fills every empty region and reports who surrounds it. Dead stones
    /// are treated as removed first, so the points they occupied join the
    /// surrounding region. Used by area scoring and by the on-board territory
    /// preview; the result is a snapshot and does not track later board changes.
    /// </summary>
    public TerritoryMap GetTerritory(IEnumerable<GoPoint>? deadStones = null)
    {
        var work = (StoneColor[,])_cells.Clone();
        if (deadStones is not null)
        {
            foreach (var d in deadStones)
                if (InBounds(d.Row, d.Col))
                    work[d.Row, d.Col] = StoneColor.Empty;
        }

        var owners = new PointOwner[Size * Size];
        var visited = new bool[Size, Size];
        var regions = new List<Region>();
        int blackTerritory = 0, whiteTerritory = 0, neutral = 0;

        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (work[r, c] != StoneColor.Empty || visited[r, c]) continue;

                var points = new List<GoPoint>();
                var borders = new HashSet<StoneColor>();
                var queue = new Queue<GoPoint>();
                visited[r, c] = true;
                queue.Enqueue(new GoPoint(r, c));

                while (queue.Count > 0)
                {
                    var p = queue.Dequeue();
                    points.Add(p);
                    foreach (var n in Neighbors(p.Row, p.Col))
                    {
                        var v = work[n.Row, n.Col];
                        if (v == StoneColor.Empty)
                        {
                            if (!visited[n.Row, n.Col])
                            {
                                visited[n.Row, n.Col] = true;
                                queue.Enqueue(n);
                            }
                        }
                        else
                        {
                            borders.Add(v);
                        }
                    }
                }

                var owner = borders.Count switch
                {
                    0 => PointOwner.None,
                    // Bordered by both colours: contested territory or a dame point.
                    > 1 => PointOwner.Both,
                    _ => borders.Contains(StoneColor.Black) ? PointOwner.Black : PointOwner.White,
                };

                foreach (var p in points)
                    owners[p.Row * Size + p.Col] = owner;
                regions.Add(new Region(owner, points));

                if (owner == PointOwner.Black) blackTerritory += points.Count;
                else if (owner == PointOwner.White) whiteTerritory += points.Count;
                else neutral += points.Count;
            }
        }

        return new TerritoryMap(Size, owners, regions, blackTerritory, whiteTerritory, neutral);
    }

    /// <summary>
    /// Chinese-area scoring. Dead stones are removed from the board before
    /// counting: each empty point that touches only black is black territory,
    /// only white is white territory; points touching both are neutral (dame).
    /// Score = territory + live stones on the board; White receives komi.
    /// </summary>
    public ScoreResult CountScore(IEnumerable<GoPoint> deadStones, double komi) =>
        CountScore(GetTerritory(deadStones), deadStones, komi);

    /// <summary>
    /// Scores a position from a territory map that has already been computed.
    /// Lets the UI share a single flood fill between the on-board preview and
    /// the running score estimate.
    /// </summary>
    public ScoreResult CountScore(TerritoryMap map, IEnumerable<GoPoint> deadStones, double komi)
    {
        int deadBlack = 0, deadWhite = 0;
        foreach (var d in deadStones)
        {
            if (!InBounds(d.Row, d.Col)) continue;
            var stone = _cells[d.Row, d.Col];
            if (stone == StoneColor.Black) deadBlack++;
            else if (stone == StoneColor.White) deadWhite++;
        }

        int blackStones = StoneCount(StoneColor.Black) - deadBlack;
        int whiteStones = StoneCount(StoneColor.White) - deadWhite;

        double blackScore = map.BlackTerritory + blackStones;
        double whiteScore = map.WhiteTerritory + whiteStones + komi;

        return new ScoreResult(
            blackScore, whiteScore, komi,
            map.BlackTerritory, map.WhiteTerritory,
            blackStones, whiteStones,
            deadBlack, deadWhite, map.Neutral);
    }
}

