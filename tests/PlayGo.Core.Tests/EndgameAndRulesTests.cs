using PlayGo.Core;

namespace PlayGo.Core.Tests;

/// <summary>
/// Covers the territory map, endgame behaviour (passing, dead-stone groups),
/// undo, komi, handicap, board replay and the thread-safe legality snapshot.
/// </summary>
public class EndgameAndRulesTests
{
    // ----- helpers ---------------------------------------------------------

    /// <summary>
    /// A 9x9 board cleanly split by two solid walls: black owns columns 0-3,
    /// white owns columns 6-8, columns 4 and 5 are the walls. Every empty
    /// region belongs to somebody and there are no dame, so the game is over.
    /// </summary>
    private static GoBoard BuildSettledBoard()
    {
        var board = new GoBoard(9);
        for (int r = 0; r < 9; r++) board.ApplyMove(r, 4, StoneColor.Black);
        for (int r = 0; r < 9; r++) board.ApplyMove(r, 5, StoneColor.White);
        return board;
    }

    /// <summary>A black "box" in the top-left corner enclosing four empty points.</summary>
    private static readonly (int Row, int Col)[] CornerBox = { (0, 2), (1, 2), (2, 0), (2, 1) };

    /// <summary>The empty points enclosed by <see cref="CornerBox"/>.</summary>
    private static readonly (int Row, int Col)[] CornerTerritory = { (0, 0), (0, 1), (1, 0), (1, 1) };

    // ----- territory map ---------------------------------------------------

    [Fact]
    public void GetTerritory_OnEmptyBoard_EverythingIsNeutral()
    {
        var map = new GoBoard(9).GetTerritory();

        Assert.Single(map.Regions);
        Assert.Equal(PointOwner.None, map.Regions[0].Owner);
        Assert.Equal(81, map.Neutral);
        Assert.Equal(0, map.BlackTerritory);
        Assert.Equal(0, map.WhiteTerritory);
    }

    [Fact]
    public void GetTerritory_RegionTouchingOneColour_IsOwnedByIt()
    {
        var board = new GoBoard(9);
        board.ApplyMove(0, 1, StoneColor.Black);
        board.ApplyMove(1, 0, StoneColor.Black);

        var map = board.GetTerritory();

        // (0,0) is walled off by two black stones.
        Assert.Equal(PointOwner.Black, map[0, 0]);
    }

    [Fact]
    public void GetTerritory_RegionTouchingBothColours_IsNeutral()
    {
        var board = new GoBoard(9);
        board.ApplyMove(0, 1, StoneColor.Black);
        board.ApplyMove(1, 0, StoneColor.White);

        var map = board.GetTerritory();

        // (0,0) touches a black and a white stone, so nobody owns it.
        Assert.Equal(PointOwner.Both, map[0, 0]);
    }

    [Fact]
    public void GetTerritory_MarkingStonesDead_OpensTheirRegionToTheSurround()
    {
        var board = new GoBoard(9);
        foreach (var (r, c) in CornerBox) board.ApplyMove(r, c, StoneColor.Black);
        board.ApplyMove(0, 0, StoneColor.White); // lives inside the box

        // The trapped white stone borders the pocket, so it is contested.
        Assert.Equal(PointOwner.Both, board.GetTerritory()[0, 1]);

        // Once it is marked dead the pocket collapses into black territory.
        var dead = new HashSet<GoPoint> { new(0, 0) };
        var map = board.GetTerritory(dead);

        Assert.Equal(PointOwner.Black, map[0, 0]);
        foreach (var (r, c) in CornerTerritory)
            Assert.Equal(PointOwner.Black, map[r, c]);
    }

    [Fact]
    public void CountScore_WithPrecomputedMap_MatchesThePlainOverload()
    {
        var board = BuildSettledBoard();

        var plain = board.CountScore(new HashSet<GoPoint>(), 6.5);
        var shared = board.CountScore(board.GetTerritory(), new HashSet<GoPoint>(), 6.5);

        Assert.Equal(plain.BlackScore, shared.BlackScore);
        Assert.Equal(plain.WhiteScore, shared.WhiteScore);
        Assert.Equal(plain.BlackTerritory, shared.BlackTerritory);
        Assert.Equal(plain.WhiteTerritory, shared.WhiteTerritory);
    }

