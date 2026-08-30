namespace PlayGo.Core;

public enum PlayerType
{
    Human,
    Computer,
}

public enum GameState
{
    /// <summary>Players take turns placing stones or passing.</summary>
    Playing,

    /// <summary>Both players passed; dead stones can be marked before scoring.</summary>
    Scoring,

    /// <summary>The game ended (score counted or a player resigned).</summary>
    Finished,
}

/// <summary>
/// Orchestrates a full game of Go: whose turn it is, passes, resignations,
/// ko / positional-superko enforcement, move history, undo and the
/// two-phase game end (marking dead stones, then scoring).
/// </summary>
public sealed class GameManager
{
    private readonly List<GoMove> _history = new();
    private readonly List<Snapshot> _undoStack = new();
    private readonly Dictionary<string, int> _positionCounts = new();

    private HashSet<GoPoint> _deadStones = new();
    private int _consecutivePasses;
    private GameState _state = GameState.Playing;
    private StoneColor _currentPlayer = StoneColor.Black;
    private StoneColor? _winner;
    private int _moveNumber;

    public GameManager(
        int size = 19,
        PlayerType blackPlayer = PlayerType.Human,
        PlayerType whitePlayer = PlayerType.Human,
        double? komi = null,
        int handicap = 0)
    {
        BoardSize = size;
        Board = new GoBoard(size);
        BlackPlayer = blackPlayer;
        WhitePlayer = whitePlayer;
        Komi = komi ?? DefaultKomi(size);
        Handicap = Math.Clamp(handicap, 0, MaxHandicap);
        if (Handicap > 0) ApplyHandicap(Handicap);
        _positionCounts[Board.PositionHash()] = 1;
    }

    public GoBoard Board { get; private set; }

    public int BoardSize { get; private set; }

    public double Komi { get; set; }

    /// <summary>Number of black handicap stones placed before the game began.</summary>
    public int Handicap { get; private set; }

    public const int MaxHandicap = 9;

    /// <summary>
    /// The usual compensation for moving second. It grows with the board because
    /// the value of the first move is larger on a bigger board.
    /// </summary>
    public static double DefaultKomi(int boardSize) => boardSize switch
    {
        9 => 5.5,
        13 => 6.5,
        _ => 7.5,
    };

    /// <summary>
    /// Handicap stones in the traditional order: the four corners first, then the
    /// side star points, then tengen.
    /// </summary>
    public static (int Row, int Col)[] HandicapPoints(int size, int count)
    {
        var order = size switch
        {
            9 => HandicapOrder9,
            13 => HandicapOrder13,
            _ => HandicapOrder19,
        };
        return order.Take(Math.Clamp(count, 0, MaxHandicap)).ToArray();
    }

    public PlayerType BlackPlayer { get; set; }

    public PlayerType WhitePlayer { get; set; }

    public GameState State => _state;

    public StoneColor CurrentPlayer => _currentPlayer;

    public StoneColor? Winner => _winner;

    public int MoveNumber => _moveNumber;

    public int ConsecutivePasses => _consecutivePasses;

    public IReadOnlyList<GoMove> History => _history;

    public IReadOnlyCollection<GoPoint> DeadStones => _deadStones;

    public int BlackPrisoners => Board.BlackPrisoners;

    public int WhitePrisoners => Board.WhitePrisoners;

    public bool IsComputerTurn =>
        _state == GameState.Playing &&
        (_currentPlayer == StoneColor.Black ? BlackPlayer == PlayerType.Computer
                                            : WhitePlayer == PlayerType.Computer);

    public PlayerType GetPlayer(StoneColor color) =>
        color == StoneColor.Black ? BlackPlayer : WhitePlayer;

    public event Action? BoardChanged;

    public event Action? StatusChanged;

    public event Action? GameOver;

