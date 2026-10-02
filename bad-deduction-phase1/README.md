# Bad Deduction

A systemic psychological-crime and social-deduction simulation: autonomous characters with memory,
beliefs, secrets and relationships; two hidden geniuses (**Malvr** and **Lumiel**); and investigations
that emerge from the simulation instead of a scripted solution.

* Design references (unchanged, at repo root): `Desain AI Decision System.png`, `Desain UIUX.png`,
  `Desain Vertical Slice.png`, `Desain mapkota.png`, `Rangkuman Semua Image.png`
* Audit, conflicts, roadmap and open questions: [`docs/00-repository-audit.md`](docs/00-repository-audit.md)
* Architecture decisions: [`docs/decisions.md`](docs/decisions.md)

## Status

**Phases 1-12 complete — roadmap done.**
* Phase 1 (Foundation): deterministic RNG, game time, event bus + causal world-event log, versioned
  save/load with state hashing, location content, truth-vs-player-view separation.
* Phase 2 (Characters): seeded cast generation (`session.Cast.Generate(new CastSpec())` → 17 civilians
  + 5 police), households, personality, secrets, relationship graph, goals, and daily/day-off schedules
  with travel-feasibility checks. Save format v2.

* Phase 3 (Social): eight directed relationship axes per edge (trust, fear, respect, loyalty, suspicion,
  influence, affection, resentment), `session.Social` (validated, logged changes + daily calming), the
  police trust ladder, and `session.Compliance` (explainable "will they do it?" decisions where trust alone
  never forces an action). Save format v3.

* Phase 4 (Cognition): `session.Cognition` — memories with daily decay/distortion, the 5-layer knowledge
  model (truth / knows / believes / suspects / player-knows), beliefs with integer confidence + evidence
  counts, contact-gated rumor spread (telephone game), and a knowledge-gated player journal.
  Save format v4.

* Phase 5 (World simulation): `session.Simulate` — schedule-driven routines with two live tiers
  (Spotlight 1-minute / Near 15-minute; Background reserved), travel over timed edges with persisted
  in-progress trips, `character.departed`/`character.moved`/`character.activity_changed` events, and
  arrival/departure witnessing through the Phase 4 knowledge gate. Save format v5.

* Phase 6 (AI dialogue): `session.Dialogue` — Orchestrator → ContextEngine → IAIProvider → Validator
  pipeline. Prompts are built from one NPC's knowledge only (never truth); a deterministic
  `MockAIProvider` is the CI baseline; every exchange (accepted or fallback) is stored as a
  `dialogue.exchanged` world event, so the sim never re-calls the provider on load. No save-format
  change (conversations are transient). Opt-in **Ollama provider** (`OllamaDialogueProvider`,
  local LLM, same validator, silent mock fallback when unreachable) — see `docs/OLLAMA_SETUP.md`.

* Phase 7 (Crime): `session.Crime` — data-driven `CrimeDefinition`s (`data/crimes.json`: murder as
  the first-class type, arson as a variant), seeded incident generation (victim never the player),
  sealed scenes, evidence with structurally immutable authenticity (false evidence can never become
  true), arrival-driven discovery through the Phase 4 knowledge gate, derived witnesses, and a
  causal timeline view. Save format v6.

* Phase 8 (Investigation): `session.Investigate` — surveillance as a pure query over the movement
  log (`SurveillanceRecord`: who was where, when), structured interviews from each NPC's own
  knowledge (free text stays in `session.Dialogue`), interrogations under pressure (recorded +
  a suspicion nudge), statements as testimony, difficulty-gated contradiction detection
  (blatant always flagged; subtle by sensitivity; Genius holds wider alibi windows), explicit
  surveillance cross-checks, and hypotheses with integer confidence reused from belief math
  (false evidence may support one — it stays false). Merged case timelines for the board.
  Save format v7.

