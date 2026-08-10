using PlayGo.Core;

namespace PlayGo.Core.Tests;

/// <summary>
/// A canonical ko shape:
///   . W .
///   W B W      black at (1,1) surrounded by white at (0,1),(1,0),(2,1)
///   . B B      black blocks at (0,2),(2,2),(1,3); white captures at (1,2)
/// The capturing white stone ends up in atari at (1,1), so black could
/// recapture — which is exactly what the ko rule forbids immediately.
/// </summary>
public class KoAndSuperkoTests
{
    private static void PlayKoSetup(GameManager game)
    {
        Assert.True(game.PlayStone(1, 1).Success); // B at ko center
        Assert.True(game.PlayStone(0, 1).Success); // W
        Assert.True(game.PlayStone(0, 2).Success); // B
        Assert.True(game.PlayStone(1, 0).Success); // W
        Assert.True(game.PlayStone(2, 2).Success); // B
        Assert.True(game.PlayStone(2, 1).Success); // W
        Assert.True(game.PlayStone(1, 3).Success); // B
    }

    [Fact]
    public void KoCapture_ThenImmediateRecapture_IsIllegal()
    {
        var game = new GameManager(9);
        PlayKoSetup(game);

        // White captures the ko stone.
        var capture = game.PlayStone(1, 2);
        Assert.True(capture.Success);
        Assert.Equal(StoneColor.Empty, game.Board[1, 1]);
        Assert.Equal(1, game.WhitePrisoners);

        // Black may not immediately recapture.
        var recapture = game.PlayStone(1, 1);
        Assert.False(recapture.Success);
        Assert.Contains("Ko", recapture.Error);
        Assert.Equal(StoneColor.Empty, game.Board[1, 1]);
    }

    [Fact]
    public void Ko_AfterTenukiMoves_RecaptureIsLegal()
    {
        var game = new GameManager(9);
        PlayKoSetup(game);
        Assert.True(game.PlayStone(1, 2).Success); // W captures
        Assert.False(game.PlayStone(1, 1).Success); // immediate recapture blocked

        // Both players play elsewhere…
        Assert.True(game.PlayStone(8, 8).Success);
        Assert.True(game.PlayStone(7, 7).Success);

        // …then the ko can be recaptured (the full-board position is new).
        var recapture = game.PlayStone(1, 1);
        Assert.True(recapture.Success);
        Assert.Equal(1, recapture.CapturedCount);
        Assert.Equal(1, game.BlackPrisoners);
        Assert.Equal(1, game.WhitePrisoners);
    }

    [Fact]
    public void PositionalSuperko_TripleKoCycle_IsForbidden()
    {
        // Three independent ko fights let the position return to an earlier
        // one after more than two plies — the exact case where positional
        // superko (but not the simple one-move ko rule) forbids the move.
        //
        //   Ko A (white captures black): B(1,1); W(0,1),(1,0),(2,1); B(0,2),(2,2),(1,3); capture (1,2)
        //   Ko B (black captures white): W(5,3); B(4,3),(5,2),(6,3); W(4,4),(5,5),(6,4); capture (5,4)
        //   Ko C (white captures black): B(7,7); W(8,7),(7,8),(6,7); B(8,6),(6,6),(7,5); capture (7,6)
        var game = new GameManager(9);

        foreach (var (r, c) in new[]
        {
            (1, 1), (0, 1), (0, 2), (1, 0), (2, 2), (2, 1), (1, 3), // ko A
            (5, 3), (4, 3), (4, 4), (5, 2), (5, 5), (6, 3), (6, 4), // ko B
            (7, 7), (8, 7), (8, 6), (7, 8), (6, 6), (6, 7), (7, 5), // ko C
            (8, 8), (0, 8), // filler stones so white is to move at P0
        })
        {
            var res = game.PlayStone(r, c);
            Assert.True(res.Success, $"Setup move at ({r},{c}) failed: {res.Error}");
        }
        Assert.Equal(StoneColor.White, game.CurrentPlayer);

        // Full triple-ko cycle. Each move is a capture in one of the three kos:
        //   W takes A → P1, B takes B → P2, W takes C → P3,
        //   B retakes A → P4, W retakes B → P5, B retakes C → P6 = P0.
        // The last move recreates the setup position six plies earlier, so the
        // one-move ko rule would allow it — positional superko must not.
        Assert.True(game.PlayStone(1, 2).Success); // P1
        Assert.True(game.PlayStone(5, 4).Success); // P2
        Assert.True(game.PlayStone(7, 6).Success); // P3
        Assert.True(game.PlayStone(1, 1).Success); // P4
        Assert.True(game.PlayStone(5, 3).Success); // P5
        string p5Hash = game.Board.PositionHash();

        var cycle = game.PlayStone(7, 7); // P6: restores P0
        Assert.False(cycle.Success);
        Assert.Contains("Superko", cycle.Error);
        Assert.Equal(p5Hash, game.Board.PositionHash()); // board unchanged

        // The game remains playable afterwards.
        Assert.True(game.CanPlay(0, 0).Success);
    }
}
