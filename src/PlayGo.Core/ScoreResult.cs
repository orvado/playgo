namespace PlayGo.Core;

/// <summary>The result of area scoring, in points.</summary>
public sealed class ScoreResult
{
    public ScoreResult(
        double blackScore, double whiteScore, double komi,
        int blackTerritory, int whiteTerritory,
        int blackStones, int whiteStones,
        int deadBlack, int deadWhite, int neutralPoints)
    {
        BlackScore = blackScore;
        WhiteScore = whiteScore;
        Komi = komi;
        BlackTerritory = blackTerritory;
        WhiteTerritory = whiteTerritory;
        BlackStones = blackStones;
        WhiteStones = whiteStones;
        DeadBlack = deadBlack;
        DeadWhite = deadWhite;
        NeutralPoints = neutralPoints;
    }

    public double BlackScore { get; }

    public double WhiteScore { get; }

    public double Komi { get; }

    public int BlackTerritory { get; }

    public int WhiteTerritory { get; }

    public int BlackStones { get; }

    public int WhiteStones { get; }

    public int DeadBlack { get; }

    public int DeadWhite { get; }

    /// <summary>Neutral empty points (dame) surrounded by both colors.</summary>
    public int NeutralPoints { get; }

    public double TotalPoints => BlackTerritory + WhiteTerritory + BlackStones + WhiteStones + NeutralPoints;

    public StoneColor? Winner => BlackScore > WhiteScore
        ? StoneColor.Black
        : WhiteScore > BlackScore
            ? StoneColor.White
            : null;

    /// <summary>Positive margin of the winner (0 on a draw).</summary>
    public double Margin => Math.Abs(BlackScore - WhiteScore);
}
