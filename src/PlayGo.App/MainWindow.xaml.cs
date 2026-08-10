using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PlayGo.Core;

namespace PlayGo.App;

public partial class MainWindow : Window
{
    private GameManager _game;
    private ScoreResult? _lastScore;
    private int _aiGeneration;
    private readonly Random _rng = new();

    public MainWindow()
    {
        InitializeComponent();

        _game = new GameManager(19, PlayerType.Human, PlayerType.Computer);
        _game.BoardChanged += OnBoardChanged;
        _game.StatusChanged += OnStatusChanged;
        _game.GameOver += OnGameOver;

        BoardControl.Board = _game.Board;
        BoardControl.LastMove = null;
        BoardControl.DeadStones = _game.DeadStones;

        PreviewKeyDown += OnWindowPreviewKeyDown;
        Closing += (_, _) => _aiGeneration++; // stop any pending computer move

        UpdateBoardVisuals();
        UpdateSidePanel();
        RefreshStatus("New game. Black to move — click the board to place a stone, or press P to pass.");
    }

    private void OnWindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.N)
        {
            NewGame_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.P && !e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            Pass_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z)
        {
            Undo_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ---------- game events ----------

    private void OnBoardChanged()
    {
        BoardControl.Board = _game.Board;
        BoardControl.DeadStones = _game.DeadStones;
        UpdateBoardVisuals();
    }

    private void OnStatusChanged()
    {
        UpdateSidePanel();
        MaybeTriggerComputer();
    }

    private void OnGameOver()
    {
        // The result panel is filled by the caller that holds the ScoreResult.
    }

    private void UpdateBoardVisuals()
    {
        BoardControl.CurrentPlayer = _game.CurrentPlayer;
        BoardControl.Interactive = _game.State == GameState.Playing && !_game.IsComputerTurn;
        BoardControl.ScoringMode = _game.State == GameState.Scoring;
        BoardControl.LastMove = _game.History.Count > 0
            ? _game.History[^1].Point
            : null;
        BoardControl.Refresh();
    }

    // ---------- side panel ----------

    private void UpdateSidePanel()
    {
        bool playing = _game.State == GameState.Playing;
        if (playing)
        {
            var color = _game.CurrentPlayer;
            TurnStone.Fill = color == StoneColor.Black
                ? (Brush)FindResource("BlackStoneBrush")
                : (Brush)FindResource("WhiteStoneBrush");
            TurnStone.Stroke = color == StoneColor.Black
                ? Brushes.Black
                : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            TurnLabel.Text = $"{color.DisplayName()} to move";
            var player = _game.GetPlayer(color);
            TurnSubLabel.Text = player == PlayerType.Computer
                ? "Computer is thinking…"
                : _game.BlackPlayer == _game.WhitePlayer
                    ? "Two-player local game"
                    : "Your move";
        }
        else if (_game.State == GameState.Scoring)
        {
            TurnStone.Fill = Brushes.Transparent;
            TurnLabel.Text = "Scoring";
            TurnSubLabel.Text = "Mark dead stones, then count.";
        }
        else
        {
            TurnStone.Fill = Brushes.Transparent;
            TurnLabel.Text = _game.Winner is StoneColor w ? $"{w.DisplayName()} wins" : "Game over";
            TurnSubLabel.Text = _game.Winner is null ? "Draw" : "By resignation or score";
        }

        BlackPrisonersLabel.Text = $"{_game.BlackPrisoners} captured";
        WhitePrisonersLabel.Text = $"{_game.WhitePrisoners} captured";

        PassButton.IsEnabled = playing;
        ResignButton.IsEnabled = playing;
        UndoButton.IsEnabled = _game.State != GameState.Scoring && _game.History.Count > 0;

        ScoringPanel.Visibility = _game.State == GameState.Scoring ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = _game.State == GameState.Finished ? Visibility.Visible : Visibility.Collapsed;

        if (_game.State == GameState.Finished)
        {
            if (_lastScore is ScoreResult score)
            {
                FillResultPanel(score);
            }
            else if (_game.Winner is StoneColor w)
            {
                // Game ended by resignation.
                WinnerLabel.Text = $"{w.DisplayName()} wins by resignation";
                BlackTerritoryLabel.Text = BlackStonesLabel.Text = WhiteTerritoryLabel.Text =
                    WhiteStonesLabel.Text = KomiLabel.Text = TotalLabel.Text = "—";
            }
        }

        RefreshMoveList();
    }

    private void FillResultPanel(ScoreResult s)
    {
        WinnerLabel.Text = s.Winner is StoneColor w
            ? $"{w.DisplayName()} wins by {FormatScore(s.Margin)}"
            : "Draw";
        BlackTerritoryLabel.Text = $"{s.BlackTerritory}";
        BlackStonesLabel.Text = $"{s.BlackStones} (+ {s.DeadBlack} dead)";
        WhiteTerritoryLabel.Text = $"{s.WhiteTerritory}";
        WhiteStonesLabel.Text = $"{s.WhiteStones} (+ {s.DeadWhite} dead)";
        KomiLabel.Text = $"+ {s.Komi:0.0}";
        TotalLabel.Text = $"{FormatScore(s.BlackScore)}  vs  {FormatScore(s.WhiteScore)}";
    }

    private static string FormatScore(double v) => v.ToString(v % 1 == 0 ? "0" : "0.0");

    private void RefreshMoveList()
    {
        var lines = new List<string>();
        var history = _game.History;
        for (int i = 0; i < history.Count; i += 2)
        {
            var black = history[i];
            string w = i + 1 < history.Count ? FormatMove(history[i + 1]) : "";
            lines.Add($"{black.MoveNumber,3}.  {FormatMove(black),-8} {w}");
        }
        MoveList.ItemsSource = lines;
        if (MoveList.Items.Count > 0)
            MoveList.ScrollIntoView(MoveList.Items[MoveList.Items.Count - 1]);
    }

    private static string FormatMove(GoMove m)
    {
        string baseNotation = m.ToNotation();
        string captures = m.Captured.Count > 0 ? $" ⚑{m.Captured.Count}" : "";
        return baseNotation + captures;
    }

    private void RefreshStatus(string message) => StatusText.Text = message;

    // ---------- input handlers ----------

    private void OnBoardMoveRequested(object? sender, GoPoint point)
    {
        if (_game.State != GameState.Playing || _game.IsComputerTurn) return;
        var result = _game.PlayStone(point.Row, point.Col);
        if (!result.Success)
            RefreshStatus(result.Error ?? "Illegal move.");
        else if (result.CapturedCount > 0)
            RefreshStatus($"{_game.CurrentPlayer.Opponent().DisplayName()} captured {result.CapturedCount} stone(s).");
        else
            RefreshStatus($"{_game.History[^1].ToNotation()} played.");
    }

    private void OnBoardStoneMarked(object? sender, GoPoint point)
    {
        _game.ToggleDeadStone(point);
    }

    private void Pass_Click(object sender, RoutedEventArgs e)
    {
        if (_game.State != GameState.Playing) return;
        var move = _game.Pass();
        if (move is not null)
        {
            if (_game.State == GameState.Scoring)
                RefreshStatus("Both players passed. Mark dead stones and count the score.");
            else
                RefreshStatus($"{move.Color.DisplayName()} passed.");
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_game.Undo())
            RefreshStatus("Move undone.");
    }

    private void Resign_Click(object sender, RoutedEventArgs e)
    {
        if (_game.State != GameState.Playing) return;
        if (MessageBox.Show(this, "Resign now? Your opponent will win.",
                "Resign", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var move = _game.Resign();
        if (move is not null)
            RefreshStatus($"{move.Color.DisplayName()} resigned — {_game.Winner!.Value.DisplayName()} wins.");
    }


    private void NewGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewGameDialog(_game.BoardSize,
            _game.BlackPlayer == PlayerType.Computer,
            _game.WhitePlayer == PlayerType.Computer)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return;

        _aiGeneration++; // cancel any in-flight computer move
        _lastScore = null;
        _game.NewGame(dialog.BoardSize, dialog.BlackIsComputer ? PlayerType.Computer : PlayerType.Human,
            dialog.WhiteIsComputer ? PlayerType.Computer : PlayerType.Human);
        RefreshStatus($"New {dialog.BoardSize}×{dialog.BoardSize} game. Black to move.");
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleCoordinates_Click(object sender, RoutedEventArgs e)
    {
        if (BoardControl is not null)
        {
            BoardControl.ShowCoordinates = ShowCoordsItem.IsChecked;
            BoardControl.Refresh();
        }
    }

    private void HowToPlay_Click(object sender, RoutedEventArgs e)
    {
        string help =
            "HOW TO PLAY GO\n\n" +
            "Go is played on a grid of intersections. Black moves first, then " +
            "players alternate placing one stone per turn.\n\n" +
            "• Goal: surround more empty territory than your opponent.\n" +
            "• A stone or connected group is captured when it has no empty " +
            "adjacent points (liberties) left.\n" +
            "• Suicide is not allowed, and repeating a previous board position " +
            "is forbidden (ko/superko).\n" +
            "• Press P or click Pass to give up your turn. When both players " +
            "pass, the game is scored.\n\n" +
            "Scoring uses Chinese area rules: your score is your territory " +
            "plus your stones still on the board. White receives a komi of 7.5 " +
            "to compensate for moving second.";
        MessageBox.Show(this, help, "How to Play Go", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "PlayGo\n\nA classic two-player board game for Windows, built with .NET 9 and WPF.\n\n" +
            "Play against a friend on the same PC or challenge the built-in computer player.\n\n" +
            "Board sizes: 9×9, 13×13 and 19×19.",
            "About PlayGo", MessageBoxButton.OK, MessageBoxImage.Information);
    }


    // ---------- scoring ----------

    private void CountScore_Click(object sender, RoutedEventArgs e)
    {
        var score = _game.CountScore();
        if (score is null) return;
        _lastScore = score;
        FillResultPanel(score);
        var winner = score.Winner is StoneColor w
            ? $"{w.DisplayName()} wins by {FormatScore(score.Margin)}"
            : "It's a draw";
        RefreshStatus($"Game over. {winner} (Black {FormatScore(score.BlackScore)} — White {FormatScore(score.WhiteScore)}).");
    }

    private void ResumePlay_Click(object sender, RoutedEventArgs e)
    {
        _game.ResumePlay();
        RefreshStatus("Play resumed — pass again to restart scoring.");
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e)
    {
        _game.ClearDeadMarks();
    }

    // ---------- computer player ----------

    private void MaybeTriggerComputer()
    {
        if (!_game.IsComputerTurn || _game.State != GameState.Playing)
        {
            UpdateBoardVisuals();
            return;
        }
        ScheduleComputerMove();
    }

    private void ScheduleComputerMove()
    {
        int gen = ++_aiGeneration;
        var snapshotBoard = _game.Board.Clone();
        var color = _game.CurrentPlayer;
        int moveNumber = _game.MoveNumber;
        int size = _game.BoardSize;
        var rng = _rng;

        UpdateBoardVisuals();
        RefreshStatus($"{color.DisplayName()} (computer) is thinking…");

        _ = Task.Run(() =>
        {
            GoPoint? point;
            try
            {
                point = GoAI.ChooseMove(snapshotBoard, color, rng, moveNumber, size * size,
                    isLegal: (r, c) => _game.CanPlay(r, c).Success);
            }
            catch
            {
                point = null;
            }

            Dispatcher.InvokeAsync(() =>
            {
                if (gen != _aiGeneration) return;
                if (_game.State != GameState.Playing || _game.CurrentPlayer != color) return;

                if (point is GoPoint p)
                {
                    var result = _game.PlayStone(p.Row, p.Col);
                    if (result.Success)
                    {
                        RefreshStatus(result.CapturedCount > 0
                            ? $"Computer ({color.DisplayName()}) played {GoMoveFormatter.ToNotation(p, size)} and captured {result.CapturedCount} stone(s)."
                            : $"Computer ({color.DisplayName()}) played {GoMoveFormatter.ToNotation(p, size)}.");
                    }
                    else
                    {
                        _game.Pass();
                        RefreshStatus($"Computer ({color.DisplayName()}) passed.");
                    }
                }
                else
                {
                    _game.Pass();
                    RefreshStatus($"Computer ({color.DisplayName()}) passed.");
                }
            });
        });
    }
}

