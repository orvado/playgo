namespace PlayGo.Core;

/// <summary>
/// A lightweight tactical Go engine. It evaluates every legal move with a
/// hand-tuned heuristic: captures are strongly preferred, atari'd groups are
/// rescued, connections and center play are rewarded, and self-atari blunders
/// are avoided. Good enough for a friendly club-level opponent.
/// </summary>
public static class GoAI
{
    /// <summary>
    /// Picks a move for <paramref name="color"/>. Returns null only when no
    /// legal move exists anywhere on the board.
    /// </summary>
    /// <param name="isLegal">
    /// Optional extra legality filter (e.g. ko/superko enforcement that lives
    /// outside the raw board rules). Return false to exclude a candidate.
    /// </param>
    public static GoPoint? ChooseMove(
        GoBoard board,
        StoneColor color,
        Random rng,
        int moveNumber = 0,
        int maxCandidates = 361,
        Func<int, int, bool>? isLegal = null)
    {
        if (board is null) return null;

        int best = int.MinValue;
        GoPoint? bestMove = null;
        var ties = new List<GoPoint>();
        int evaluated = 0;

        for (int r = 0; r < board.Size; r++)
        {
            for (int c = 0; c < board.Size; c++)
            {
                if (board[r, c] != StoneColor.Empty) continue;
                if (evaluated++ >= maxCandidates) break;
                if (isLegal is not null && !isLegal(r, c)) continue;

                var res = board.EvaluateMove(r, c, color);
                if (!res.Success) continue;

                int score = ScoreMove(board, res, r, c, color, rng);
                if (score > best)
                {
                    best = score;
                    bestMove = new GoPoint(r, c);
                    ties.Clear();
                    ties.Add(bestMove.Value);
                }
                else if (score == best)
                {
                    ties.Add(new GoPoint(r, c));
                }
            }
            if (evaluated >= maxCandidates) break;
        }

        // Randomize among equally-good candidates for variety.
        if (ties.Count > 1 && rng.Next(100) < 40)
            return ties[rng.Next(ties.Count)];

        return bestMove;
    }

    private static int ScoreMove(
        GoBoard board,
        MoveResult res,
        int row, int col,
        StoneColor me,
        Random rng)
    {
        StoneColor opp = me.Opponent();
        int score = 0;

        // 1. Captures dominate everything else.
        if (res.CapturedCount > 0)
        {
            score += 160 + res.CapturedCount * 70;
        }
        else if (res.LibertiesAfter == 1)
        {
            // Playing into self-atari (group will have a single liberty).
            score -= 110;
        }

        // 2. Rescue own groups in atari: if this point is the last liberty of
        //    an adjacent friendly group, playing here connects and saves it.
        int savedGroups = 0;
        int extendedGroups = 0;
        var seenGroups = new HashSet<GoPoint>();
        foreach (var n in board.Neighbors(row, col))
        {
            if (board[n.Row, n.Col] != me) continue;
            if (!seenGroups.Add(n)) continue;
            var (group, libs) = board.GetGroupInfo(n.Row, n.Col);
            foreach (var g in group) seenGroups.Add(g);

            if (libs.Count == 1)
            {
                if (libs.Contains(new GoPoint(row, col))) savedGroups++;
            }
            else if (libs.Count <= 2)
            {
                extendedGroups++;
            }
        }
        score += savedGroups * 95;
        score += extendedGroups * 22;

        // 3. Threats: reducing an opponent group to one liberty is valuable.
        int atariThreats = 0;
        var seenOpp = new HashSet<GoPoint>();
        foreach (var n in board.Neighbors(row, col))
        {
            if (board[n.Row, n.Col] != opp) continue;
            if (!seenOpp.Add(n)) continue;
            var (group, libs) = board.GetGroupInfo(n.Row, n.Col);
            foreach (var g in group) seenOpp.Add(g);
            if (libs.Count == 2 && libs.Contains(new GoPoint(row, col)))
                atariThreats++;
        }
        score += atariThreats * 35;

        // 4. Proximity: building connected shapes beats scattering stones.
        int friends = 0;
        foreach (var n in board.Neighbors(row, col))
        {
            if (board[n.Row, n.Col] == me) friends += 2;
            else if (board[n.Row, n.Col] == opp) friends += 1;
        }
        score += friends * 8;

        // 5. Early game: prefer center and star points, avoid extreme corners.
        if (board.StoneCount(me) + board.StoneCount(opp) < 50)
        {
            double c = (board.Size - 1) / 2.0;
            int distFromCenter = (int)Math.Round(Math.Abs(row - c) + Math.Abs(col - c));
            score += Math.Max(0, 10 - distFromCenter);

            if (IsStarPoint(row, col, board.Size)) score += 14;

            if ((row <= 1 && col <= 1) ||
                (row <= 1 && col >= board.Size - 2) ||
                (row >= board.Size - 2 && col <= 1) ||
                (row >= board.Size - 2 && col >= board.Size - 2))
            {
                score -= 20;
            }
        }

        // 6. Random jitter keeps games from being identical.
        score += rng.Next(0, 14);
        return score;
    }

    private static bool IsStarPoint(int row, int col, int size)
    {
        // Star points differ per board size (hoshi / tengen).
        var stars = size switch
        {
            9 => new[] { (2, 2), (2, 6), (6, 2), (6, 6), (4, 4) },
            13 => new[] { (3, 3), (3, 9), (9, 3), (9, 9), (6, 6), (3, 6), (6, 3), (6, 9), (9, 6) },
            _ => new[] { (3, 3), (3, 9), (3, 15), (9, 3), (9, 9), (9, 15), (15, 3), (15, 9), (15, 15) },
        };
        foreach (var (r, c) in stars)
            if (r == row && c == col) return true;
        return false;
    }
}
