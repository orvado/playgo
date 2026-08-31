using System.Globalization;
using System.Text;

namespace PlayGo.Core;

/// <summary>One move in a game record. A null point is a pass.</summary>
public readonly record struct SgfMove(StoneColor Color, GoPoint? Point)
{
    public bool IsPass => Point is null;
}

/// <summary>
/// A parsed SGF (Smart Game Format) game record: the board setup plus the
/// sequence of moves. SGF is the standard text format for Go game records,
/// so writing it makes PlayGo games openable in every other Go program —
/// and lets real game records be replayed here.
/// </summary>
public sealed class SgfGame
{
    public int BoardSize { get; init; } = 19;

    public double Komi { get; init; } = 7.5;

    public int Handicap { get; init; }

    /// <summary>Stones on the board before move 1 (handicap, or an arbitrary problem setup).</summary>
    public IReadOnlyList<GoPoint> SetupBlack { get; init; } = Array.Empty<GoPoint>();

    public IReadOnlyList<GoPoint> SetupWhite { get; init; } = Array.Empty<GoPoint>();

    public string? BlackName { get; init; }

    public string? WhiteName { get; init; }

    public string? Result { get; init; }

    public string? Date { get; init; }

    public string? Comment { get; init; }

    public IReadOnlyList<SgfMove> Moves { get; init; } = Array.Empty<SgfMove>();

    /// <summary>Who plays first. Inferred from the setup stones unless the record states it.</summary>
    public StoneColor FirstPlayer { get; init; } = StoneColor.Black;
}

/// <summary>Reads and writes SGF game records.</summary>
public static class Sgf
{
    public const string FileFilter = "Go game records (*.sgf)|*.sgf|All files (*.*)|*.*";

    /// <summary>Application identity recorded in the AP[] property of saved files.</summary>
    private const string ApplicationName = "PlayGo";

    // ---------------------------------------------------------------- save

    /// <summary>Captures the current state of a game as a record.</summary>
    /// <param name="game">The game to record.</param>
    /// <param name="score">Final score, if the game has been counted (used for the RE[] result).</param>
    public static SgfGame FromGame(GameManager game, ScoreResult? score = null)
    {
        var moves = new List<SgfMove>();
        foreach (var m in game.History)
        {
            switch (m.Kind)
            {
                case MoveKind.Play:
                    moves.Add(new SgfMove(m.Color, m.Point));
                    break;
                case MoveKind.Pass:
                    moves.Add(new SgfMove(m.Color, null));
                    break;
                case MoveKind.Resign:
                    // Resignation ends the record; nothing follows it.
                    break;
            }
        }

        string NameFor(PlayerType type) => type == PlayerType.Computer ? ApplicationName : "Human";

        return new SgfGame
        {
            BoardSize = game.BoardSize,
            Komi = game.Komi,
            Handicap = game.Handicap,
            SetupBlack = game.Handicap > 0
                ? GameManager.HandicapPoints(game.BoardSize, game.Handicap)
                    .Select(p => new GoPoint(p.Row, p.Col)).ToArray()
                : Array.Empty<GoPoint>(),
            BlackName = NameFor(game.BlackPlayer),
            WhiteName = NameFor(game.WhitePlayer),
            Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Result = ResultText(game, score),
            Moves = moves,
            FirstPlayer = game.Handicap > 0 ? StoneColor.White : StoneColor.Black,
        };
    }

    private static string ResultText(GameManager game, ScoreResult? score)
    {
        if (game.Winner is not StoneColor winner) return "?";
        char c = winner == StoneColor.Black ? 'B' : 'W';

        if (score is ScoreResult s)
            return $"{c}+{s.Margin.ToString(s.Margin % 1 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture)}";

        return $"{c}+Resign";
    }

