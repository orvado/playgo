# PlayGo — the classic game of Go for Windows

Go (Weiqi, Baduk) is an abstract strategy board game for two players in which
the aim is to fence off more territory than the opponent. PlayGo is a native
Windows desktop app built with **.NET 9 and WPF** — no external dependencies.

## Features

- **Full Go rules engine** (in `src/PlayGo.Core`, fully unit tested):
  - Captures and group liberties
  - Suicide is forbidden; capturing moves are always legal
  - Basic ko *and* positional superko (no board position may repeat)
  - Passes, resignation, undo (smart undo that steps back past computer moves)
  - Chinese area scoring with komi (7.5), including dead-stone marking at the
    end of the game
- **Three board sizes**: 9×9, 13×13 and 19×19
- **Game modes**: Human vs Human (hot-seat), Human vs Computer (either color),
  or Computer vs Computer to watch
- **Built-in heuristic AI**: captures when possible, rescues atari'd groups,
  builds connections and favors the centre and star points early
- **Polished board rendering**: wooden board, gradient-shaded 3D stones,
  star points (hoshi), coordinate labels, last-move marker, ghost-stone hover,
  and red ✕ marks for dead stones
- **Move history** with Go notation (columns A–T, skipping I) and capture counts

## How to play

1. Black moves first. Click an intersection to place a stone.
2. A stone (or connected group) is **captured** when the opponent fills its
   last liberty — the last empty adjacent point.
3. You may **pass** (P) when you have nothing useful to play. When both players
   pass in a row, the game enters scoring.
4. In scoring mode, click stones that are **dead** (surrounded but not captured)
   to mark them, then press **Count Score**.
5. **Undo** (Ctrl+Z) takes back the last move (or two, against the computer).

### Keyboard shortcuts

| Key       | Action     |
|-----------|------------|
| `Ctrl+N`  | New game   |
| `P`       | Pass       |
| `Ctrl+Z`  | Undo       |

## Build and run

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
# build the whole solution and run the rule-engine tests
dotnet build PlayGo.sln
dotnet test tests/PlayGo.Core.Tests/PlayGo.Core.Tests.csproj

# run the app
dotnet run --project src/PlayGo.App
# or, after building, launch the exe directly:
# .\src\PlayGo.App\bin\Debug\net9.0-windows\PlayGo.App.exe
```

## Project layout

```
PlayGo.sln
src/
  PlayGo.Core/            # rules engine + AI (pure C#, no UI dependencies)
    GoBoard.cs            # board state, captures, suicide, groups, scoring
    GameManager.cs        # turns, passes, ko/superko, history, undo, modes
    GoAI.cs               # heuristic computer player
    GoMove.cs, ScoreResult.cs, MoveResult.cs, StoneColor.cs, GoPoint.cs
  PlayGo.App/             # WPF application
    MainWindow.xaml(.cs)  # board UI, side panel, scoring flow, AI integration
    NewGameDialog.xaml(.cs)
    Controls/GoBoardControl.cs   # custom-drawn board
tests/
  PlayGo.Core.Tests/      # 42 xUnit tests covering all rules and the AI
```

## Scoring rules

PlayGo uses **Chinese area scoring**: your score is your territory (empty
points fully enclosed by your stones) plus your own stones still on the board.
White receives a **komi** of 7.5 points to compensate for moving second.