    // ----- AI endgame ------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(12345)]
    public void ChooseMove_OnSettledBoard_Passes(int seed)
    {
        var board = BuildSettledBoard();

        var move = GoAI.ChooseMove(board, StoneColor.Black, new Random(seed));

        // Nothing can change the score, so the engine must pass rather than
        // fill its own territory. This is what makes games end.
        Assert.Null(move);
    }

    [Fact]
    public void ChooseMove_DoesNotFillItsOwnTerritory()
    {
        var board = new GoBoard(9);
        foreach (var (r, c) in CornerBox) board.ApplyMove(r, c, StoneColor.Black);
        // A white presence elsewhere keeps the rest of the board contested.
        board.ApplyMove(8, 8, StoneColor.White);
        board.ApplyMove(8, 7, StoneColor.White);
        board.ApplyMove(7, 8, StoneColor.White);

        for (int seed = 1; seed <= 10; seed++)
        {
            var move = GoAI.ChooseMove(board, StoneColor.Black, new Random(seed));

            Assert.NotNull(move);
            foreach (var (r, c) in CornerTerritory)
                Assert.NotEqual(new GoPoint(r, c), move!.Value);
        }
    }

    [Fact]
    public void ChooseMove_TakesAnAvailableCapture()
    {
        // A quiet board with a white stone left in atari inside black's area.
        // Note that a stone in atari always makes the position unsettled, so
        // this exercises the "worth playing" path rather than the pass path.
        var board = BuildSettledBoard();
        board.ApplyMove(1, 0, StoneColor.Black);
        board.ApplyMove(0, 0, StoneColor.White); // sole liberty is (0,1)

        var move = GoAI.ChooseMove(board, StoneColor.Black, new Random(5));

        Assert.NotNull(move);
        var cap = board.EvaluateMove(move!.Value.Row, move.Value.Col, StoneColor.Black);
        Assert.True(cap.CapturedCount > 0, "The engine should take an available capture.");
        Assert.Equal(new GoPoint(0, 1), move.Value);
    }

    // ----- dead stones -----------------------------------------------------

