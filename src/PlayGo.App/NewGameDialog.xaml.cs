using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PlayGo.Core;

namespace PlayGo.App;

public partial class NewGameDialog : Window
{
    /// <summary>Komi used in handicap games, where the stones already favour Black.</summary>
    private const double HandicapKomi = 0.5;

    /// <summary>Set once the user edits komi, so board-size changes stop overwriting it.</summary>
    private bool _komiEdited;

    /// <summary>True while the dialog is populating its own controls.</summary>
    private bool _initializing = true;

    public NewGameDialog(int currentSize, bool blackIsComputer, bool whiteIsComputer,
        double? currentKomi = null, int currentHandicap = 0)
    {
        InitializeComponent();

        SizeCombo.SelectedIndex = currentSize switch
        {
            9 => 0,
            13 => 1,
            _ => 2,
        };

        if (blackIsComputer && whiteIsComputer) ModeAIvAI.IsChecked = true;
        else if (blackIsComputer) ModeVsAIWhite.IsChecked = true;
        else if (whiteIsComputer) ModeVsAI.IsChecked = true;
        else ModePvP.IsChecked = true;

        HandicapCombo.SelectedIndex = HandicapToIndex(currentHandicap);

        _komiEdited = currentKomi is double k && Math.Abs(k - SuggestedKomi()) > 0.001;
        KomiBox.Text = Format(currentKomi ?? SuggestedKomi());

        _initializing = false;
        RefreshHints();
    }

    public int BoardSize => SizeCombo.SelectedIndex switch
    {
        0 => 9,
        1 => 13,
        _ => 19,
    };

    public bool BlackIsComputer => ModeVsAIWhite.IsChecked == true || ModeAIvAI.IsChecked == true;

    public bool WhiteIsComputer => ModeVsAI.IsChecked == true || ModeAIvAI.IsChecked == true;

    /// <summary>Handicap index 0 is "none"; the list then jumps straight to 2 stones.</summary>
    public int Handicap => HandicapCombo.SelectedIndex switch
    {
        <= 0 => 0,
        int i => i + 1,
    };

    /// <summary>
    /// The entered komi, clamped to a sane range. Unparseable input falls back to
    /// the standard value for the chosen board size rather than failing to start.
    /// </summary>
    public double Komi
    {
        get
        {
            if (double.TryParse(KomiBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return Math.Clamp(v, -50, 50);
            return GameManager.DefaultKomi(BoardSize);
        }
    }

    /// <summary>Standard komi for the current board size, or 0.5 in a handicap game.</summary>
    private double SuggestedKomi() =>
        Handicap > 0 ? HandicapKomi : GameManager.DefaultKomi(BoardSize);

    private static int HandicapToIndex(int handicap) =>
        handicap switch
        {
            <= 0 => 0,
            >= GameManager.MaxHandicap => GameManager.MaxHandicap - 1,
            int h => h - 1,
        };

    private static string Format(double value) =>
        value.ToString(value % 1 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);

    private void RefreshHints()
    {
        bool handicap = Handicap > 0;
        double standard = GameManager.DefaultKomi(BoardSize);

        HandicapHint.Text = handicap
            ? $"{Handicap} black stones placed on the star points; White plays first."
            : "Handicap games suit players of different strengths.";

        KomiHint.Text = handicap
            ? $"Handicap games normally use {Format(HandicapKomi)}."
            : $"Standard for {BoardSize}×{BoardSize} is {Format(standard)}.";

        bool parsed = double.TryParse(KomiBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
        if (!parsed)
        {
            KomiWarning.Text = $"“{KomiBox.Text}” isn't a number — using {Format(SuggestedKomi())}.";
        }
        else if (Math.Abs(v - SuggestedKomi()) > 0.001)
        {
            KomiWarning.Text = $"Custom komi of {Format(v)}.";
        }
        else
        {
            KomiWarning.Text = "";
        }
    }

    private void SizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        // Re-suggest komi for the new size unless it was deliberately changed.
        if (!_komiEdited)
            KomiBox.Text = Format(SuggestedKomi());

        RefreshHints();
    }

    private void HandicapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        // Handicap stones already compensate Black, so drop komi to 0.5 — but
        // only while the user is still on a suggested value.
        if (!_komiEdited)
            KomiBox.Text = Format(SuggestedKomi());

        RefreshHints();
    }

    private void KomiBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        _komiEdited = true;
        RefreshHints();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
