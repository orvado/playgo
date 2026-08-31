using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PlayGo.Core;

namespace PlayGo.App;

public partial class MainWindow : Window
{
    /// <summary>Pause between moves in a computer-vs-computer game, so it is watchable.</summary>
    private const int ComputerPacingMs = 500;

    private GameManager _game;
    private ScoreResult? _lastScore;
    private int _aiGeneration;

    /// <summary>
    /// How many moves are shown while reviewing the game; null means "follow the
    /// live position".
    /// </summary>
    private int? _reviewMoveCount;

    /// <summary>Guards MoveList.SelectionChanged while the list is being rebuilt.</summary>
    private bool _suppressReviewSelection;

    /// <summary>Where the current game was last saved, so Save can reuse it.</summary>
    private string? _recordPath;

    /// <summary>
    /// Set while a game record is being replayed. Replaying fires the same
    /// board-changed events as real play, so this stops the computer from
    /// trying to answer moves as they stream past.
    /// </summary>
    private bool _loadingGame;


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
        else if (ctrl && e.Key == Key.O)
        {
            OpenGame_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S)
        {
            SaveGame_Click(this, new RoutedEventArgs());
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
        else if (e.Key == Key.Escape && _reviewMoveCount is not null)
        {
            ExitReview();
            RefreshStatus("Back to the live position.");
            e.Handled = true;
        }
    }

    // ---------- game events ----------

    private void OnBoardChanged()
    {
        // Any change to the live position pulls the view back out of review mode.
        bool wasReviewing = _reviewMoveCount is not null;
        _reviewMoveCount = null;

        UpdateBoardVisuals();
        if (wasReviewing)
        {
            UpdateReviewBar();
            RefreshMoveList();
        }
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
        TerritoryMap? territory = null;

        if (_reviewMoveCount is int count)
        {
            // Reviewing: show a replayed position, with no interaction at all.
            BoardControl.Board = _game.GetBoardAt(count - 1);
            BoardControl.DeadStones = null;
            BoardControl.LastMove = count > 0 ? _game.History[count - 1].Point : null;
            BoardControl.CurrentPlayer = count > 0
                ? _game.History[count - 1].Color.Opponent()
                : StoneColor.Black;
            BoardControl.Interactive = false;
            BoardControl.ScoringMode = false;
        }
        else
        {
            BoardControl.Board = _game.Board;
            BoardControl.DeadStones = _game.DeadStones;
            BoardControl.CurrentPlayer = _game.CurrentPlayer;
            BoardControl.Interactive = _game.State == GameState.Playing && !_game.IsComputerTurn;
            BoardControl.ScoringMode = _game.State == GameState.Scoring;
            BoardControl.LastMove = _game.History.Count > 0
                ? _game.History[^1].Point
                : null;

            if (_game.State == GameState.Scoring)
                territory = _game.Board.GetTerritory(_game.DeadStones);
        }

        BoardControl.Territory = territory;
        SyncMoveNumbers();
        BoardControl.Refresh();
        UpdateEstimate(territory);
    }

    /// <summary>
    /// Running score shown while dead stones are being marked. Recomputed on
    /// every board change so it tracks the marks as they are made.
    /// </summary>
    private void UpdateEstimate(TerritoryMap? territory)
    {
        if (_game.State != GameState.Scoring || territory is null)
        {
            EstimateBox.Visibility = Visibility.Collapsed;
            return;
        }

        var s = _game.Board.CountScore(territory, _game.DeadStones, _game.Komi);
        EstimateBox.Visibility = Visibility.Visible;
        EstBlackLabel.Text = $"{s.BlackTerritory} territory + {s.BlackStones} stones";
        EstWhiteLabel.Text = $"{s.WhiteTerritory} territory + {s.WhiteStones} stones + komi";
        EstDeadLabel.Text = s.DeadBlack + s.DeadWhite == 0
            ? "none marked"
            : $"{s.DeadBlack + s.DeadWhite} ({s.DeadBlack} black, {s.DeadWhite} white)";
        EstScoreLabel.Text = $"{FormatScore(s.BlackScore)}  vs  {FormatScore(s.WhiteScore)}";
        EstMarginLabel.Text = s.Winner is StoneColor w
            ? $"{w.DisplayName()} +{FormatScore(s.Margin)}"
            : "Even";
    }