    /// <summary>Serialises a record to SGF text.</summary>
    public static string Save(SgfGame game)
    {
        var sb = new StringBuilder();
        sb.Append("(;GM[1]FF[4]AP[").Append(Escape(ApplicationName)).Append("]");
        sb.Append("SZ[").Append(game.BoardSize.ToString(CultureInfo.InvariantCulture)).Append(']');
        sb.Append("KM[").Append(game.Komi.ToString("0.0", CultureInfo.InvariantCulture)).Append(']');

        if (game.Handicap > 0)
            sb.Append("HA[").Append(game.Handicap.ToString(CultureInfo.InvariantCulture)).Append(']');

        Text(sb, "PB", game.BlackName);
        Text(sb, "PW", game.WhiteName);
        Text(sb, "DT", game.Date);
        Text(sb, "RE", game.Result);
        Text(sb, "C", game.Comment);

        sb.Append("PL[").Append(game.FirstPlayer == StoneColor.White ? 'W' : 'B').Append(']');

        // Setup stones, if the record does not use plain handicap stones.
        Setup(sb, "AB", game.SetupBlack);
        Setup(sb, "AW", game.SetupWhite);

        sb.Append('\n');
        foreach (var move in game.Moves)
        {
            char tag = move.Color == StoneColor.Black ? 'B' : 'W';
            sb.Append(';').Append(tag).Append('[');
            if (move.Point is GoPoint p) sb.Append(Coord(p));
            sb.Append("]\n");
        }

        sb.Append(')');
        return sb.ToString();

        static void Text(StringBuilder builder, string prop, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                builder.Append(prop).Append('[').Append(Escape(value)).Append(']');
        }

        static void Setup(StringBuilder builder, string prop, IReadOnlyList<GoPoint> points)
        {
            if (points.Count == 0) return;
            builder.Append(prop);
            foreach (var p in points)
                builder.Append('[').Append(Coord(p)).Append(']');
        }
    }

    // ---------------------------------------------------------------- load

    /// <summary>
    /// Parses SGF text. Only the main line of play is read; variations in
    /// brackets are skipped, which is what a replay needs.
    /// </summary>
    public static bool TryParse(string text, out SgfGame? game, out string? error)
    {
        game = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The file is empty.";
            return false;
        }

        var nodes = ParseNodes(text);
        if (nodes.Count == 0)
        {
            error = "This does not look like an SGF file: no game tree was found.";
            return false;
        }

        var root = nodes[0];
        int size = GetInt(root, "SZ") ?? 19;
        if (size is not (9 or 13 or 19))
        {
            error = $"Unsupported board size {size}. PlayGo plays on 9×9, 13×13 and 19×19.";
            return false;
        }

        double komi = GetDouble(root, "KM") ?? GameManager.DefaultKomi(size);
        int handicap = GetInt(root, "HA") ?? 0;

        var setupBlack = GetPoints(root, "AB", size);
        var setupWhite = GetPoints(root, "AW", size);
        if (handicap == 0 && setupBlack.Count > 0) handicap = setupBlack.Count;

