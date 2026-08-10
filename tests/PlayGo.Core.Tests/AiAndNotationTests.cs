using PlayGo.Core;

namespace PlayGo.Core.Tests;

public class AiTests
{
    [Fact]
    public void ChooseMove_ReturnsLegalMoveOnEmptyBoard()
    {
        var board = new GoBoard(9);
        var move = GoAI.ChooseMove(board, StoneColor.Black, new Random(1));

        Assert.NotNull(move);
        var res = board.EvaluateMove(move.Value.Row, move.Value.Col, StoneColor.Black);
        Assert.True(res.Success);
    }

    [Fact]
    public void ChooseMove_CapturesWhenPossible()
    {
        // White stone at (1,1) in atari at (2,1) — black must capture.
        var board = new GoBoard(9);
        board.ApplyMove(0, 1, StoneColor.Black);
        board.ApplyMove(1, 0, StoneColor.Black);
        board.ApplyMove(1, 2, StoneColor.Black);
        board.ApplyMove(1, 1, StoneColor.White);

        var move = GoAI.ChooseMove(board, StoneColor.Black, new Random(42));

        Assert.NotNull(move);
        Assert.Equal(new GoPoint(2, 1), move.Value);
    }

    [Fact]
    public void ChooseMove_SavesOwnGroupInAtari()
    {
        // Black group at (1,1) with single liberty (2,1); white threatens.
        var board = new GoBoard(9);
        board.ApplyMove(1, 1, StoneColor.Black);
        board.ApplyMove(0, 1, StoneColor.White);
        board.ApplyMove(1, 0, StoneColor.White);
        board.ApplyMove(1, 2, StoneColor.White);

        var move = GoAI.ChooseMove(board, StoneColor.Black, new Random(7));

        Assert.NotNull(move);
        Assert.Equal(new GoPoint(2, 1), move.Value); // extend to escape atari
    }

    [Fact]
    public void ChooseMove_ReturnsNullOnFullBoard()
    {
        var cells = new StoneColor[9, 9];
        for (int r = 0; r < 9; r++)
            for (int c = 0; c < 9; c++)
                cells[r, c] = StoneColor.Black;
        var board = GoBoard.FromCells(9, cells, 0, 0);

        Assert.Null(GoAI.ChooseMove(board, StoneColor.White, new Random(1)));
    }

    [Fact]
    public void FullGame_ComputerVsComputer_PlaysToCompletionWithoutRepeatingPositions()
    {
        var game = new GameManager(9, PlayerType.Computer, PlayerType.Computer);
        var rng = new Random(2026);
        var hashes = new HashSet<string> { game.Board.PositionHash() };
        int guard = 0;

        while (game.State == GameState.Playing && guard++ < 5000)
        {
            var color = game.CurrentPlayer;
            var move = GoAI.ChooseMove(game.Board, color, rng, game.MoveNumber, 81,
                isLegal: (r, c) => game.CanPlay(r, c).Success);

            if (move is GoPoint p)
            {
                var res = game.PlayStone(p.Row, p.Col);
                Assert.True(res.Success, $"AI played an illegal move at {p}: {res.Error}");
                Assert.True(hashes.Add(game.Board.PositionHash()),
                    "Positional superko was violated — a board position repeated.");
            }
            else
            {
                game.Pass();
            }
        }

        Assert.True(game.State is GameState.Scoring or GameState.Finished,
            "Computer game never ended (moves may have stalled).");
    }

    [Fact]
    public void FullGame_19x19_ComputerVsComputer_RunsCleanly()
    {
        var game = new GameManager(19, PlayerType.Computer, PlayerType.Computer);
        var rng = new Random(7);
        var hashes = new HashSet<string> { game.Board.PositionHash() };
        int moves = 0;

        while (game.State == GameState.Playing && moves++ < 1200)
        {
            var move = GoAI.ChooseMove(game.Board, game.CurrentPlayer, rng, game.MoveNumber, 361,
                isLegal: (r, c) => game.CanPlay(r, c).Success);
            if (move is GoPoint p)
            {
                var res = game.PlayStone(p.Row, p.Col);
                Assert.True(res.Success, $"AI played an illegal move at {p}: {res.Error}");
                Assert.True(hashes.Add(game.Board.PositionHash()),
                    "Positional superko was violated on the 19x19 board.");
            }
            else
            {
                game.Pass();
            }
        }

        // Smoke test: no exceptions, some moves made, superko held.
        Assert.True(moves > 20);
    }
}

public class NotationTests
{
    [Theory]
    [InlineData(0, 0, "A1")]
    [InlineData(3, 3, "D4")]
    [InlineData(0, 8, "J1")] // I is skipped
    [InlineData(9, 9, "K10")]
    [InlineData(18, 18, "T19")]
    public void ToNotation_FormatsIntersections(int row, int col, string expected)
    {
        Assert.Equal(expected, GoMoveFormatter.ToNotation(new GoPoint(row, col), 19));
    }

    [Fact]
    public void ToNotation_HandlesPassAndResign()
    {
        var pass = new GoMove { MoveNumber = 1, Color = StoneColor.Black, Kind = MoveKind.Pass };
        var resign = new GoMove { MoveNumber = 2, Color = StoneColor.White, Kind = MoveKind.Resign };
        var play = new GoMove { MoveNumber = 3, Color = StoneColor.Black, Kind = MoveKind.Play, Point = new GoPoint(2, 2) };

        Assert.Equal("Pass", pass.ToNotation());
        Assert.Equal("Resign", resign.ToNotation());
        Assert.Equal("C3", play.ToNotation());
    }
}