* Phase 9 (Police AI): `session.Police` — independent officers with their own case files
  (owned hypotheses; divergence from different knowledge plus deterministic per-officer
  evidence readings), dynamic trust-ladder evaluation from each officer's own theories,
  a city alert ladder (Calm → Alert → Manhunt, driven by unsolved violence), cordon
  enforcement on discovered sealed scenes (police and the player pass), daily duty rosters
  (Guard/Patrol/Investigate) that steer officers' work blocks in the sim, and bounded
  arrests (Suspect stance + Believes-band theory + 2 supporting evidence). Save format v8.

* Phase 10 (Malvr/Lumiel): `session.Agenda` — seeded hidden-identity assignment (the player's
  side is their campaign; the opposing genius is drawn uniformly from eligible NPCs via the
  `agenda.roles` RNG stream), hidden objectives (Malvr: EliminateObstacle/SowDistrust/EvadeSuspicion;
  Lumiel: ProtectTarget/GatherAlly/PursueLead) with leak-free lifecycle events, a daily strategic
  tick driving the NPC-held genius (one deterministic action per day from their own knowledge
  only — never WorldTruth), and interview deception (genius holders may lie about whereabouts
  near known crimes; the lie is recorded normally so Genius contradiction detection can catch it).
  Save format v9.
* Phase 11 (UI/UX): `godot/` — Godot 4.7 (.NET) presentation layer: `GameController`
  autoload owns one `GameSession`; ten panels per the UI/UX sheet (World, NPC inspect +
  trust dials, Evidence, Timeline + compare statements, NPC memory, social graph,
  dialogue + response deltas, investigation board) plus New Run and Resolution screens.
  Playable Day 1–7 loop with the Mock provider: travel, talk, interview/interrogate,
  examine evidence, pin hypotheses, time controls, save/load. UI truth-gating rule
  enforced (no script touches `State.Truth`/`DebugAccess`/`WorldTruth`; see
  `godot/docs/UI_RULES.md`). 1897 "The Veiled City" skin. No save-format change.
* Phase 12 (Vertical slice): `src/BadDeduction.Core/Slice/` — "The Burning at Saint
  Velmont": 22-character cast (15 civilians + 5 police), 8 connected locations,
  Day-1 fatal cathedral arson; `SliceBot` headless player; 7 pacing beats as checks;
  balance metrics over 200 seeds (98.5% solvable, 0 knowledge violations, Easy/Genius
  contradiction gap, ~29 s). Slice bible: [`docs/VERTICAL_SLICE.md`](docs/VERTICAL_SLICE.md).
  No save-format change.

324 tests. Presentation layer: `godot/` (Godot 4.7 .NET project, 10 panels, playable
Day 1–7 loop with the Mock provider — requires the Godot 4.7.2-stable .NET editor;
see `godot/README.md`).

## Layout

```
src/BadDeduction.Core/     engine-free simulation library (net8.0)
  Core/  World/  Characters/  Social/  Cognition/  Content/  AI/  Crime/  Investigation/  Police/  Agenda/
tests/BadDeduction.Tests/  dependency-free test runner
data/                      data-driven content (locations, occupations, goals, character secrets, names, crimes)
docs/                      audit, roadmap, decisions
godot/                     Godot 4.7 .NET presentation layer (scenes/, scripts/, data/ snapshot)
```

## Build and test

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build BadDeduction.sln
dotnet run --project tests/BadDeduction.Tests            # all tests
dotnet run --project tests/BadDeduction.Tests -- Save    # only tests whose name contains "Save"
dotnet run --project tests/BadDeduction.Tests -- VerticalSlice  # slice beats + 200-seed metrics (~30 s)
```

## Rules for contributors

* Cast generation uses one named RNG stream per stage (`cast.people`, `cast.social`, ...); never draw from the sim RNG.
* Simulation code must be deterministic: use `DeterministicRandom`, never `System.Random`,
  `DateTime.Now` or `Guid.NewGuid()`.
* Trust/suspicion/confidence are integers.
* Hidden truth is only reachable through `DebugAccess`; UI code uses `PlayerView`.
* The LLM never mutates `GameState`; its output is validated first.
* **Never commit API keys or credentials.** Use environment variables or secure runtime settings.