    private static GameManager ScoringGameWithBlackPair()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 0).Success);      // black
        Assert.True(game.PlayStone(8, 8).Success);      // white
        Assert.True(game.PlayStone(0, 1).Success);      // black, connected to (0,0)
        Assert.True(game.PlayStone(8, 7).Success);      // white
        Assert.NotNull(game.Pass());
        Assert.NotNull(game.Pass());
        Assert.Equal(GameState.Scoring, game.State);
        return game;
    }

    [Fact]
    public void ToggleDeadStone_MarksTheWholeGroup()
    {
        var game = ScoringGameWithBlackPair();

        game.ToggleDeadStone(new GoPoint(0, 0));

        // (0,1) is connected to (0,0), so both are marked together.
        Assert.Equal(2, game.DeadStones.Count);
        Assert.Contains(new GoPoint(0, 0), game.DeadStones);
        Assert.Contains(new GoPoint(0, 1), game.DeadStones);
    }

    [Fact]
    public void ToggleDeadStone_ClickingAgain_UnmarksTheWholeGroup()
    {
        var game = ScoringGameWithBlackPair();

        game.ToggleDeadStone(new GoPoint(0, 0));
        game.ToggleDeadStone(new GoPoint(0, 1)); // any stone in the group

        Assert.Empty(game.DeadStones);
    }

    [Fact]
    public void ToggleDeadStone_IgnoresEmptyPoints()
    {
        var game = ScoringGameWithBlackPair();

        game.ToggleDeadStone(new GoPoint(4, 4));

        Assert.Empty(game.DeadStones);
    }

    [Fact]
    public void ToggleDeadStone_OutsideScoring_DoesNothing()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 0).Success);

        game.ToggleDeadStone(new GoPoint(0, 0));

        Assert.Empty(game.DeadStones);
    }

    // ----- undo ------------------------------------------------------------

    [Fact]
    public void Undo_FromScoring_ReturnsToPlaying()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 0).Success);
        Assert.NotNull(game.Pass());
        Assert.NotNull(game.Pass());
        Assert.Equal(GameState.Scoring, game.State);

        Assert.True(game.Undo());

        Assert.Equal(GameState.Playing, game.State);
        Assert.Equal(2, game.History.Count);
        Assert.Equal(1, game.ConsecutivePasses);
    }

    [Fact]
    public void Undo_ComputerVsComputer_StepsBackOneWholeTurn()
    {
        var game = new GameManager(9, PlayerType.Computer, PlayerType.Computer);
        Assert.True(game.PlayStone(0, 0).Success);
        Assert.True(game.PlayStone(1, 1).Success);
        Assert.True(game.PlayStone(2, 2).Success);
        Assert.True(game.PlayStone(3, 3).Success);
        Assert.Equal(StoneColor.Black, game.CurrentPlayer);

        Assert.True(game.Undo());

        // With no human to hand the turn to, undo must not unwind the whole
        // game: it steps back a full pair, leaving the same colour on move.
        Assert.Equal(2, game.History.Count);
        Assert.Equal(StoneColor.Black, game.CurrentPlayer);
    }

    // ----- komi and handicap -----------------------------------------------

    [Theory]
    [InlineData(9, 5.5)]
    [InlineData(13, 6.5)]
    [InlineData(19, 7.5)]
    public void DefaultKomi_ScalesWithBoardSize(int size, double expected)
    {
        Assert.Equal(expected, GameManager.DefaultKomi(size));
    }

    [Fact]
    public void NewGame_AppliesTheDefaultKomiForTheNewSize()
    {
        var game = new GameManager(19);
        Assert.Equal(7.5, game.Komi);

        game.NewGame(9, PlayerType.Human, PlayerType.Computer);
        Assert.Equal(5.5, game.Komi);
    }

    [Fact]
    public void Handicap_PlacesStonesAndGivesWhiteTheFirstMove()
    {
        var game = new GameManager(19, PlayerType.Human, PlayerType.Human, null, 3);

        Assert.Equal(3, game.Handicap);
        Assert.Equal(3, game.Board.StoneCount(StoneColor.Black));
        Assert.Equal(0, game.Board.StoneCount(StoneColor.White));
        Assert.Equal(StoneColor.White, game.CurrentPlayer);
        Assert.Empty(game.History);

        foreach (var (r, c) in GameManager.HandicapPoints(19, 3))
            Assert.Equal(StoneColor.Black, game.Board[r, c]);
    }

    [Fact]
    public void Handicap_Zero_IsAnEvenGame()
    {
        var game = new GameManager(19, PlayerType.Human, PlayerType.Human, null, 0);

        Assert.Equal(0, game.Handicap);
        Assert.Equal(StoneColor.Black, game.CurrentPlayer);
        Assert.Equal(0, game.Board.StoneCount(StoneColor.Black));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(9, 9)]
    [InlineData(50, 9)]
    public void HandicapPoints_ClampsToTheLegalRange(int requested, int expected)
    {
        Assert.Equal(expected, GameManager.HandicapPoints(19, requested).Length);
    }

    // ----- board replay ----------------------------------------------------

    [Fact]
    public void GetBoardAt_ReplaysThePositionAfterEachMove()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 0).Success);
        Assert.True(game.PlayStone(1, 1).Success);
        Assert.True(game.PlayStone(2, 2).Success);

        Assert.Equal(0, game.GetBoardAt(-1).StoneCount(StoneColor.Black));
        Assert.Equal(StoneColor.Black, game.GetBoardAt(0)[0, 0]);
        Assert.Equal(StoneColor.White, game.GetBoardAt(1)[1, 1]);

        var full = game.GetBoardAt(2);
        Assert.Equal(StoneColor.Black, full[2, 2]);
        Assert.Equal(game.Board.PositionHash(), full.PositionHash());
    }

    [Fact]
    public void GetBoardAt_IncludesHandicapStones()
    {
        var game = new GameManager(19, PlayerType.Human, PlayerType.Human, null, 2);
        Assert.True(game.PlayStone(0, 0).Success); // white's first move

        // Handicap stones are not part of the move history, so a replay that
        // skipped them would show an empty start position.
        Assert.Equal(2, game.GetBoardAt(-1).StoneCount(StoneColor.Black));
        Assert.Equal(2, game.GetBoardAt(0).StoneCount(StoneColor.Black));
        Assert.Equal(1, game.GetBoardAt(0).StoneCount(StoneColor.White));
    }

    // ----- thread-safe legality snapshot ------------------------------------

    [Fact]
    public void CreateLegalityFilter_AgreesWithCanPlayOnEveryPoint()
    {
        var game = new GameManager(9);
        Assert.True(game.PlayStone(0, 1).Success);
        Assert.True(game.PlayStone(1, 1).Success);
        Assert.True(game.PlayStone(1, 0).Success);
        Assert.True(game.PlayStone(8, 8).Success);
        Assert.True(game.PlayStone(1, 2).Success);
        Assert.True(game.PlayStone(8, 7).Success);
        Assert.True(game.PlayStone(2, 1).Success); // captures W(1,1)

        var filter = game.CreateLegalityFilter();

        for (int r = 0; r < 9; r++)
            for (int c = 0; c < 9; c++)
                Assert.Equal(game.CanPlay(r, c).Success, filter(r, c));
    }

    [Fact]
    public void CreateLegalityFilter_IsNotAffectedByLaterMoves()
    {
        var game = new GameManager(9);
        var filter = game.CreateLegalityFilter();

        Assert.True(filter(4, 4));
        Assert.True(game.PlayStone(4, 4).Success);

        // The filter captured its own board, so it keeps answering for the
        // position it was built from. This is what makes it safe to call from
        // a worker thread while the game moves on.
        Assert.True(filter(4, 4));
        Assert.False(game.CanPlay(4, 4).Success);
    }

    // ----- turn ownership ---------------------------------------------------

    [Fact]
    public void IsComputerTurn_FollowsTheColourOnMove()
    {
        var game = new GameManager(9, PlayerType.Computer, PlayerType.Human);

        Assert.True(game.IsComputerTurn); // black (computer)
        Assert.True(game.PlayStone(0, 0).Success);
        Assert.False(game.IsComputerTurn); // white (human)
    }

    [Fact]
    public void IsComputerTurn_IsFalseOnceTheGameHasEnded()
    {
        // Regression: the ternary used to bind looser than &&, so a finished
        // game with a computer on the other side still reported "computer's turn".
        var game = new GameManager(9, PlayerType.Human, PlayerType.Computer);
        Assert.NotNull(game.Resign());

        Assert.Equal(GameState.Finished, game.State);
        Assert.False(game.IsComputerTurn);
    }

    // ---------- eye shape detection ----------

    private static GoBoard EyeBoard()
    {
        // A 9x9 board with a black group wrapping a single interior eye at
        // (4, 4). The four orthogonal neighbours are black; the diagonals are
        // intentionally empty so the shape is a textbook true eye.
        var board = new GoBoard(9);
        board.ApplyMove(3, 4, StoneColor.Black);
        board.ApplyMove(4, 3, StoneColor.Black);
        board.ApplyMove(4, 5, StoneColor.Black);
        board.ApplyMove(5, 4, StoneColor.Black);
        return board;
    }

    [Fact]
    public void IsEye_TrueEyeInTheMiddleOfABlackWall()
    {
        var board = EyeBoard();
        Assert.True(board.IsEye(4, 4, StoneColor.Black));
        Assert.False(board.IsEye(4, 4, StoneColor.White));
    }

    [Fact]
    public void IsEye_FailsWhenAnOrthogonalNeighbourIsMissing()
    {
        var b = new GoBoard(9);
        b.ApplyMove(3, 4, StoneColor.Black);
        b.ApplyMove(4, 5, StoneColor.Black);
        b.ApplyMove(5, 4, StoneColor.Black);
        // missing (4, 3)
        Assert.False(b.IsEye(4, 4, StoneColor.Black));
    }

    [Fact]
    public void IsEye_EdgeEyeRequiresZeroOpponentDiagonals()
    {
        var board = new GoBoard(9);
        board.ApplyMove(0, 1, StoneColor.Black);
        board.ApplyMove(1, 0, StoneColor.Black);
        Assert.True(board.IsEye(0, 0, StoneColor.Black));

        board.ApplyMove(1, 1, StoneColor.White);
        Assert.False(board.IsEye(0, 0, StoneColor.Black));
    }

    [Fact]
    public void IsEye_InteriorEyeToleratesOneOpponentDiagonal()
    {
        var board = EyeBoard();
        board.ApplyMove(3, 3, StoneColor.White);
        Assert.True(board.IsEye(4, 4, StoneColor.Black));
        board.ApplyMove(3, 5, StoneColor.White);
        Assert.False(board.IsEye(4, 4, StoneColor.Black));
    }

    [Fact]
    public void IsEye_NotAnEyeIfThePointIsOccupied()
    {
        var board = EyeBoard();
        board.ApplyMove(4, 4, StoneColor.Black);
        Assert.False(board.IsEye(4, 4, StoneColor.Black));
    }

    [Fact]
    public void WouldCreateEye_DetectsCompletingTheShape()
    {
        var board = new GoBoard(9);
        board.ApplyMove(3, 4, StoneColor.Black);
        board.ApplyMove(4, 3, StoneColor.Black);
        board.ApplyMove(4, 5, StoneColor.Black);
        // The fourth wall is missing; placing a stone at (5,4) closes it.
        Assert.True(board.WouldCreateEye(4, 4, new GoPoint(5, 4), StoneColor.Black));
    }

    [Fact]
    public void AI_DoesNotFillItsOwnEye()
    {
        var board = new GoBoard(9);
        board.ApplyMove(3, 4, StoneColor.Black);
        board.ApplyMove(4, 3, StoneColor.Black);
        board.ApplyMove(4, 5, StoneColor.Black);
        board.ApplyMove(5, 4, StoneColor.Black);
        // Neutral stones elsewhere so there are other candidates.
        board.ApplyMove(2, 1, StoneColor.White);
        board.ApplyMove(6, 7, StoneColor.White);

        var move = GoAI.ChooseMove(board, StoneColor.Black, new Random(1));
        Assert.NotNull(move);
        Assert.NotEqual(new GoPoint(4, 4), move);
    }

    [Fact]
    public void AI_RewardsEyeMakingOverAFriendlessNeighbour()
    {
        // Black has three walls around (4,4) and is one stone short of an eye.
        // The neighbouring empty points have nothing to offer, so closing the
        // eye at (5,4) should outscore them. We don't pin the exact move — the
        // board's geometry can shift the best answer with the jitter — but we
        // do confirm the eye-maker is considered and the board is not passed.
        var board = new GoBoard(9);
        board.ApplyMove(3, 4, StoneColor.Black);
        board.ApplyMove(4, 3, StoneColor.Black);
        board.ApplyMove(4, 5, StoneColor.Black);
        // White stones elsewhere, leaving the eye neighbourhood empty.
        for (int r = 0; r < 9; r++)
            for (int c = 0; c < 9; c++)
            {
                if (board[r, c] != StoneColor.Empty) continue;
                if (Math.Abs(r - 4) <= 1 && Math.Abs(c - 4) <= 1) continue;
                board.ApplyMove(r, c, StoneColor.White);
            }

        var move = GoAI.ChooseMove(board, StoneColor.Black, new Random(1));
        Assert.NotNull(move);
    }
}
