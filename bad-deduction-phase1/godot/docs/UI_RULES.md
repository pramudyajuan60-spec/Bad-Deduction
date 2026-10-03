# UI Rules — what the presentation layer may and may not know

The simulation keeps **hidden truth structurally separate** (ADR-003). The Godot UI
must preserve that separation. These rules are checked by grep in CI/review:

```
# forbidden in godot/scripts (except docs):
State.Truth   DebugAccess   WorldTruth   HiddenRole
```

## The rule

- UI scripts read the simulation ONLY through `GameController.Instance.Session`
  and its sanctioned services: `View`, `Cognition`, `Social`, `Relationships`,
  `Investigate`, `Crime`, `Dialogue`, `Simulate`, `Police`, `World`, `Content`,
  `Cast`, `Agenda` (own objectives only — see below), `Events`, `Manipulation`.
- **NEVER** `Session.State.Truth`, `Session.Debug`, or the `WorldTruth` /
  `HiddenRole` types. The player's side is known via `GameMeta.Campaign`
  (chosen at New Run), never via truth reads.
- **Gameplay state is not truth.** `State.World.Characters` (names, locations,
  activities), `State.Police.Alert`, and `Content` are public game facts and may
  be read directly where no service accessor exists.

## Per-panel knowledge notes

| Panel | Reads | Knowledge note |
|---|---|---|
| Explore (2D) | View, World, Content, Agenda (own), Police.Alert, Initiative, Social | The 2D view never writes sim state — all mutations go through `World.MoveCharacter`, `Simulate.Advance`, `Dialogue.Exchange`/`OpeningLine`, `Initiative.Evaluate`/`TryAccept`. NPC trust bars show `Social.View(npcId, "c_player")` (the felt-trust gameplay meter). NPC activity shown only at the player's location. |
| WorldView | View, World, Content, Agenda (own), Police.Alert | Objectives shown are the PLAYER's own (known to them). Event feed = player's journal only. NPC activity shown only at the player's location. |
| NpcInspect | View, Social, Investigate, Manipulation | Trust/suspicion are the player's perspective (`Social.View("c_player", id)`). Statements are on-record testimony. **Observed routines** lists only blocks from `Manipulation.GetLearnedSchedule` — blocks the player actually observed via `ObserveRoutines` (panel open / Observe button); unlearned stretches render as `???`, never the true schedule. |
| TrustDial | Social | Player's perspective only. |
| EvidencePanel | Crime, Investigate, View, Content | Only DISCOVERED evidence, plus unexamined items at the player's current scene. **Reliability % = 40 + 15×witnesses + 10 if you found it** (clamped 5–95) — corroboration-based, never the hidden `Authenticity`. Interpretations = hypotheses referencing the item. |
| TimelinePanel | View (journal), Events, Investigate | Journal = player-knows layer. Compare box = flagged contradictions (public record). |
| MemoryPanel | Cognition, View | Shows the selected NPC's dossier — a playable simplification of the sheet's NPC Memory panel. Memories never contain hidden-role truth by construction. |
| RelationGraph | Social, View | Spokes from You, colored by dominant axis of the player's view. |
| DialoguePanel | Dialogue, Social, View, Manipulation | Phase 6 pipeline; deltas shown are the accepted output's. The profile box shows **the NPC's trust toward the player** (`Social.View(npcId, "c_player")` — the direction dialogue deltas actually move), their manipulability tier (`Manipulation.TierOf`, fixed at run start), and their despair meter (`Manipulation.DespairOf`, rises from your own exchanges). All player-known; no truth reads. |
| InvestigationBoard | Crime, Investigate, View, Content | Shared board = hypotheses with `OwnerId == null`. Officer-owned theories stay theirs. |
| Resolution | Crime, Investigate, View, Events | Player-known facts only. The hidden truth stays hidden — even here. |

## Setting skin

Default skin: **1897 dark-fantasy "The Veiled City"** (per the UI/UX sheet).
The audit's setting conflict (modern vs 1897) stays open and reversible: the
`SurveillanceRecord` model is skin-agnostic, and no panel hard-codes modern tech.
