# Architecture Decisions

## Phase 1 — Foundation: what exists and why

```
GameSession (composition root for one run)
 ├─ GameState ............ single authoritative, plain-data, serializable root
 │    Meta · TotalMinutes · SimRng · World · Truth · EventLog · Player
 ├─ TimeSystem ........... advances minute by minute; emits Day/TimeOfDay/Hour/Minute messages
 ├─ EventSystem .......... transient pub/sub bus  +  persistent, causally-linked WorldEvent log
 ├─ WorldService ......... validated mutations (add/move characters), logs movement events
 ├─ HiddenIdentitySystem . assigns Malvr/Lumiel roles into WorldTruth
 ├─ PlayerView ........... gameplay-safe projection (no truth)      DebugAccess ← gated truth
 └─ DeterministicRandom .. xoshiro256** wrapping GameState.SimRng

SaveSystem   versioned envelope · migration hook · strict schema · atomic file write · state hash
ContentDatabase   data/locations.json → validated location graph (undirected, timed edges)
GameStateValidator  integrity checks run on every load
```

Data flows one way: **content (static) + GameState (dynamic) → systems → events → state.** Nothing
stores derived data (e.g. the day number is computed from `TotalMinutes`).

## ADR-001 — Engine-agnostic C# core, presentation later
The simulation is the product's hard part. It lives in `BadDeduction.Core` (net8.0, zero engine
references) and is tested headless. The engine (recommended: Godot 4 .NET) only renders it.
*Consequence:* the engine choice can change without touching simulation code.

## ADR-002 — Own PRNG, named streams, RNG state inside GameState
`System.Random` seeded sequences are not guaranteed stable across runtime versions. We use
xoshiro256** (SplitMix64-seeded), verified against an independent Python implementation.
`DeterministicRandom.Derive(seed, "name")` gives independent streams so adding a consumer never
shifts another's sequence. The live generator mutates `GameState.SimRng` in place, so it cannot
drift out of sync with a save.

## ADR-003 — World truth is structurally separate
Hidden roles live in `GameState.Truth`, not on `CharacterState`. UI code uses `PlayerView`
(`PublicCharacterInfo` has no role fields, guarded by a test); truth is reachable from the UI side only
through `DebugAccess`, which throws unless debug mode is on.
*Known limitation:* engine-side code (e.g. the Malvr AI) can still read `GameState` directly.
Enforcement by assembly boundary (`Core.Abstractions`) is a candidate for Phase 6.

## ADR-004 — Persistent causal event log
Every notable happening is a `WorldEvent` with `CausedBy`. This gives the debugger the
"murder → discovery → rumor → arrest" chain for free (`EventSystem.CausalChain`) and is the
substrate for witness memory in Phase 4. Transient bus messages (minute ticks etc.) are never saved.

## ADR-005 — Saves are deterministic bytes
No wall-clock timestamp inside the save; strict deserialization (`UnmappedMemberHandling.Disallow`);
version + migration dictionary from v1; atomic write (temp + move); load runs the validator and a
content cross-check. `ComputeStateHash` (SHA-256 of canonical JSON) is the equality oracle used by
determinism tests. Dead characters are never removed from `Characters`, which also keeps dictionary
enumeration order stable.

## ADR-006 — Dependency-free test harness
NuGet was unreachable in the build sandbox, so `tests/BadDeduction.Tests` is a console app with an
xunit-shaped `[Fact]`/`Assert` harness. Migrating to xunit is mechanical (swap `using`). The suite was
mutation-checked: deliberately breaking RNG/state sync, causal validation, and day-boundary logging each
made a test fail.

## Conventions for later phases
* Trust, suspicion, fear, confidence: **integers** (0–100), never floats, to stay deterministic across platforms.
* No `DateTime.Now`, `Guid.NewGuid()`, or `System.Random` inside simulation code.
* New persisted fields ⇒ bump `SaveSystem.CurrentFormatVersion` and add a migration.
* Never let LLM output touch `GameState`; it goes through the validator.
