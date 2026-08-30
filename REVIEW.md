# PlayGo — code review

**Reviewed:** 2026-08-30 · `main` @ `3e995b1` · .NET 9 / WPF
**Build:** clean (0 warnings, 0 errors) · **Tests:** 42/42 passing

## Verdict

The bones here are genuinely good. `PlayGo.Core` is cleanly separated from the WPF layer, the
rules engine is correct (captures, suicide, ko, superko, area scoring all check out), the board
renderer is hand-drawn and looks decent, and 42 tests pass. This is a solid foundation.

The problem is that **the game does not reliably end**, and that undermines everything else.
`GoAI` has no concept of a settled position, so it never passes. A 9×9 computer-vs-computer
game ran **8,335 moves** before positional superko finally starved it out — a normal 9×9 game
is 40–80 moves. Computer vs Computer is advertised as a headline feature in the README, and as
shipped it is unwatchable.

Below, everything is ordered by what I'd fix first. Each item is something I verified by
running against the real code, not just reading it.

---

## Critical — the game doesn't end properly

### 1. The AI never passes, so games run away

`GoAI.ChooseMove` returns `null` **only when there are zero legal moves on the entire board**
(`GoAI.cs:27-66`). There is no notion of "the position is settled, I should pass." Neither is
there any evaluation of *whether a move is worth playing* — it picks the best of the available
moves no matter how worthless that best move is.

Measured on the real engine:

| Scenario | Expected | Actual |
|---|---|---|
| 9×9, Computer vs Computer | 40–80 moves | **8,335 moves**, 4,093 captures for Black |
| 9×9, Human passes every turn | game ends after 2 passes | Human had to pass **81 times** (162 plies) |

The only reason it terminates at all is that positional superko eventually exhausts the
position space. The human-facing consequence: **you cannot finish a game against the computer
by passing** — you pass, the engine plays a meaningless stone into its own territory, your
pass counter resets, repeat.

**Fix:** give `ChooseMove` a "not worth playing" threshold and let it return null, plus a real
pass heuristic. The cheapest effective version: after scoring candidates, if the best score is
below a floor and no captures/rescues are available, pass. Better: count remaining *meaningful*
points — if every empty region is either (a) enclosed by one colour (dame-free) or (b) filling
your own territory, pass. Also add a hard cap: if both sides pass the engine should stop.

### 2. A stale computer move survives Undo

`MainWindow.Undo_Click` (`MainWindow.xaml.cs:222-226`) calls `_game.Undo()` but never bumps
`_aiGeneration`, which is the token used to invalidate in-flight computer moves. Reproduced:

```
human plays (4,4)  -> engine starts computing White's reply
human hits Ctrl+Z  -> move undone, generation token unchanged
human plays (0,0)  -> now White to move again
engine callback    -> guard passes (state==Playing, CurrentPlayer==White) -> plays the move
                      it computed from the OLD position, which included Black at (4,4)
```

The engine's move is computed from a board that no longer exists. It may be illegal (falls back
to a bogus pass) or legal but nonsensical. One line fixes it — bump `_aiGeneration` in
`Undo_Click`.

### 3. The AI worker reads game state off the UI thread

`ScheduleComputerMove` (`MainWindow.xaml.cs:346-352`) passes
`isLegal: (r, c) => _game.CanPlay(r, c).Success` into `Task.Run`. That callback is invoked up to
361 times from a threadpool thread, and `CanPlay` reads `_positionCounts` (a `Dictionary`),
`_history`, and the live `Board` (`GameManager.cs:95-117`).

Meanwhile the UI thread is still interactive and can call `Pass()`, `Undo()`, `Resign()` or
`NewGame()` — all of which mutate that dictionary and the history list. That is a genuine
concurrent read/write on a `Dictionary`, which can throw or corrupt state. It's intermittent and
therefore easy to ship.

**Fix:** snapshot the data the legality check needs (position hashes + history) onto the worker
before starting it, and have the callback only re-validate on the UI thread.

### 4. The human can act during the computer's turn

`PassButton`, `UndoButton` and `ResignButton` are enabled whenever `State == Playing`
(`MainWindow.xaml.cs:126-128`) — including while the engine is thinking. The board correctly
goes non-interactive, but the buttons don't. Verified: `Pass()` and `Resign()` both succeed on
the computer's turn, so you can pass *on the computer's behalf* or resign its game. The pending
engine move is then silently dropped by the colour check.

**Fix:** gate all three on `!_game.IsComputerTurn`, not just on `State == Playing`.

