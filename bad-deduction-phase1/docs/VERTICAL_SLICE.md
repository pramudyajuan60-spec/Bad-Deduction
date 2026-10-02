# The Burning at Saint Velmont — Vertical Slice Bible

Phase 12. The slice is the whole game in miniature: one case, seven days, every
system firing. It lives in `src/BadDeduction.Core/Slice/` so the Godot UI (Phase 11)
can offer it as "Play vertical slice", and in `tests/BadDeduction.Tests/VerticalSlice/`
as the headless balance harness.

## The case

Night of Day 1, 21:00: **Saint Velmont Cathedral burns** with a victim inside.
Accident, cover-up, or something sinister? The hidden truth (grader-visible only):
**Malvr set the fire**. The slice answers the design image's question with the
"sinister" option — and the player is free to conclude otherwise. Not a fixed ending.

- Crime definition: `arson_fatal` ("Deadly Arson", `data/crimes.json`) — fatal arson
  with four evidence templates (accelerant traces, half-burnt ledger, victim's locket,
  barred-from-without door), tuned so the signature case is robustly solvable.
- Scene: `loc_cathedral` ("Saint Velmont Cathedral", `data/locations.json`), tagged
  `sanctuary` + `crime_scene_candidate` + `investigation`.

## The cast

22 characters via `CastGenerator` with a fixed `CastSpec`: **15 civilians + 5 police**.
The player is one hidden genius (their campaign side); the opposing genius is drawn
deterministically from living non-player characters (`agenda.roles` stream). The victim
is drawn from living non-police civilians excluding the player and the opposing
genius; two witnesses are placed at the cathedral and perceive the fire through the
sanctioned knowledge gate. The Malvr-holder is recorded as the incident's hidden
attribution and knows their own deed (a bland inference, like `ActEliminateObstacle`).

## The map

Eight connected locations (`SliceScenario.SliceLocationIds`): cathedral, church,
central market, docks, old town, noble district, city watch post, and the
cathedral plaza approach. The subgraph is verified connected by BFS in tests.

## The seven beats (checks, not scripts)

`SliceBeats.Check(run)` scores each beat against the event log at day boundaries:

| Day | Beat | Condition |
|-----|------|-----------|
| 1 | Incident | `arson_fatal` exists at the cathedral, Day 1 |
| 2 | Investigation | scene discovered by end of Day 2 AND ≥2 interviews |
| 3 | Contradictions | ≥1 flagged contradiction on the record |
| 4 | Social chain reaction | ≥3 `rumor_spread` events |
| 5 | Hidden conflict | ≥1 rumor (rumors are only ever spread by hidden-agenda actions) |
| 6 | Convergence | ≥1 hypothesis at the Believes band |
| 7 | Resolution | arc reaches Day 8 with a believed hypothesis on the board |

## SliceBot policy (deliberately mediocre)

`SliceBot` plays the 7-day arc using only player-legal services:

- **Day 1 (21:00):** walks to the cathedral, waits out the discovery delay, discovers
  the scene and every evidence item.
- **Day 2:** interviews up to 3 witnesses on the incident, surveys the scene,
  pins an "it was arson" hypothesis and attaches all discovered evidence as supporting.
- **Days 3–4:** interrogates every living NPC on their whereabouts at the incident
  minute, runs each statement against surveillance, holds two dialogue exchanges.
- **Days 5–6:** re-interviews witnesses on the incident.
- **Day 7:** reviews the board. Ends at Day 8, 00:00.

The bot exercises the loop; it does not solve optimally.

## Acceptance metrics (measured 2026-10-02, 200 seeds, this machine)

| Metric | Bar | Measured |
|--------|-----|----------|
| Solvable cases | ≥95% | **98.5%** (197/200) |
| Identity dominance (max share vs uniform, 21 eligible) | ≤2.0× | **1.79×** (top holder 17/200) |
| Knowledge-boundary violations | 0 | **0** |
| Easy/Genius contradiction gap (fixed seed 13) | Genius strictly more | **0 vs 1** |
| Replay determinism (same seed + policy ⇒ same hash) | identical | **identical** |
| 200-seed runtime | <10 min | **~29 s** (≈143 ms/seed) |

Solvable (omniscient grader, test-harness only): scene discovered by end of Day 2
AND ≥2 authentic evidence items discovered AND ≥1 living witness at the end.
Two authentic items attached as supporting reach Believes (50+8·2=66), so the
theory *can* harden — a fair path exists, whether or not the mediocre bot takes it.

Also reported per run: average ~3,815 events, ~0.85 flagged contradictions.

## Tuning log

1. **`data/crimes.json` — `arson_fatal` evidence authenticity (content tuning).**
   Before: 3 templates at authentic weights 75/50/65 → P(≥2 authentic) ≈ 0.70 →
   solvable rate measured **70.5%**. After: 4 templates at 90/75/85/80 →
   P(≥2 authentic) ≈ 0.983 → solvable rate measured **98.5%**. The signature case's
   physical evidence is now reliably genuine; the old weights made one case in three
   unfairly thin. (Content, not code — same allowance as the Phase 12 location add.)
2. **`HiddenAgendaService.ActDeflectAttention` — crash fix (not tuning).**
   The patsy pick could coincide with the rumor listener, throwing
   "A character cannot change its relationship with itself" from `SocialService.Adjust`.
   Fixed by excluding the listener: `PickLiving(holderId, listeners[0])`. The slice
   surfaced it because the attributed+discovered arson makes DeflectAttention fire
   often; no existing test covered that geometry.

No `*Rules.cs` constants needed changing — the systems were already balanced; the
slice's failures were content weights and a latent crash.

## Vetted seeds

- Beats (all seven hit): **4, 11, 20** — deception fires, contradictions land.
- Easy/Genius gap: **13** — the seeded deceiver lies about T but tells the truth
  about T−90; the lie clashes inside Genius's 120-minute alibi window but outside
  Easy's 60-minute window (0 vs 1 flagged).

## Reproduce

```
dotnet run --project tests/BadDeduction.Tests -- "VerticalSlice"
```

The 200-seed metrics test takes ~30 s. The full suite (`dotnet run --project
tests/BadDeduction.Tests`) runs everything: 324 tests.

## Deferred (still)

Chase, firearms/combat, transactions, Harbor, Underground, multi-district —
unchanged from the audit. The slice does not need them.
