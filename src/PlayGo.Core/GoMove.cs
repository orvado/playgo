namespace PlayGo.Core;

public enum MoveKind
{
    Play,
    Pass,
    Resign,
}

/// <summary>A single recorded move in a game.</summary>
public sealed class GoMove
{
    /// <summary>Move index (1-based), shared between both colors.</summary>
    public required int MoveNumber { get; init; }

    public required StoneColor Color { get; init; }

    public required MoveKind Kind { get; init; }

    public GoPoint? Point { get; init; }

    /// <summary>Stones of the opponent captured by this move (in board order).</summary>
    public IReadOnlyList<GoPoint> Captured { get; init; } = Array.Empty<GoPoint>();

    /// <summary>Total prisoners held by black after this move (white stones removed).</summary>
    public int BlackPrisoners { get; init; }

    /// <summary>Total prisoners held by white after this move (black stones removed).</summary>
    public int WhitePrisoners { get; init; }

    /// <summary>Hash of the whole board position after this move (used for ko / superko).</summary>
    public string BoardPositionHash { get; init; } = "";
}

public static class GoMoveFormatter
{
    /// <summary>
    /// Formats a move as Go notation, e.g. "D4", "K10", "Pass", or "Resign".
    /// Columns use A–T skipping I; rows are numbered from the top starting at 1.
    /// </summary>
    public static string ToNotation(this GoMove move)
    {
        if (move.Kind == MoveKind.Pass) return "Pass";
        if (move.Kind == MoveKind.Resign) return "Resign";
        if (move.Point is null) return "?";
        return ToNotation(move.Point.Value, size: -1);
    }

    public static string ToNotation(GoPoint point, int size)
    {
        int col = point.Col;
        int row = point.Row;
        char letter = col >= 8 ? (char)('A' + col + 1) : (char)('A' + col);
        return $"{letter}{row + 1}";
    }
}