---

## Rules & game-flow bugs

### 5. `IsComputerTurn` has an operator-precedence bug

```csharp
// GameManager.cs:76-79
public bool IsComputerTurn =>
    _state == GameState.Playing &&
    _currentPlayer == StoneColor.Black ? BlackPlayer == PlayerType.Computer
                                       : WhitePlayer == PlayerType.Computer;
```

`&&` binds tighter than `?:`, so this parses as
`(_state == Playing && _current == Black) ? ... : (WhitePlayer == Computer)`. Once the game
leaves `Playing`, it reports whether *White* is a computer regardless of whose turn it is.
Verified: after a finished game with Black (human) to move, `IsComputerTurn == true`.

Currently latent — every caller checks `State == Playing` first — but it's a live landmine for
any new caller. Add the parentheses.

### 6. Dead-stone marking is per-stone, not per-group

`ToggleDeadStone` (`GameManager.cs:202-211`) marks exactly one point. In Go you mark a *dead
group*. Verified: a connected 4-stone group takes **4 separate clicks**. On a 19×19 endgame
with several dead groups this is genuinely tedious.

**Fix:** on click, flood-fill the group via `GetGroupInfo` and toggle the whole set. Bonus:
auto-suggest dead groups (a group with no eye and no escape is dead).

### 7. Undo silently does nothing in scoring

`Undo()` returns `false` when `State == Scoring` (`GameManager.cs:185`), and `UndoButton` is
disabled there. So after two passes, Ctrl+Z is a dead end — you have to notice the separate
"Resume Play" button. Either allow undo to leave scoring, or grey it out *and* surface Resume
more prominently.

### 8. Komi is fixed at 7.5 for every board size

`7.5` is the 19×19 komi. On 9×9 the standard is 5.5 (or 6.5), on 13×13 it's 6.5. `GameManager`
already accepts a `komi` parameter — `MainWindow.NewGame_Click` just never passes one
(`MainWindow.xaml.cs:252-253`).

Worse, `NewGameDialog.xaml:85-88` renders komi as a prominent accent-coloured value under the
label "White's komi (compensation for moving second)" — it reads as a control, but it's a static
`TextBlock`. Users will try to change it. Make it a real input (slider or textbox), defaulted
per board size.

---

## AI quality

The heuristic handles the tactical basics well — I verified it captures an atari'd stone in
**30/30** seeds and extends its own atari'd group in **30/30** seeds. What it lacks is any
positional understanding.

### 9. It fills its own territory (30/30 seeds)

`ScoreMove` rewards adjacency to friendly stones at `friends * 8` per neighbour
(`GoAI.cs:130-134`). A point buried inside your own territory has 3–4 friendly neighbours, so
it scores higher than any useful move. Verified: given a walled-off area with 3 black
neighbours, the AI played into its own area in **30/30** seeds.

That's score-neutral under area scoring, but it wastes every endgame move, throws away the
option to invade or reduce, and makes the endgame look like this (real 9×9 self-play, 60 moves):

```
o . o o o . . . .
. o o o o X X X X
. . . o o o X X X
. o o o o o X X X
. o o o o X X X X
. o o o o o X X X
. o o o o X X X X
o X . . . . . . .
. . . . . . . . .
```

Two solid blobs, no eyes, no territory. **Fix:** penalise playing into a region already enclosed
by your own colour (and reward playing into regions enclosed by the opponent — reduction and
invasion). A cheap proxy: if all four neighbours are your own colour and the move captures
nothing, apply a large negative term.

### 10. Eye shapes are invisible to the engine

No eye detection, no life-and-death, no ladder reading. It won't kill a group it should kill, and
it won't defend a group that's about to die. Out of scope for a full engine, but a simple eye
heuristic (don't fill your own eyes; do fill the opponent's) would lift playing strength a lot
for very little code.

### 11. The opening threshold doesn't scale with board size

```csharp
// GoAI.cs:137
if (board.StoneCount(me) + board.StoneCount(opp) < 50)
```

50 stones is over half a 9×9 board, so the "opening" centre/star-point heuristic runs for nearly
the whole game on 9×9 and barely at all on 19×19. Scale it: `size * size / 6` or similar.

### 12. Random noise swamps the positional signal

`score += rng.Next(0, 14)` (`GoAI.cs:155`) versus `friends * 8` (max ~32), centre bonus
(max 10), star bonus (14). The jitter is bigger than most of the signal, which is why play looks
scattered. Drop to `rng.Next(0, 5)`.