    // ---------- side panel ----------

    private void UpdateSidePanel()
    {
        bool playing = _game.State == GameState.Playing;
        if (_reviewMoveCount is not null)
        {
            TurnStone.Fill = Brushes.Transparent;
            TurnLabel.Text = "Reviewing";
            TurnSubLabel.Text = $"Move {_reviewMoveCount} of {_game.History.Count} — play is paused";
        }
        else if (playing)
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

        // While the computer is thinking, its move is already in flight — let it
        // land rather than racing it.
        bool canAct = !_game.IsComputerTurn && _game.State != GameState.Finished;
        PassButton.IsEnabled = canAct && playing;
        ResignButton.IsEnabled = canAct && playing;
        UndoButton.IsEnabled = canAct && _game.History.Count > 0;

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
        UpdateReviewBar();
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

        _suppressReviewSelection = true;
        MoveList.ItemsSource = lines;

        if (_reviewMoveCount is int count && count > 0)
        {
            // Each row pairs two moves; highlight the row holding the reviewed one.
            MoveList.SelectedIndex = Math.Min((count - 1) / 2, MoveList.Items.Count - 1);
            MoveList.ScrollIntoView(MoveList.SelectedItem);
        }
        else
        {
            MoveList.SelectedIndex = -1;
            if (MoveList.Items.Count > 0)
                MoveList.ScrollIntoView(MoveList.Items[MoveList.Items.Count - 1]);
        }
        _suppressReviewSelection = false;
    }

    // ---------- move review ----------