        var moves = new List<SgfMove>();
        for (int i = 1; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.TryGetValue("B", out var b) && b.Count > 0)
                moves.Add(new SgfMove(StoneColor.Black, ParseCoord(b[0], size)));
            else if (node.TryGetValue("W", out var w) && w.Count > 0)
                moves.Add(new SgfMove(StoneColor.White, ParseCoord(w[0], size)));
        }

        // SGF does not record the turn directly; PL[] says so, otherwise a
        // game that starts with setup stones is a handicap game (white first).
        bool anySetup = setupBlack.Count > 0 || setupWhite.Count > 0;
        var first = GetFirstPlayer(root) ?? (anySetup ? StoneColor.White : StoneColor.Black);

        game = new SgfGame
        {
            BoardSize = size,
            Komi = komi,
            Handicap = handicap,
            SetupBlack = setupBlack,
            SetupWhite = setupWhite,
            BlackName = GetText(root, "PB"),
            WhiteName = GetText(root, "PW"),
            Result = GetText(root, "RE"),
            Date = GetText(root, "DT"),
            Comment = GetText(root, "C"),
            Moves = moves,
            FirstPlayer = first,
        };
        return true;
    }

    /// <summary>
    /// Loads a record into a running game, replacing whatever was there.
    /// The current player assignments (human/computer) are kept.
    /// </summary>
    public static bool TryLoad(GameManager game, SgfGame record, out string? error)
    {
        error = null;

        var black = game.BlackPlayer;
        var white = game.WhitePlayer;

        game.NewGame(record.BoardSize, black, white, record.Komi, handicap: 0);

        foreach (var p in record.SetupWhite)
            game.PlaceSetupStone(p.Row, p.Col, StoneColor.White);
        foreach (var p in record.SetupBlack)
            game.PlaceSetupStone(p.Row, p.Col, StoneColor.Black);

        game.SetTurn(record.FirstPlayer);

        for (int i = 0; i < record.Moves.Count; i++)
        {
            var move = record.Moves[i];

            if (move.IsPass)
            {
                if (game.Pass() is null)
                {
                    error = $"Move {i + 1} could not be replayed: the game had already ended.";
                    return false;
                }
                continue;
            }

            var p = move.Point!.Value;
            var result = game.PlayStone(p.Row, p.Col);
            if (!result.Success)
            {
                error = $"Move {i + 1} ({GoMoveFormatter.ToNotation(p, record.BoardSize)}) is not legal here: {result.Error}";
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- parsing

    private static List<Dictionary<string, List<string>>> ParseNodes(string text)
    {
        var nodes = new List<Dictionary<string, List<string>>>();
        Dictionary<string, List<string>>? current = null;
        int depth = 0;
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '(')
            {
                depth++;
                i++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                i++;
                continue;
            }

            if (c == ';')
            {
                // Only the main line is kept; a nested variation is skipped.
                if (depth <= 1)
                {
                    current = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    nodes.Add(current);
                }
                i++;
                continue;
            }

            if (IsPropChar(c) && current is not null)
            {
                int start = i;
                while (i < text.Length && IsPropChar(text[i])) i++;
                string prop = text[start..i];

                var values = new List<string>();
                while (i < text.Length)
                {
                    // Skip whitespace between a property name and its values.
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    if (i >= text.Length || text[i] != '[') break;

                    i++; // consume '['
                    var sb = new StringBuilder();
                    while (i < text.Length)
                    {
                        char v = text[i];
                        if (v == '\\' && i + 1 < text.Length)
                        {
                            sb.Append('\\').Append(text[i + 1]);
                            i += 2;
                            continue;
                        }
                        if (v == ']')
                        {
                            i++;
                            break;
                        }
                        sb.Append(v);
                        i++;
                    }
                    values.Add(Unescape(sb.ToString()));
                }

                current[prop] = values;
                continue;
            }

            i++;
        }

        return nodes;
    }

    private static bool IsPropChar(char c) => c is >= 'A' and <= 'Z';

    private static string? GetText(Dictionary<string, List<string>> node, string prop) =>
        node.TryGetValue(prop, out var v) && v.Count > 0 && v[0].Length > 0 ? v[0] : null;

    private static int? GetInt(Dictionary<string, List<string>> node, string prop)
    {
        var text = GetText(node, prop);
        return text is not null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    private static double? GetDouble(Dictionary<string, List<string>> node, string prop)
    {
        var text = GetText(node, prop);
        return text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    private static StoneColor? GetFirstPlayer(Dictionary<string, List<string>> node)
    {
        var text = GetText(node, "PL");
        if (text is null || text.Length == 0) return null;
        return char.ToUpperInvariant(text[0]) switch
        {
            'B' => StoneColor.Black,
            'W' => StoneColor.White,
            _ => null,
        };
    }

    private static List<GoPoint> GetPoints(Dictionary<string, List<string>> node, string prop, int size)
    {
        var points = new List<GoPoint>();
        if (!node.TryGetValue(prop, out var values)) return points;
        foreach (var v in values)
        {
            if (ParseCoord(v, size) is GoPoint p) points.Add(p);
        }
        return points;
    }

    // ------------------------------------------------------------ coordinates

    /// <summary>
    /// SGF writes a point as two lowercase letters: the column first, then the
    /// row, both counted from the top-left. An out-of-range pair (or an empty
    /// value) is a pass.
    /// </summary>
    private static GoPoint? ParseCoord(string value, int size)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 2) return null;

        int col = char.ToLowerInvariant(value[0]) - 'a';
        int row = char.ToLowerInvariant(value[1]) - 'a';

        if (col < 0 || row < 0 || col >= size || row >= size) return null;
        return new GoPoint(row, col);
    }

    private static string Coord(GoPoint p) =>
        $"{(char)('a' + p.Col)}{(char)('a' + p.Row)}";

    // ---------------------------------------------------------------- text

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("]", "\\]", StringComparison.Ordinal);

    /// <summary>Reverses SGF's text escaping, including soft line breaks.</summary>
    private static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }

            char next = value[i + 1];
            i++;

            // A backslash before a line break is a "soft" break: it is removed.
            if (next is '\n' or '\r')
            {
                while (i + 1 < value.Length && value[i + 1] is '\n' or '\r') i++;
                continue;
            }

            // Anything else is literal: \] and \\ keep just the second character.
            sb.Append(next);
        }
        return sb.ToString();
    }
}