### 13. Minor: the candidate cap counts rejected points

```csharp
// GoAI.cs:39-40
if (evaluated++ >= maxCandidates) break;
if (isLegal is not null && !isLegal(r, c)) continue;
```

`evaluated` increments before the legality filter, so the cap counts points that were thrown
away. Move the increment below the filter.

---

## UI / UX

### 14. Biggest win: territory preview during scoring

Right now scoring is blind. You mark dead stones and the score appears only after you press
"Count Score". Every serious Go client shades each empty region in the owner's colour while you
mark, and updates the estimated score live. You already compute exactly this in
`GoBoard.CountScore` (the flood-fill at `GoBoard.cs:214-256`) — it just isn't surfaced.

**Fix:** extract that flood fill into a public `TerritoryMap` (owner per point), and have
`GoBoardControl` paint translucent squares/ dots over empty points during scoring. Recompute on
every dead-stone toggle and show a live running score. This single change makes the scoring
phase feel finished.

### 15. Move history is inert

`MoveList` is a `ListBox` of pre-formatted strings with no `SelectionChanged` handler
(`MainWindow.xaml:312-323`, `MainWindow.xaml.cs:166-179`). You can't click a move to review it.
Make it clickable — navigate to that position, highlight the stone, allow "play here instead"
after stepping back. Also consider optional move numbers on stones (a standard Go review aid).

### 16. No SGF save/load or any persistence

For a Go app this is a conspicuous gap: no way to save a game, export it, load a professional
game record, or resume where you left off. SGF is a simple text format and would not take long.
Close the window and the game is gone.

### 17. Computer vs Computer has no pacing

Even once the engine can pass, CvC fires moves back-to-back with no delay, so it's a blur.
Add ~400–700 ms between moves, plus a "pause" control.

### 18. Smaller UI items

- **No handicap support.** Handicap stones are central to Go. Easy win: place N black stones on
  the star points before move 1, and white moves first.
- **No feedback for illegal moves.** The reason goes to the status bar, which is easy to miss.
  Flash the intersection red, or show a tooltip.
- **No keyboard board navigation.** Arrow keys to move a cursor, Enter to place, would help a lot.
- **No sound.** A subtle click on stone placement adds a lot of feel.
- **Duplicated styling.** `MainWindow.xaml` carries a ~90-line `MenuItem` retemplate inline
  (lines 93-207), and `NewGameDialog.xaml` re-declares its own button styles and copies of the
  same colour resources. Move the palette + button/menu styles into `App.xaml` as a shared
  `ResourceDictionary`; both windows get them for free and the palette becomes one place to edit.
- **No application icon.** `App.xaml` has an empty `Application.Resources` and there's no
  `<ApplicationIcon>` in the csproj — the window shows the default WPF icon.
- **Per-frame allocations in the renderer.** `DrawWood` builds a fresh `Random` and `Pen` on
  every render (`GoBoardControl.cs:113-119`), and `DrawHover` clones a brush on every mouse move
  (`GoBoardControl.cs:224`). Cache both as static fields; the wood grain can be baked once.
- **Status text is transient.** Errors like "Superko: …" get overwritten by the next message
  with no history. Consider a short-lived toast.

---

## Environment note (not an app bug)

`DOTNET_ROOT` is set to `C:\Users\Ken\Tools\dotnet`, which only has the **8.0.24** runtime, while
`dotnet` on PATH resolves to `C:\Program Files\dotnet` (SDK 9.0.315, runtime **9.0.17**). So
`dotnet test` and `dotnet run` fail with *"You must install or update .NET to run this
application"* even though .NET 9 is installed. I worked around it with
`DOTNET_ROOT="C:/Program Files/dotnet" dotnet test`. Worth clearing that env var — otherwise
you'll keep hitting it.

---

## Suggested order of work

1. **Make the game end** (items 1, 2, 3, 4) — AI pass heuristic, undo generation bump, thread
   safety, button gating. Nothing else matters much until games finish.
2. **Fix the scoring flow** (6, 7, 14) — group marking + territory preview. This is where the app
   currently feels most unfinished.
3. **Tighten the rules details** (5, 8) — precedence, komi per board size, editable komi.
4. **Improve playing strength** (9–13) — territory/eye awareness above all.
5. **Round out the app** (15–18) — SGF, handicap, pacing, styling consolidation, icon.

Items 1–8 are all small, well-contained changes; the two that need real design work are the AI
pass/territory heuristic (1, 9) and the territory map for scoring (14).
