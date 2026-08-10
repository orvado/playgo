using PlayGo.Core;

namespace PlayGo.Core.Tests;

public class GameFlowTests
{
    [Fact]
    public void TwoConsecutivePasses_EnterScoring_ThenCountScore_FinishesGame()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 0).Success);
        Assert.True(game.PlayStone(1, 1).Success);

        Assert.NotNull(game.Pass());
        Assert.Equal(GameState.Playing, game.State); // only one pass so far

        Assert.NotNull(game.Pass());
        Assert.Equal(GameState.Scoring, game.State);

        var score = game.CountScore();
        Assert.NotNull(score);
        Assert.Equal(GameState.Finished, game.State);
        Assert.NotNull(game.Winner);
    }

    [Fact]
    public void Resign_EndsGameWithOpponentAsWinner()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 0).Success);

        Assert.NotNull(game.Resign()); // white resigns

        Assert.Equal(GameState.Finished, game.State);
        Assert.Equal(StoneColor.Black, game.Winner);
    }

    [Fact]
    public void Undo_RestoresBoardTurnAndPrisoners()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 1).Success);
        Assert.True(game.PlayStone(1, 1).Success);
        Assert.True(game.PlayStone(1, 0).Success);
        Assert.True(game.PlayStone(8, 8).Success); // tenuki
        Assert.True(game.PlayStone(1, 2).Success);
        Assert.True(game.PlayStone(8, 7).Success); // tenuki
        Assert.True(game.PlayStone(2, 1).Success); // black captures W(1,1)

        Assert.Equal(1, game.BlackPrisoners);
        Assert.Equal(StoneColor.White, game.CurrentPlayer);
        Assert.Equal(7, game.MoveNumber);

        Assert.True(game.Undo());

        Assert.Equal(StoneColor.Black, game.CurrentPlayer);
        Assert.Equal(0, game.BlackPrisoners);
        Assert.Equal(StoneColor.White, game.Board[1, 1]);
        Assert.Equal(StoneColor.Empty, game.Board[2, 1]);
        Assert.Equal(6, game.MoveNumber);
        Assert.Equal(6, game.History.Count);
    }

    [Fact]
    public void Undo_AgainstComputer_UndoesBothMoves()
    {
        var game = new GameManager(9, PlayerType.Human, PlayerType.Computer);
        Assert.True(game.PlayStone(0, 0).Success); // human black
        Assert.True(game.PlayStone(1, 1).Success); // computer white

        Assert.Equal(StoneColor.Black, game.CurrentPlayer);
        Assert.Equal(2, game.History.Count);

        Assert.True(game.Undo());

        Assert.Empty(game.History);
        Assert.Equal(StoneColor.Black, game.CurrentPlayer);
    }

    [Fact]
    public void Undo_OnEmptyHistory_Fails()
    {
        var game = new GameManager(9);
        Assert.False(game.Undo());
    }

    [Fact]
    public void PlayOnOccupiedPoint_FailsAndKeepsTurn()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(3, 3).Success);
        Assert.Equal(StoneColor.White, game.CurrentPlayer);

        var result = game.PlayStone(3, 3);
        Assert.False(result.Success);
        Assert.Equal(StoneColor.White, game.CurrentPlayer);
        Assert.Equal(1, game.MoveNumber);
    }

    [Fact]
    public void NewGame_ResetsEverything()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 0).Success);
        Assert.NotNull(game.Pass());
        Assert.NotNull(game.Pass());
        Assert.Equal(GameState.Scoring, game.State);

        game.NewGame(13, PlayerType.Human, PlayerType.Computer);

        Assert.Equal(13, game.BoardSize);
        Assert.Equal(0, game.MoveNumber);
        Assert.Equal(GameState.Playing, game.State);
        Assert.Equal(StoneColor.Black, game.CurrentPlayer);
        Assert.Empty(game.History);
        Assert.False(game.Undo());
    }

    [Fact]
    public void MoveHistory_RecordsCapturesAndPrisoners()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 1).Success);
        Assert.True(game.PlayStone(1, 1).Success);
        Assert.True(game.PlayStone(1, 0).Success);
        Assert.True(game.PlayStone(8, 8).Success);
        Assert.True(game.PlayStone(1, 2).Success);
        Assert.True(game.PlayStone(8, 7).Success);
        Assert.True(game.PlayStone(2, 1).Success); // captures W(1,1)

        var last = game.History[^1];
        Assert.Equal(MoveKind.Play, last.Kind);
        Assert.Equal(new GoPoint(2, 1), last.Point);
        Assert.Single(last.Captured);
        Assert.Equal(1, last.BlackPrisoners);
        Assert.Equal(7, last.MoveNumber);
    }

    [Fact]
    public void ResumePlay_LeavesScoringAndClearsDeadMarks()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 0).Success);
        Assert.NotNull(game.Pass());
        Assert.NotNull(game.Pass());

        game.ToggleDeadStone(new GoPoint(0, 0));
        Assert.Single(game.DeadStones);

        game.ResumePlay();

        Assert.Equal(GameState.Playing, game.State);
        Assert.Empty(game.DeadStones);
        Assert.Equal(0, game.ConsecutivePasses);
    }

    [Fact]
    public void PlayAfterGameEnd_IsRejected()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 0).Success);
        Assert.NotNull(game.Resign());

        var result = game.PlayStone(1, 1);
        Assert.False(result.Success);
    }
}
