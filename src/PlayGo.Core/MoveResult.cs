namespace PlayGo.Core;

/// <summary>Outcome of attempting to place a stone.</summary>
public sealed class MoveResult
{
    public static MoveResult Ok(IReadOnlyList<GoPoint> captured, int libertiesAfter) =>
        new(true, null, captured, libertiesAfter);

    public static MoveResult Fail(string reason) =>
        new(false, reason, Array.Empty<GoPoint>(), 0);

    private MoveResult(bool success, string? error, IReadOnlyList<GoPoint> captured, int libertiesAfter)
    {
        Success = success;
        Error = error;
        Captured = captured;
        LibertiesAfter = libertiesAfter;
    }

    public bool Success { get; }

    public string? Error { get; }

    public IReadOnlyList<GoPoint> Captured { get; }

    /// <summary>Liberties of the group just played after the move completes.</summary>
    public int LibertiesAfter { get; }

    public int CapturedCount => Captured.Count;

    public bool CapturesSomething => Captured.Count > 0;
}
