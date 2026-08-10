using PlayGo.Core;

namespace PlayGo.Core.Tests;

public class GoBoardRulesTests
{
    [Fact]
    public void ApplyMove_OccupiedPoint_FailsAndLeavesBoardUntouched()
    {
        var board = new GoBoard(9);
        board.ApplyMove(3, 3, StoneColor.Black);

        var result = board.ApplyMove(3, 3, StoneColor.White);

        Assert.False(result.Success);
        Assert.Equal(StoneColor.Black, board[3, 3]);
        Assert.Equal(0, board.WhitePrisoners);
    }

    [Fact]
    public void ApplyMove_OutOfBounds_Fails()
    {
        var board = new GoBoard(9);
        Assert.False(board.ApplyMove(-1, 3, StoneColor.Black).Success);
        Assert.False(board.ApplyMove(3, 9, StoneColor.Black).Success);
        Assert.False(board.ApplyMove(9, 0, StoneColor.Black).Success);
    }

    [Fact]
    public void ApplyMove_CapturesSingleStone()
    {
        var board = new GoBoard(9);
        board.ApplyMove(0, 1, StoneColor.Black);
        board.ApplyMove(1, 0, StoneColor.Black);
        board.ApplyMove(1, 2, StoneColor.Black);
        board.ApplyMove(1, 1, StoneColor.White); // only liberty is (2,1)

        var result = board.ApplyMove(2, 1, StoneColor.Black);

        Assert.True(result.Success);
        Assert.Equal(StoneColor.Empty, board[1, 1]);
        Assert.Equal(1, board.BlackPrisoners);
        Assert.Single(result.Captured);
        Assert.Equal(new GoPoint(1, 1), result.Captured[0]);
    }

    [Fact]
    public void ApplyMove_CapturesEntireGroup()
    {
        var board = new GoBoard(9);
        board.ApplyMove(0, 1, StoneColor.Black);
        board.ApplyMove(0, 2, StoneColor.Black);
        board.ApplyMove(1, 0, StoneColor.Black);
        board.ApplyMove(1, 3, StoneColor.Black);
        board.ApplyMove(2, 1, StoneColor.Black);
        board.ApplyMove(1, 1, StoneColor.White);
        board.ApplyMove(1, 2, StoneColor.White); // group liberty is (2,2)

        var result = board.ApplyMove(2, 2, StoneColor.Black);

        Assert.True(result.Success);
        Assert.Equal(StoneColor.Empty, board[1, 1]);
        Assert.Equal(StoneColor.Empty, board[1, 2]);
        Assert.Equal(2, result.Captured.Count);
        Assert.Equal(2, board.BlackPrisoners);
    }

    [Fact]
    public void ApplyMove_CapturesTwoSeparateGroupsAtOnce()
    {
        var board = new GoBoard(9);
        // Two white groups whose last liberty is both (0,2).
        board.ApplyMove(0, 0, StoneColor.Black);
        board.ApplyMove(1, 1, StoneColor.Black);
        board.ApplyMove(1, 3, StoneColor.Black);
        board.ApplyMove(2, 2, StoneColor.Black);
        board.ApplyMove(0, 1, StoneColor.White); // only liberty (0,2)
        board.ApplyMove(1, 2, StoneColor.White); // liberties (0,2),(2,2)

        var result = board.ApplyMove(0, 2, StoneColor.Black);

        Assert.True(result.Success);
        Assert.Equal(2, result.Captured.Count);
        Assert.Equal(StoneColor.Empty, board[0, 1]);
        Assert.Equal(StoneColor.Empty, board[1, 2]);
        Assert.Equal(2, board.BlackPrisoners);
    }

    [Fact]
    public void ApplyMove_CapturesCornerStone()
    {
        var board = new GoBoard(9);
        board.ApplyMove(0, 1, StoneColor.Black);
        board.ApplyMove(0, 0, StoneColor.White); // corner, liberty (1,0)

        var result = board.ApplyMove(1, 0, StoneColor.Black);

        Assert.True(result.Success);
        Assert.Equal(StoneColor.Empty, board[0, 0]);
        Assert.Equal(1, board.BlackPrisoners);
    }

    [Fact]
    public void ApplyMove_ConnectedGroupHasSharedLiberties()
    {
        var board = new GoBoard(9);
        board.ApplyMove(1, 1, StoneColor.Black);
        board.ApplyMove(1, 2, StoneColor.Black);

        var (group, liberties) = board.GetGroupInfo(1, 1);

        Assert.Equal(2, group.Count);
        Assert.Contains(new GoPoint(1, 1), group);
        Assert.Contains(new GoPoint(1, 2), group);
        // Liberties: (0,1),(0,2),(2,1),(2,2),(1,0),(1,3)
        Assert.Equal(6, liberties.Count);
        Assert.Equal(6, board.LibertyCount(1, 1));
    }

    [Fact]
    public void ApplyMove_SuicideIsIllegal()
    {
        var board = new GoBoard(9);
        board.ApplyMove(0, 1, StoneColor.White);
        board.ApplyMove(1, 0, StoneColor.White);
        board.ApplyMove(1, 2, StoneColor.White);
        board.ApplyMove(2, 1, StoneColor.White);

        var result = board.ApplyMove(1, 1, StoneColor.Black);

        Assert.False(result.Success);
        Assert.Contains("Suicide", result.Error);
        Assert.Equal(StoneColor.Empty, board[1, 1]);
    }

    [Fact]
    public void ApplyMove_SuicideThatCapturesIsLegal()
    {
        var board = new GoBoard(9);
        board.ApplyMove(0, 1, StoneColor.Black);
        board.ApplyMove(1, 0, StoneColor.Black);
        board.ApplyMove(1, 2, StoneColor.Black);
        board.ApplyMove(1, 1, StoneColor.White); // in atari at (2,1)

        // Black plays (2,1): captures white, legal even though the new stone
        // would have had no liberties without the capture.
        var result = board.ApplyMove(2, 1, StoneColor.Black);

        Assert.True(result.Success);
        Assert.Equal(StoneColor.Empty, board[1, 1]);
        Assert.Equal(1, board.BlackPrisoners);
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var board = new GoBoard(9);
        board.ApplyMove(3, 3, StoneColor.Black);

        var clone = board.Clone();
        clone.ApplyMove(3, 4, StoneColor.Black);

        Assert.Equal(StoneColor.Black, board[3, 3]);
        Assert.Equal(StoneColor.Empty, board[3, 4]);
    }

    [Fact]
    public void PositionHash_IsStableAndOrderIndependent()
    {
        var a = new GoBoard(9);
        a.ApplyMove(1, 1, StoneColor.Black);
        a.ApplyMove(2, 2, StoneColor.White);

        var b = new GoBoard(9);
        b.ApplyMove(2, 2, StoneColor.White);
        b.ApplyMove(1, 1, StoneColor.Black);

        Assert.Equal(a.PositionHash(), b.PositionHash());
        Assert.Equal(a.PositionHash(), a.PositionHash());

        b.ApplyMove(3, 3, StoneColor.White);
        Assert.NotEqual(a.PositionHash(), b.PositionHash());
    }

    [Fact]
    public void EvaluateMove_DoesNotMutateBoard()
    {
        var board = new GoBoard(9);
        board.ApplyMove(3, 3, StoneColor.Black);
        string before = board.PositionHash();

        var result = board.EvaluateMove(3, 4, StoneColor.Black);

        Assert.True(result.Success);
        Assert.Equal(before, board.PositionHash());
        Assert.Equal(StoneColor.Empty, board[3, 4]);
    }
}

