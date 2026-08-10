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

    public GameManager(int size = 19, PlayerType blackPlayer = PlayerType.Human, PlayerType whitePlayer = PlayerType.Human)
    {
        BoardSize = size;
        Board = new GoBoard(size);
        BlackPlayer = blackPlayer;
        WhitePlayer = whitePlayer;
        _positionCounts[Board.PositionHash()] = 1;
    }

    public GoBoard Board { get; private set; }

    public int BoardSize { get; private set; }

    public double Komi { get; set; } = 7.5;

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
        _currentPlayer == StoneColor.Black ? BlackPlayer == PlayerType.Computer
                                           : WhitePlayer == PlayerType.Computer;

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
    /// undoing until it is a human's turn again.
    /// </summary>
    public bool Undo()
    {
        if (_state == GameState.Scoring) return false;
        if (_history.Count == 0) return false;

        // Undo the last move.
        RestoreLastSnapshot();

        // Keep undoing while the player who would now move is a computer.
        while (IsComputerTurn && _history.Count > 0)
        {
            RestoreLastSnapshot();
        }

        RaiseBoard();
        RaiseStatus();
        return true;
    }

    public void ToggleDeadStone(GoPoint p)
    {
        if (_state != GameState.Scoring) return;
        if (!Board.InBounds(p.Row, p.Col)) return;
        if (Board[p.Row, p.Col] == StoneColor.Empty) return;

        if (!_deadStones.Remove(p))
            _deadStones.Add(p);
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

    public void NewGame(int size, PlayerType black, PlayerType white, double komi = 7.5)
    {
        Board = new GoBoard(size);
        BoardSize = size;
        Komi = komi;
        BlackPlayer = black;
        WhitePlayer = white;
        _history.Clear();
        _undoStack.Clear();
        _deadStones = new HashSet<GoPoint>();
        _positionCounts.Clear();
        _positionCounts[Board.PositionHash()] = 1;
        _consecutivePasses = 0;
        _currentPlayer = StoneColor.Black;
        _winner = null;
        _state = GameState.Playing;
        _moveNumber = 0;
        RaiseBoard();
        RaiseStatus();
    }


    // ----- internal helpers -----

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

