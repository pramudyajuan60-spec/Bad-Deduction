# 2D Explorer Controls (Phase 13)

The **Explore** panel is the 2D open-world presentation layer: a top-down walkable
view of the player's current location. It is presentation only — it never writes
sim state except through the sanctioned services (`World.MoveCharacter`,
`Simulate.Advance`, `Dialogue.Exchange` / `OpeningLine`, `Initiative.Evaluate` /
`TryAccept`).

## Controls

| Input | Action |
|---|---|
| `W` `A` `S` `D` / arrow keys | Walk (260 px/s, clamped to the 1600×1000 grounds) |
| `E` | Talk to the nearest NPC within range, or hear them out when they approach you |
| `1`–`9` | Switch panels (1 = Explore, 2 = World, … 9 = Board) |
| `Tab` | Cycle to the next panel |
| `Esc` | Back to Explore (releases text-field focus first if you were typing) |

Number keys never fire while a text field (dialogue input, save name) has focus.

## Proximity & "!" bubbles

- Walk within ~96 px of an NPC to see a gold prompt: **"E: Talk to {name}"**.
- NPCs with something urgent show a gold **!** bubble overhead. Walk close and the
  prompt reads **"E: Hear out {name} (!)"** — pressing `E` accepts their queued
  initiative (`Initiative.TryAccept`) and they speak first (`Dialogue.OpeningLine`);
  the line lands in the dialogue log marked *(they approached you)* before you type.
- If the NPC left the location, no bubble renders. `E` with no one nearby does nothing.
- Pressing `E` near an NPC with no pending initiative opens normal dialogue with them
  (the old `SelectedNpcId` path, unchanged).

## Exits

Gold-ringed markers at the grounds' edges lead to neighboring locations (one per
neighbor from `Content.Neighbors`, with travel minutes on the label). Walk into one
to travel: `World.MoveCharacter` moves you, then `Simulate.Advance` spends the real
travel minutes. If police sealed the destination, you get
"Access denied — sealed by police." and are nudged back — the cordon is never bypassed.

## Time model

- While Explore is the visible panel and no overlay (New Run / Resolution / Dialogue)
  is open, every **1 real second advances `MinutesPerSecond` game minutes** (default 1,
  configurable on `GameController`). So 1 s ≈ 1 game minute out of the box.
- Time pauses while you read other panels or talk. The menu's `+1h` / `+8h` /
  `Next Day` buttons still work as before.
- NPCs drift around their spots as a pure function of game time
  (`base + (cos, sin) * 24`, seeded per NPC) — visual only, never written to sim state.

## Programmer art

Everything is drawn in code with the `UiTheme` palette (near-black blue grounds,
gold accents): floor rect, deterministic decor shapes (seeded `2d.{locationId}`
stream — same seed, same grounds, every run), location banner, gold exit rings,
blue discs for civilians, steel-grey for police, gold-ringed disc for you.

## Limitations

- No collision with decor (walk-through programmer art), no pathfinding, no
  click-to-move — keyboard only.
- NPC wander is cosmetic; their *real* movement between locations happens via the
  simulation's routines when time advances, and the view rebuilds to match.
- The 2D view does not show hidden truth — same `UI_RULES.md` applies: names,
  locations and activities are public game facts; motives and roles stay hidden.
