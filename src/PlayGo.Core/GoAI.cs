namespace PlayGo.Core;

/// <summary>
/// A lightweight tactical Go engine. It evaluates every legal move with a
/// hand-tuned heuristic: captures are strongly preferred, atari'd groups are
/// rescued, connections and center play are rewarded, and self-atari blunders
/// are avoided. Good enough for a friendly club-level opponent.
///
/// On top of the tactics it understands <em>empty regions</em>: filling your own
/// territory is heavily penalised, contesting the opponent's area is rewarded,
/// and once every region belongs to somebody the engine passes. That last part
/// is what makes a game actually end — without it the engine will happily play
/// thousands of meaningless moves into its own territory.
/// </summary>
public static class GoAI
{
    /// <summary>
    /// Picks a move for <paramref name="color"/>. Returns null when the engine
    /// wants to pass, either because nothing is worth playing (the position is
    /// settled) or because no legal move exists anywhere on the board.
    /// </summary>
    /// <param name="isLegal">
    /// Optional extra legality filter (e.g. ko/superko enforcement that lives
    /// outside the raw board rules). Return false to exclude a candidate.
    /// Called from whichever thread invokes this method — keep it thread-safe.
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

        // Classify each empty region once; every candidate move reuses this.
        var territory = board.GetTerritory();

        // When the board is settled, only moves that change the score (captures
        // and rescues) are worth making. Everything else is a pass.
        bool settled = IsSettled(board, territory);

        int best = int.MinValue;
        GoPoint? bestMove = null;
        var ties = new List<GoPoint>();
        int evaluated = 0;

        for (int r = 0; r < board.Size; r++)
        {
            for (int c = 0; c < board.Size; c++)
            {
                if (board[r, c] != StoneColor.Empty) continue;
                if (isLegal is not null && !isLegal(r, c)) continue;
                if (evaluated++ >= maxCandidates) break;

                var res = board.EvaluateMove(r, c, color);
                if (!res.Success) continue;

                var (score, worthPlaying) = ScoreMove(board, res, r, c, color, territory, rng);

                // On a settled board, skip anything that cannot change the score.
                if (settled && !worthPlaying) continue;

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

        if (bestMove is null) return null;

        // Randomize among equally-good candidates for variety.
        if (ties.Count > 1 && rng.Next(100) < 40)
            return ties[rng.Next(ties.Count)];

        return bestMove;
    }

    /// <summary>
    /// True when every empty region is unambiguously somebody's territory, so
    /// there is nothing left to contest and both players should pass.
    /// </summary>
    private static bool IsSettled(GoBoard board, TerritoryMap map)
    {
        // Don't pass while there is anything left to contest. This is the real
        // end-of-game condition: the dame are filled and every remaining empty
        // point unambiguously belongs to one side, so no move can change the
        // score and both players should pass.
        foreach (var region in map.Regions)
        {
            if (region.Owner is PointOwner.None or PointOwner.Both) return false;
        }

        // Guard against a freak early position where one colour happens to
        // enclose the whole board: both sides must have a real presence.
        if (board.StoneCount(StoneColor.Black) < 4 || board.StoneCount(StoneColor.White) < 4)
            return false;

        return true;
    }

    /// <summary>
    /// Scores a single candidate move. Also reports whether the move is
    /// <c>worthPlaying</c> — i.e. whether it can change the final score, as
    /// opposed to filling your own territory or a neutral dame point.
    /// </summary>
    private static (int Score, bool WorthPlaying) ScoreMove(
        GoBoard board,
        MoveResult res,
        int row, int col,
        StoneColor me,
        TerritoryMap territory,
        Random rng)
    {
        StoneColor opp = me.Opponent();
        int score = 0;
        bool worthPlaying = false;

        // 1. Captures dominate everything else.
        if (res.CapturedCount > 0)
        {
            score += 160 + res.CapturedCount * 70;
            worthPlaying = true;
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
        if (savedGroups > 0) worthPlaying = true;

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

        // 4. Shape: what kind of empty region is this point part of?
        var owner = territory[row, col];
        var mine = me == StoneColor.Black ? PointOwner.Black : PointOwner.White;
        var theirs = me == StoneColor.Black ? PointOwner.White : PointOwner.Black;

        if (owner == mine)
        {
            // Filling your own territory is score-neutral under area scoring and
            // throws the move away — and filling your own eyes is outright harmful.
            if (!worthPlaying) score -= 150;
        }
        else if (owner == theirs)
        {
            // Invasion / reduction: contest the opponent's area.
            score += 30;
        }

        // 4.5 Eyes: filling your own eye almost always loses the group; making
        //     a new eye almost always keeps it alive.
        bool isOwnEye = board.IsEye(row, col, me);
        if (isOwnEye && !worthPlaying) score -= 350;
        if (!isOwnEye)
        {
            int createdEyes = 0;
            foreach (var n in board.Neighbors(row, col))
            {
                if (board[n.Row, n.Col] != StoneColor.Empty) continue;
                if (board.WouldCreateEye(n.Row, n.Col, new GoPoint(row, col), me))
                    createdEyes++;
            }
            if (createdEyes > 0)
            {
                score += createdEyes * 50;
                worthPlaying = true;
            }
        }

        // 5. Proximity: building connected shapes beats scattering stones.
        int friends = 0;
        foreach (var n in board.Neighbors(row, col))
        {
            if (board[n.Row, n.Col] == me) friends += 2;
            else if (board[n.Row, n.Col] == opp) friends += 1;
        }
        score += friends * 8;

        // 6. Early game: prefer center and star points, avoid extreme corners.
        int totalStones = board.StoneCount(me) + board.StoneCount(opp);
        if (totalStones < board.Size * board.Size / 4)
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

        // 7. A little jitter keeps games from being identical. It stays small
        //    relative to the positional terms so it doesn't drown out the signal.
        score += rng.Next(0, 6);
        return (score, worthPlaying);
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
