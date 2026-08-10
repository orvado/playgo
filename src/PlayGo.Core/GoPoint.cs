namespace PlayGo.Core;

/// <summary>A position on the board. (Row, Col) with (0, 0) at the top-left intersection.</summary>
public readonly record struct GoPoint(int Row, int Col)
{
    public override string ToString() => $"{Row},{Col}";
}
