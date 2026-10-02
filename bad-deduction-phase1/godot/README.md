# Bad Deduction — Godot 4 UI (Phase 11)

Playable presentation layer over the engine-free `BadDeduction.Core` simulation.
Dark 1897 "The Veiled City" skin, gold-on-dark, per the UI/UX concept sheet.

## Requirements

- **Godot 4.7.2-stable** editor with .NET support (`Godot_v4.7.2-stable_mono_linux.x86_64`
  or the Windows/macOS .NET build — the plain editor cannot build C#).
  The project pins `Godot.NET.Sdk` **4.7.2** (see `BadDeduction.Godot.csproj`); the
  editor version must match the SDK major.minor.
- **.NET 8 SDK** (the editor uses it to compile the game assembly).
- `data/*.json` is a snapshot copy of `../data/` (content the game loads at runtime).

## Open in the editor

1. Godot 4.7.2 .NET editor → Import → select `godot/project.godot`.
2. Wait for the first C# build to finish (bottom panel).
3. Press F5 (or ▶). The New Run screen appears.

## Play

1. **New Run**: pick a seed (same seed + same actions = same city), your side
   (Lumiel = Order, Malvr = Chaos — you always know your own side), difficulty.
2. **World**: travel between locations, see who's with you, read what you know.
   A Day-1 murder is generated for you to investigate.
3. **NPC / Dialogue / Memory / Graph**: inspect, talk (free text, mock provider),
   interview / interrogate, review memories, see the social graph.
4. **Evidence / Timeline / Board**: examine evidence at scenes, pin hypotheses,
   attach evidence, compare statements, watch the 7-day timeline.
5. **Time**: +1h, +8h, Next Day. The city lives while you investigate.
6. Past Day 7 → **Resolution**: what YOU established. Save/Load anytime.

## Architecture

```
Main.tscn (Main.cs)
 ├─ menu bar: panel switcher, time controls, save/load
 ├─ ContentHost: 8 switchable panels (each .tscn + script, IPanel.Refresh())
 ├─ NewRunPanel / ResolutionPanel overlays
 └─ autoload Game (GameController.cs): owns ONE GameSession
```

Panel → service map (detail in `docs/UI_RULES.md`):

| Panel | Primary services |
|---|---|
| WorldView | View, World, Content, Agenda (own objectives), Police (alert) |
| NpcInspect + TrustDial | View, Social, Investigate |
| EvidencePanel | Crime, Investigate, View |
| TimelinePanel | View, Events, Investigate |
| MemoryPanel | Cognition, View |
| RelationGraph | Social, View (custom `_Draw` node-link) |
| DialoguePanel | Dialogue (Phase 6 pipeline), Social |
| InvestigationBoard | Crime, Investigate, View |

The golden rule: **no script touches `State.Truth`, `DebugAccess`, or
`WorldTruth`**. Verified by grep (see Phase 11 report).

## Headless smoke test

```
godot --headless --path . -- --autotest
```

Starts a seeded run, refreshes every panel, runs a dialogue exchange and an
interview, advances 3 days, saves, and quits. Any script error fails the run.

## Deferred / simplified (Phase 11 scope)

- Relation graph: functional node-link spokes from You (not a full force layout).
- Corkboard: hypothesis cards with confidence/evidence links (no draggable strings).
- Portraits: initial-letter placeholders (art pipeline is future work).
- NPC memory panel shows the dossier directly (documented simplification).
- Player movement is instant (travel-time simulation applies to NPCs).
- Provider is the deterministic Mock; real LLM providers plug into `IAIProvider`.