    /// <summary>
    /// Checks whether the current player may legally play at (row, col),
    /// enforcing occupancy, suicide, basic ko and positional superko.
    /// Does not mutate the game. <see cref="PlayStone"/> commits the move.
    /// </summary>
    public MoveResult CanPlay(int row, int col)
    {
        if (_state != GameState.Playing)
            return MoveResult.Fail("The game is not in progress.");

        StoneColor color = _currentPlayer;
        var scratch = Board.Clone();
        var result = scratch.ApplyMove(row, col, color);
        if (!result.Success)
            return result;

        string newHash = scratch.PositionHash();

        // Basic ko: immediate recapture that restores the position from two plies ago.
        if (_history.Count >= 2 && _history[^2].BoardPositionHash == newHash)
            return MoveResult.Fail("Ko: you may not immediately recapture this shape. Play elsewhere first.");

        // Positional superko: repeating any previous board position is forbidden.
        if (_positionCounts.TryGetValue(newHash, out int prior) && prior > 0)
            return MoveResult.Fail("Superko: this move would recreate a previous board position.");

        return MoveResult.Ok(result.Captured, result.LibertiesAfter);
    }

    /// <summary>
    /// Tries to play a stone for the current player.
    /// Enforces occupancy, suicide, basic ko and positional superko.
    /// </summary>
    public MoveResult PlayStone(int row, int col)
    {
        var check = CanPlay(row, col);
        if (!check.Success)
            return check;

        // Commit.
        PushSnapshot();
        var result = Board.ApplyMove(row, col, _currentPlayer);
        string actualHash = Board.PositionHash();
        _positionCounts[actualHash] = (_positionCounts.TryGetValue(actualHash, out int prior) ? prior : 0) + 1;
        _consecutivePasses = 0;
        RecordMove(MoveKind.Play, result.Captured, actualHash, new GoPoint(row, col));
        _winner = null;
        EndOfTurn();
        return result;
    }

    /// <summary>Records a pass for the current player; two consecutive passes end the game.</summary>
    public GoMove? Pass()
    {
        if (_state != GameState.Playing) return null;

        PushSnapshot();
        _consecutivePasses++;
        string hash = Board.PositionHash();
        var move = RecordMove(MoveKind.Pass, Array.Empty<GoPoint>(), hash, null);

        if (_consecutivePasses >= 2)
        {
            _state = GameState.Scoring;
            _deadStones = new HashSet<GoPoint>();
        }
        else
        {
            _currentPlayer = _currentPlayer.Opponent();
        }

        RaiseStatus();
        return move;
    }

    public GoMove? Resign()
    {
        if (_state != GameState.Playing) return null;

        PushSnapshot();
        _winner = _currentPlayer.Opponent();
        string hash = Board.PositionHash();
        var move = RecordMove(MoveKind.Resign, Array.Empty<GoPoint>(), hash, null);
        _state = GameState.Finished;
        RaiseStatus();
        GameOver?.Invoke();
        return move;
    }

    /// <summary>
    /// Undoes the most recent move. Against a computer opponent it continues
    /// undoing until it is a human's turn again. Works from the scoring phase
    /// too, which steps back into play.
    /// </summary>
    public bool Undo()
    {
        if (_state == GameState.Finished && _undoStack.Count == 0) return false;
        if (_history.Count == 0) return false;

        var colorBefore = _currentPlayer;

        // Undo the last move.
        RestoreLastSnapshot();

        if (BlackPlayer == PlayerType.Computer && WhitePlayer == PlayerType.Computer)
        {
            // There is no human to hand the turn back to, so step back a whole
            // turn pair and leave the same colour on move.
            while (_history.Count > 0 && _currentPlayer != colorBefore)
                RestoreLastSnapshot();
        }
        else
        {
            // Keep undoing while the player who would now move is a computer.
            while (IsComputerTurn && _history.Count > 0)
            {
                RestoreLastSnapshot();
            }
        }

        RaiseBoard();
        RaiseStatus();
        return true;
    }