    private void MoveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressReviewSelection || MoveList.SelectedIndex < 0) return;
        EnterReview(Math.Min((MoveList.SelectedIndex + 1) * 2, _game.History.Count));
    }

    private void ReviewPrev_Click(object sender, RoutedEventArgs e)
    {
        StepReview(-1);
    }

    private void ReviewNext_Click(object sender, RoutedEventArgs e)
    {
        StepReview(+1);
    }

    private void ReviewLive_Click(object sender, RoutedEventArgs e)
    {
        ExitReview();
        RefreshStatus("Back to the live position.");
    }

    /// <summary>Shows the position after <paramref name="count"/> moves.</summary>
    private void EnterReview(int count)
    {
        _reviewMoveCount = Math.Clamp(count, 0, _game.History.Count);

        // Drop any computer move already being worked on: reviewing pauses play.
        _aiGeneration++;

        UpdateBoardVisuals();
        UpdateSidePanel(); // also refreshes the move list and the review bar

        int total = _game.History.Count;
        RefreshStatus(_reviewMoveCount == 0
            ? $"Reviewing the empty board (move 0 of {total}). Press Esc or click Live to return."
            : $"Reviewing move {_reviewMoveCount} of {total}. Press Esc or click Live to return.");
    }

    private void StepReview(int delta)
    {
        EnterReview((_reviewMoveCount ?? _game.History.Count) + delta);
    }

    private void ExitReview()
    {
        if (_reviewMoveCount is null) return;
        _reviewMoveCount = null;
        UpdateBoardVisuals();
        UpdateSidePanel();
        MaybeTriggerComputer(); // resume a computer turn that review paused
    }

    private void UpdateReviewBar()
    {
        ReviewBar.Visibility = _game.History.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool reviewing = _reviewMoveCount is not null;
        ReviewLabel.Text = reviewing
            ? $"Reviewing {_reviewMoveCount}/{_game.History.Count}"
            : $"Live — {_game.History.Count} moves";

        ReviewPrevButton.IsEnabled = !reviewing || _reviewMoveCount > 0;
        ReviewNextButton.IsEnabled = reviewing && _reviewMoveCount < _game.History.Count;
        ReviewLiveButton.IsEnabled = reviewing;
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
        ExitReview();
        var result = _game.PlayStone(point.Row, point.Col);

        if (!result.Success)
        {
            string reason = result.Error ?? "Illegal move.";
            // Say it on the board as well as in the status bar: the status bar
            // is easy to miss while you are looking at an intersection.
            BoardControl.FlashIllegal(point, reason);
            RefreshStatus(reason);
            return;
        }

        if (result.CapturedCount > 0)
        {
            SoundService.PlayCapture();
            RefreshStatus($"{_game.CurrentPlayer.Opponent().DisplayName()} captured {result.CapturedCount} stone(s).");
        }
        else
        {
            SoundService.PlayStone();
            RefreshStatus($"{_game.History[^1].ToNotation()} played.");
        }
    }

    private void OnBoardStoneMarked(object? sender, GoPoint point)
    {
        _game.ToggleDeadStone(point);
    }

    private void Pass_Click(object sender, RoutedEventArgs e)
    {
        if (_game.State != GameState.Playing || _game.IsComputerTurn) return;
        ExitReview();
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
        ExitReview();

        // Refuse while the computer owns the turn: its move is already in flight.
        if (_game.IsComputerTurn) return;

        // Discard any computer move that is still being calculated for the
        // position we are about to leave behind.
        _aiGeneration++;

        if (_game.Undo())
            RefreshStatus("Move undone.");
    }

    private void Resign_Click(object sender, RoutedEventArgs e)
    {
        if (_game.State != GameState.Playing || _game.IsComputerTurn) return;
        if (MessageBox.Show(this, "Resign now? Your opponent will win.",
                "Resign", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        ExitReview();
        _aiGeneration++;
        var move = _game.Resign();
        if (move is not null)
            RefreshStatus($"{move.Color.DisplayName()} resigned — {_game.Winner!.Value.DisplayName()} wins.");
    }


    private void NewGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewGameDialog(_game.BoardSize,
            _game.BlackPlayer == PlayerType.Computer,
            _game.WhitePlayer == PlayerType.Computer,
            _game.Komi, _game.Handicap)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return;

        _aiGeneration++; // cancel any in-flight computer move
        _lastScore = null;
        _reviewMoveCount = null;
        _recordPath = null; // a new game is not the saved record any more
        BoardControl.ClearCursor();
        _game.NewGame(dialog.BoardSize, dialog.BlackIsComputer ? PlayerType.Computer : PlayerType.Human,
            dialog.WhiteIsComputer ? PlayerType.Computer : PlayerType.Human,
            dialog.Komi, dialog.Handicap);
        RefreshStatus(dialog.Handicap > 0
            ? $"New {dialog.BoardSize}×{dialog.BoardSize} game with {dialog.Handicap} handicap stone(s). White to move."
            : $"New {dialog.BoardSize}×{dialog.BoardSize} game. Black to move.");
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

    private void ToggleSounds_Click(object sender, RoutedEventArgs e)
    {
        SoundService.Enabled = SoundsItem.IsChecked;
        RefreshStatus(SoundService.Enabled ? "Stone sounds on." : "Stone sounds off.");
    }

    private void ToggleMoveNumbers_Click(object sender, RoutedEventArgs e)
    {
        BoardControl.ShowMoveNumbers = MoveNumbersItem.IsChecked;
        SyncMoveNumbers();
        BoardControl.Refresh();
    }

    /// <summary>
    /// Keeps the board's move-number overlay in step with the game. While
    /// reviewing, only the moves up to the reviewed position are numbered.
    /// </summary>
    private void SyncMoveNumbers()
    {
        if (!BoardControl.ShowMoveNumbers)
        {
            BoardControl.MoveHistory = null;
            return;
        }

        BoardControl.MoveHistory = _reviewMoveCount is int count
            ? _game.History.Take(count).ToArray()
            : _game.History;
    }

    // ---------- game records ----------

    private void OpenGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a Go game record",
            Filter = Sgf.FileFilter,
            DefaultExt = ".sgf",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        string text;
        try
        {
            text = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The file could not be read.\n\n{ex.Message}", "Open game record",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!Sgf.TryParse(text, out var record, out var parseError) || record is null)
        {
            MessageBox.Show(this, parseError ?? "That file is not a game record this app can read.",
                "Open game record", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Stop anything in flight, then replay without the computer answering
        // each move as it streams past.
        _aiGeneration++;
        _lastScore = null;
        _reviewMoveCount = null;
        BoardControl.ClearCursor();

        bool ok;
        string? loadError;
        _loadingGame = true;
        try
        {
            ok = Sgf.TryLoad(_game, record, out loadError);
        }
        catch (Exception ex)
        {
            ok = false;
            loadError = ex.Message;
        }
        finally
        {
            _loadingGame = false;
        }

        _recordPath = ok ? dialog.FileName : null;
        UpdateBoardVisuals();
        UpdateSidePanel();

        if (!ok)
        {
            RefreshStatus("The game record could not be replayed.");
            MessageBox.Show(this, loadError ?? "The record could not be replayed.", "Open game record",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MaybeTriggerComputer();
        RefreshStatus($"Loaded {record.Moves.Count} moves from {Path.GetFileName(dialog.FileName)}.");
    }

    private void SaveGame_Click(object sender, RoutedEventArgs e)
    {
        // No filename chosen yet: fall through to Save As.
        if (_recordPath is null)
        {
            SaveGameAs_Click(sender, e);
            return;
        }
        SaveRecordTo(_recordPath);
    }

    private void SaveGameAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save this game as a record",
            Filter = Sgf.FileFilter,
            DefaultExt = ".sgf",
            AddExtension = true,
            FileName = $"playgo-{DateTime.Now:yyyyMMdd-HHmm}.sgf",
        };
        if (dialog.ShowDialog(this) != true) return;
        SaveRecordTo(dialog.FileName);
    }

    private void SaveRecordTo(string path)
    {
        try
        {
            File.WriteAllText(path, Sgf.Save(Sgf.FromGame(_game, _lastScore)));
            _recordPath = path;
            RefreshStatus($"Saved {_game.History.Count} moves to {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The file could not be saved.\n\n{ex.Message}", "Save game record",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
            "plus your stones still on the board. White receives a komi " +
            "(5.5 on 9×9, 6.5 on 13×13, 7.5 on 19×19) to compensate for " +
            "moving second.\n\n" +
            "When both players pass, click the dead stones to mark them — " +
            "the whole group is marked at once, and the score updates as you " +
            "go. You can also step back through the game from Move History.";
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
        ExitReview();
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
        ExitReview();
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
        // Replaying a record fires the same events as real play; don't let the
        // computer answer the moves as they stream past.
        if (_loadingGame) return;

        // Reviewing pauses the game, so a computer move is never played
        // underneath the position being examined.
        if (_reviewMoveCount is null && _game.IsComputerTurn && _game.State == GameState.Playing)
        {
            ScheduleComputerMove();
            return;
        }
        UpdateBoardVisuals();
    }

    private async void ScheduleComputerMove()
    {
        int gen = ++_aiGeneration;
        var snapshotBoard = _game.Board.Clone();
        var color = _game.CurrentPlayer;
        int moveNumber = _game.MoveNumber;
        int size = _game.BoardSize;
        // Random.Shared rather than a private Random: the search runs on a
        // worker thread, and Random is not thread-safe.
        var rng = Random.Shared;

        // The filter carries its own copy of the board and the ko / superko
        // history, so the worker below never reads live game state off-thread.
        var isLegal = _game.CreateLegalityFilter();

        UpdateBoardVisuals();
        RefreshStatus($"{color.DisplayName()} (computer) is thinking…");

        // Two computers play far faster than anyone can follow, so pace it.
        if (_game.BlackPlayer == PlayerType.Computer && _game.WhitePlayer == PlayerType.Computer)
        {
            await Task.Delay(ComputerPacingMs);
            if (gen != _aiGeneration) return;
        }

        _ = Task.Run(() =>
        {
            GoPoint? point;
            try
            {
                point = GoAI.ChooseMove(snapshotBoard, color, rng, moveNumber, size * size, isLegal: isLegal);
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
                        if (result.CapturedCount > 0) SoundService.PlayCapture();
                        else SoundService.PlayStone();

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

