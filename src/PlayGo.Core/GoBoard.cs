using System.Text;

namespace PlayGo.Core;

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
    /// Chinese-area scoring. Dead stones are removed from the board before
    /// counting: each empty point that touches only black is black territory,
    /// only white is white territory; points touching both are neutral (dame).
    /// Score = territory + live stones on the board; White receives komi.
    /// </summary>
    public ScoreResult CountScore(ISet<GoPoint> deadStones, double komi)
    {
        var work = (StoneColor[,])_cells.Clone();
        int deadBlack = 0, deadWhite = 0;
        foreach (var d in deadStones)
        {
            if (!InBounds(d.Row, d.Col)) continue;
            if (work[d.Row, d.Col] == StoneColor.Black) deadBlack++;
            else if (work[d.Row, d.Col] == StoneColor.White) deadWhite++;
            work[d.Row, d.Col] = StoneColor.Empty;
        }

        int blackTerritory = 0, whiteTerritory = 0, neutral = 0;
        var visited = new bool[Size, Size];

        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (work[r, c] != StoneColor.Empty || visited[r, c]) continue;

                // Flood-fill one empty region.
                var region = new List<GoPoint>();
                var borders = new HashSet<StoneColor>();
                var queue = new Queue<GoPoint>();
                visited[r, c] = true;
                queue.Enqueue(new GoPoint(r, c));

                while (queue.Count > 0)
                {
                    var p = queue.Dequeue();
                    region.Add(p);
                    foreach (var n in Neighbors(p.Row, p.Col))
                    {
                        var c2 = work[n.Row, n.Col];
                        if (c2 == StoneColor.Empty && !visited[n.Row, n.Col])
                        {
                            visited[n.Row, n.Col] = true;
                            queue.Enqueue(n);
                        }
                        else if (c2 != StoneColor.Empty)
                        {
                            borders.Add(c2);
                        }
                    }
                }

                if (borders.Contains(StoneColor.Black) && !borders.Contains(StoneColor.White))
                    blackTerritory += region.Count;
                else if (borders.Contains(StoneColor.White) && !borders.Contains(StoneColor.Black))
                    whiteTerritory += region.Count;
                else
                    neutral += region.Count;
            }
        }

        int blackStones = StoneCount(StoneColor.Black) - deadBlack;
        int whiteStones = StoneCount(StoneColor.White) - deadWhite;

        double blackScore = blackTerritory + blackStones;
        double whiteScore = whiteTerritory + whiteStones + komi;

        return new ScoreResult(
            blackScore, whiteScore, komi,
            blackTerritory, whiteTerritory,
            blackStones, whiteStones,
            deadBlack, deadWhite, neutral);
    }
}

