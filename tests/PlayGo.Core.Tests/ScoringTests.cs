using PlayGo.Core;

namespace PlayGo.Core.Tests;

public class ScoringTests
{
    private static readonly (int, int)[] Ring =
    {
        (2, 2), (2, 3), (2, 4), (2, 5),
        (3, 2), (4, 2), (5, 2),
        (5, 3), (5, 4), (5, 5),
        (3, 5), (4, 5),
    };

    /// <summary>White corner stones bound the ring so the outside region is dame.</summary>
    private static readonly (int, int)[] Corners = { (0, 0), (0, 8), (8, 0), (8, 8) };

    private static GoBoard RingBoard()
    {
        var board = new GoBoard(9);
        foreach (var (r, c) in Ring)
            board.ApplyMove(r, c, StoneColor.Black);
        foreach (var (r, c) in Corners)
            board.ApplyMove(r, c, StoneColor.White);
        return board;
    }

    [Fact]
    public void AreaScore_EnclosedTerritory_CountsStonesPlusTerritory()
    {
        var board = RingBoard();

        var score = board.CountScore(new HashSet<GoPoint>(), 0);

        Assert.Equal(4, score.BlackTerritory);   // (3,3),(3,4),(4,3),(4,4)
        Assert.Equal(12, score.BlackStones);
        Assert.Equal(16, score.BlackScore, 3);
        Assert.Equal(4, score.WhiteStones);
        Assert.Equal(StoneColor.Black, score.Winner);
        Assert.Equal(61, score.NeutralPoints);
    }

    [Fact]
    public void DeadStone_Marking_TransfersScoreToSurroundingColor()
    {
        var board = RingBoard();
        board.ApplyMove(3, 3, StoneColor.White); // white stone inside the ring

        // While the stone is live it borders the interior, so the points next
        // to it are dame; the stone itself still scores for white.
        var live = board.CountScore(new HashSet<GoPoint>(), 0);
        Assert.Equal(0, live.BlackTerritory);
        Assert.Equal(1 + 4, live.WhiteStones);  // inside stone + corners
        Assert.Equal(12, live.BlackScore, 3);
        Assert.Equal(5, live.WhiteScore, 3);

        // Marked dead, its point becomes black territory.
        var dead = board.CountScore(new HashSet<GoPoint> { new(3, 3) }, 0);
        Assert.Equal(4, dead.BlackTerritory);
        Assert.Equal(4, dead.WhiteStones);
        Assert.Equal(16, dead.BlackScore, 3);
        Assert.Equal(1, dead.DeadWhite);
    }

    [Fact]
    public void Komi_CanDecideAWinner()
    {
        var board = new GoBoard(9); // empty board

        var score = board.CountScore(new HashSet<GoPoint>(), 7.5);

        Assert.Equal(0, score.BlackScore, 3);
        Assert.Equal(7.5, score.WhiteScore, 3);
        Assert.Equal(StoneColor.White, score.Winner);
    }

    [Fact]
    public void FullBlackBoard_HasNoTerritoryButAllStones()
    {
        var cells = new StoneColor[19, 19];
        for (int r = 0; r < 19; r++)
            for (int c = 0; c < 19; c++)
                cells[r, c] = StoneColor.Black;
        var board = GoBoard.FromCells(19, cells, 0, 0);

        var score = board.CountScore(new HashSet<GoPoint>(), 0);

        Assert.Equal(0, score.BlackTerritory);
        Assert.Equal(361, score.BlackStones);
        Assert.Equal(361, score.BlackScore, 3);
        Assert.Equal(0, score.WhiteScore, 3);
    }

    [Fact]
    public void OpenBoard_PointsAreNeutralDame()
    {
        var board = new GoBoard(9);
        board.ApplyMove(0, 0, StoneColor.Black);
        board.ApplyMove(8, 8, StoneColor.White);

        var score = board.CountScore(new HashSet<GoPoint>(), 0);

        Assert.Equal(79, score.NeutralPoints);
        Assert.Equal(1, score.BlackStones);
        Assert.Equal(1, score.WhiteStones);
        Assert.Equal(0, score.BlackTerritory);
        Assert.Equal(0, score.WhiteTerritory);
        Assert.Equal(81, score.TotalPoints);
    }
}