    /// <summary>
    /// Marks or unmarks a stone as dead during scoring. Dead stones live and die
    /// as a group, so the whole connected group toggles together — clicking any
    /// stone in a group marks all of it in one go.
    /// </summary>
    public void ToggleDeadStone(GoPoint p)
    {
        if (_state != GameState.Scoring) return;
        if (!Board.InBounds(p.Row, p.Col)) return;
        if (Board[p.Row, p.Col] == StoneColor.Empty) return;

        var (group, _) = Board.GetGroupInfo(p.Row, p.Col);

        // If any stone of the group is already marked, clear the whole group.
        bool unmark = group.Any(g => _deadStones.Contains(g));
        foreach (var g in group)
        {
            if (unmark) _deadStones.Remove(g);
            else _deadStones.Add(g);
        }
        RaiseBoard();
    }

    public void ClearDeadMarks()
    {
        _deadStones = new HashSet<GoPoint>();
        RaiseBoard();
    }

    /// <summary>Counts the final score and finishes the game.</summary>
    public ScoreResult? CountScore()
    {
        if (_state != GameState.Scoring) return null;
        var result = Board.CountScore(_deadStones, Komi);
        _state = GameState.Finished;
        _winner = result.Winner;
        RaiseStatus();
        GameOver?.Invoke();
        return result;
    }

    /// <summary>Leaves scoring mode to continue playing (dead marks are discarded).</summary>
    public void ResumePlay()
    {
        if (_state != GameState.Scoring) return;
        _state = GameState.Playing;
        _winner = null;
        _consecutivePasses = 0;
        _deadStones = new HashSet<GoPoint>();
        RaiseBoard();
        RaiseStatus();
    }

    public void NewGame(int size, PlayerType black, PlayerType white, double? komi = null, int handicap = 0)
    {
        Board = new GoBoard(size);
        BoardSize = size;
        Komi = komi ?? DefaultKomi(size);
        BlackPlayer = black;
        WhitePlayer = white;
        Handicap = Math.Clamp(handicap, 0, MaxHandicap);
        _history.Clear();
        _undoStack.Clear();
        _deadStones = new HashSet<GoPoint>();
        _positionCounts.Clear();
        if (Handicap > 0) ApplyHandicap(Handicap);
        _positionCounts[Board.PositionHash()] = 1;
        _consecutivePasses = 0;
        _currentPlayer = Handicap > 0 ? StoneColor.White : StoneColor.Black;
        _winner = null;
        _state = GameState.Playing;
        _moveNumber = 0;
        RaiseBoard();
        RaiseStatus();
    }

    /// <summary>
    /// Rebuilds the position after <paramref name="index"/> recorded moves (0-based).
    /// Pass -1 for the starting position. Handicap stones are included: they are
    /// placed before the first move and never appear in the move history.
    /// </summary>
    public GoBoard GetBoardAt(int index)
    {
        var rebuilt = new GoBoard(BoardSize);
        foreach (var (row, col) in HandicapPoints(BoardSize, Handicap))
            rebuilt.ApplyMove(row, col, StoneColor.Black);

        int limit = Math.Min(index, _history.Count - 1);
        for (int i = 0; i <= limit; i++)
        {
            var move = _history[i];
            if (move.Kind == MoveKind.Play && move.Point is GoPoint p)
                rebuilt.ApplyMove(p.Row, p.Col, move.Color);
        }
        return rebuilt;
    }

    /// <summary>
    /// Builds an immutable legality predicate for the current position. The
    /// returned delegate captures its own copy of everything it reads, so it is
    /// safe to call from a background thread while the game keeps running.
    /// </summary>
    public Func<int, int, bool> CreateLegalityFilter()
    {
        var snapshot = Board.Clone();
        var color = _currentPlayer;
        var forbidden = new HashSet<string>(_positionCounts.Keys);
        string? koHash = _history.Count >= 2 ? _history[^2].BoardPositionHash : null;

        return (row, col) =>
        {
            if (snapshot[row, col] != StoneColor.Empty) return false;
            var scratch = snapshot.Clone();
            if (!scratch.ApplyMove(row, col, color).Success) return false;

            string hash = scratch.PositionHash();
            if (koHash is not null && hash == koHash) return false;
            return !forbidden.Contains(hash);
        };
    }


