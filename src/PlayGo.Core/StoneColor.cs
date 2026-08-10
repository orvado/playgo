namespace PlayGo.Core;

/// <summary>The color of a stone / a player in a game of Go.</summary>
public enum StoneColor
{
    Empty = 0,
    Black = 1,
    White = 2,
}

public static class StoneColorExtensions
{
    public static StoneColor Opponent(this StoneColor color)
    {
        return color switch
        {
            StoneColor.Black => StoneColor.White,
            StoneColor.White => StoneColor.Black,
            _ => throw new ArgumentOutOfRangeException(nameof(color), "Empty is not a player color."),
        };
    }

    public static string DisplayName(this StoneColor color)
    {
        return color switch
        {
            StoneColor.Black => "Black",
            StoneColor.White => "White",
            _ => "Empty",
        };
    }
}
