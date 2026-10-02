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
  `Cast`, `Agenda` (own objectives only — see below), `Events`.
- **NEVER** `Session.State.Truth`, `Session.Debug`, or the `WorldTruth` /
  `HiddenRole` types. The player's side is known via `GameMeta.Campaign`
  (chosen at New Run), never via truth reads.
- **Gameplay state is not truth.** `State.World.Characters` (names, locations,
  activities), `State.Police.Alert`, and `Content` are public game facts and may
  be read directly where no service accessor exists.

## Per-panel knowledge notes

| Panel | Reads | Knowledge note |
|---|---|---|
| WorldView | View, World, Content, Agenda (own), Police.Alert | Objectives shown are the PLAYER's own (known to them). Event feed = player's journal only. NPC activity shown only at the player's location. |
| NpcInspect | View, Social, Investigate | Trust/suspicion are the player's perspective (`Social.View("c_player", id)`). Statements are on-record testimony. |
| TrustDial | Social | Player's perspective only. |
| EvidencePanel | Crime, Investigate, View, Content | Only DISCOVERED evidence, plus unexamined items at the player's current scene. **Reliability % = 40 + 15×witnesses + 10 if you found it** (clamped 5–95) — corroboration-based, never the hidden `Authenticity`. Interpretations = hypotheses referencing the item. |
| TimelinePanel | View (journal), Events, Investigate | Journal = player-knows layer. Compare box = flagged contradictions (public record). |
| MemoryPanel | Cognition, View | Shows the selected NPC's dossier — a playable simplification of the sheet's NPC Memory panel. Memories never contain hidden-role truth by construction. |
| RelationGraph | Social, View | Spokes from You, colored by dominant axis of the player's view. |
| DialoguePanel | Dialogue, Social, View | Phase 6 pipeline; deltas shown are the accepted output's. |
| InvestigationBoard | Crime, Investigate, View, Content | Shared board = hypotheses with `OwnerId == null`. Officer-owned theories stay theirs. |
| Resolution | Crime, Investigate, View, Events | Player-known facts only. The hidden truth stays hidden — even here. |

## Setting skin

Default skin: **1897 dark-fantasy "The Veiled City"** (per the UI/UX sheet).
The audit's setting conflict (modern vs 1897) stays open and reversible: the
`SurveillanceRecord` model is skin-agnostic, and no panel hard-codes modern tech.