    // ----- internal helpers -----

    private static readonly (int, int)[] HandicapOrder9 =
        { (2, 6), (6, 2), (6, 6), (2, 2), (4, 4), (2, 4), (6, 4), (4, 2), (4, 6) };

    private static readonly (int, int)[] HandicapOrder13 =
        { (3, 9), (9, 3), (9, 9), (3, 3), (3, 6), (9, 6), (6, 9), (6, 3), (6, 6) };

    private static readonly (int, int)[] HandicapOrder19 =
        { (3, 15), (15, 3), (15, 15), (3, 3), (3, 9), (15, 9), (9, 15), (9, 3), (9, 9) };

    /// <summary>
    /// Places black's handicap stones and hands the first move to white.
    /// Assumes the board is freshly created and the history is empty.
    /// </summary>
    private void ApplyHandicap(int count)
    {
        foreach (var (row, col) in HandicapPoints(BoardSize, count))
            Board.ApplyMove(row, col, StoneColor.Black);
        _currentPlayer = StoneColor.White;
    }

    private sealed class Snapshot
    {
        public required StoneColor[,] Cells { get; init; }
        public required int BlackPrisoners { get; init; }
        public required int WhitePrisoners { get; init; }
        public required string[] PositionHashes { get; init; }
        public required int[] PositionCounts { get; init; }
        public required int ConsecutivePasses { get; init; }
        public required StoneColor CurrentPlayer { get; init; }
        public required GameState State { get; init; }
        public required StoneColor? Winner { get; init; }
        public required int MoveNumber { get; init; }
        public required HashSet<GoPoint> DeadStones { get; init; }
    }

    private void PushSnapshot()
    {
        var keys = _positionCounts.Keys.ToArray();
        var values = _positionCounts.Values.ToArray();
        _undoStack.Add(new Snapshot
        {
            Cells = Board.CopyCells(),
            BlackPrisoners = Board.BlackPrisoners,
            WhitePrisoners = Board.WhitePrisoners,
            PositionHashes = keys,
            PositionCounts = values,
            ConsecutivePasses = _consecutivePasses,
            CurrentPlayer = _currentPlayer,
            State = _state,
            Winner = _winner,
            MoveNumber = _moveNumber,
            DeadStones = new HashSet<GoPoint>(_deadStones),
        });
    }

    private void RestoreLastSnapshot()
    {
        var snap = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        Board = GoBoard.FromCells(BoardSize, snap.Cells, snap.BlackPrisoners, snap.WhitePrisoners);
        _consecutivePasses = snap.ConsecutivePasses;
        _currentPlayer = snap.CurrentPlayer;
        _state = snap.State;
        _winner = snap.Winner;
        _moveNumber = snap.MoveNumber;
        _deadStones = new HashSet<GoPoint>(snap.DeadStones);
        _history.RemoveAt(_history.Count - 1);

        _positionCounts.Clear();
        for (int i = 0; i < snap.PositionHashes.Length; i++)
            _positionCounts[snap.PositionHashes[i]] = snap.PositionCounts[i];
    }

    private GoMove RecordMove(MoveKind kind, IReadOnlyList<GoPoint> captured, string hash, GoPoint? point)
    {
        _moveNumber++;
        var move = new GoMove
        {
            MoveNumber = _moveNumber,
            Color = _currentPlayer,
            Kind = kind,
            Point = point,
            Captured = captured,
            BlackPrisoners = Board.BlackPrisoners,
            WhitePrisoners = Board.WhitePrisoners,
            BoardPositionHash = hash,
        };
        _history.Add(move);
        return move;
    }

    private void EndOfTurn()
    {
        _currentPlayer = _currentPlayer.Opponent();
        RaiseBoard();
        RaiseStatus();
    }

    private void RaiseBoard() => BoardChanged?.Invoke();

    private void RaiseStatus() => StatusChanged?.Invoke();
}

