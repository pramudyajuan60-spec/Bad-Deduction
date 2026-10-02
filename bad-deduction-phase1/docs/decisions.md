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

---

## Phase 2 — Characters: what exists and why

```
CastGenerator (session.Cast) ── one named RNG stream per stage; sim RNG untouched
  people → names → personality/secrets → relationships → goals → schedules → place at schedule
WorldState  +Profiles (personality, goals, secrets, household)  +Schedules  +Relationships
ScheduleSystem  BlockAt / PlaceAtScheduledPositions / Check     RelationshipGraph  Connect / Components
ContentDatabase +Occupations +Goals +Secrets +Names +TravelMinutes (shortest path)
```

## ADR-007 — Cast generation has its own RNG streams, and ids are shuffled
`cast.people`, `cast.names`, `cast.personality`, `cast.secrets`, `cast.social`, `cast.goals`,
`cast.schedules` are derived from the RunSeed (ADR-002), so tuning one stage never reshuffles another and
generating a cast leaves `SimRng` untouched. Character ids (`c_01`..) are assigned after a shuffle: an id
reveals nothing about occupation, household or (later) hidden role.

## ADR-008 — Schedules are arrival deadlines; travel happens at the tail of a block
A block `[Start, End)` says where a character is; `Start` is when they must have *arrived*. Leaving
happens `TravelMinutes(here, next)` before the next block starts. `ScheduleSystem.Check` enforces: blocks
tile 0..1440, locations exist, each block is long enough to travel onward, and the day loops (starts and
ends at home). A separate `DayOff` list applies when `(day-1) % 7 == DayOffIndex`; police have no day off
so the station is always staffed in the 7-day slice. Phase 5 turns blocks into movement.

## ADR-009 — Relationships: directed pair of edges + a category tag; numbers added in Phase 3
Every relationship is two directed edges (A→B, B→A) so each side can diverge later. `RelationshipKind`
(Family, Friend, Colleague, Rival, Acquaintance) drives the graph view and goal targets. The trust /
fear / respect / loyalty / suspicion / influence / affection / resentment axes are Phase 3 (save v3).
The generator guarantees one connected component, so rumors can eventually reach everyone.

## ADR-010 — Household-first generation; the cast pool is 17 civilians + 5 police
People are generated in households (single, spouses, siblings, parent/child) so ages, homes and
surnames stay consistent; occupations are then drawn against age ranges and `MaxCount`. `MinCount`
guarantees required roles (exactly one guard captain). The audit's slice is "15 NPCs + Malvr + Lumiel",
so Phase 10 will pick both geniuses *from the 17 civilians*; no role or player edges exist yet.

## Save format v2
Adds `World.Profiles`, `World.Schedules`, `World.Relationships`. Migration v1→v2 adds the empty containers.
Content cross-checks (known goal/secret ids, schedule feasibility) run in `GameSession.FromState`.

## Defaults used for the open questions in the audit (section H)
Engine choice, setting skin, slice incident, portraits and LLM hosting were not answered, and none of
them affects Phase 2. Names/occupations are era-neutral placeholders; swap the JSON when the skin is decided.

---

# Phase 3 — Social system

## ADR-011 — Every axis is "From's view of To"; two edges, two truths
`RelationshipEdge` holds eight integer axes (0-100). All describe the *From* side: how much From trusts,
fears, respects, is loyal to, suspects, is fond of and resents To, and `Influence` = how much To sways From.
A→B and B→A are separate, so a betrayal one side never learns about changes only the other side's numbers.
Decisions made by X about a request from Y always read the edge X→Y.

## ADR-012 — Resting values are a pure function; strangers are acquaintances
`SocialBaselines.Resting(kind, fromPersonality, toPersonality)` gives a relationship's starting values *and*
the level its volatile axes settle back to. No RNG and nothing stored, so the same cast always gets the same
relationships and no stream is shifted. Design §9's "ordinary NPC trust = 20" is the Acquaintance row.
A pair with no edge reports the same numbers (`RelationshipView.Exists == false`), so creating the edge on
first contact never makes a value jump. Personality nudges the baseline by at most ±5-10 points.

## ADR-013 — Only volatile axes drift
Fear (3/day), suspicion (2/day) and resentment (1/day) relax toward resting values on `DayChanged`. Trust,
respect, loyalty, affection and influence move only through events: a betrayal is not forgotten by the
calendar, and police trust (90 at the start) does not quietly erode. Drift is silent; deliberate changes
(`SocialService.Adjust`) are logged as `social.relationship_changed` with a required reason, the *clamped*
amount actually applied, and an optional `causedBy`, extending the causal chain of ADR-004.

## ADR-014 — Compliance is a sum of named, signed factors
`ComplianceEvaluator` returns `score = Σ factors`, so a refusal can be explained ("morality -65") and the AI
debugger can show it. The relationship can pull at most 98 points; a maximally risky and immoral request
pushes back 123, so no axis value, including Trust 100, forces an action. Fear *can* compel a trivial order
(coercion) but not a dangerous one. Resistance covers risk (softened by Courage), morality (felt more by
honest people), loyalty/affection/fear toward whoever would be hurt, and the decider's own goals.
Beliefs, evidence and emotional state arrive in Phase 4 through the `extraFactors` parameter, so no
rework is needed. Evaluating is pure: it never mutates state.

## ADR-015 — Police ladder and the hidden-identity boundary
`SocialRules.StanceOf(trust)`: >=70 cooperate, >=45 question, >=25 verify independently, else suspect the
subject. `SeedPoliceTrust(subjectId)` sets all officers' trust (default 90, design §10) but takes the subject
as a parameter and never reads `Truth`; Phase 10 decides who Lumiel is and calls it. A test proves the social
state is identical whoever holds a hidden role.

## Save format v3
Edges gain the eight axes. Migration v2→v3 seeds them from each edge's kind with neutral personalities (v2 saves
carry no social history). The validator rejects any axis outside 0-100.

## Deferred / known limits
* Trust/suspicion dials in `Desain UIUX.png` need a `PlayerView` projection of NPC attitudes toward the player.
  Left to Phase 11 so nothing leaks early; the numbers are reachable only through `session.Social` today.
* Influence has no hierarchy yet (e.g. guard captain over guards). It needs an occupation rank in
  `data/occupations.json`; baselines use kind and personality only.
* Tuning constants live in `SocialRules` / `SocialBaselines`; move them to `data/` when balancing starts (§61).

---

# Phase 4 — Memory, knowledge, beliefs, rumors

```
CognitionService (session.Cognition) ── the only sanctioned mutator of CognitionState
  Perceive ── THE knowledge gate: grants KnownEvents + stores a MemoryEntry + journals for the player
  TellRumor ── contact-gated (same location or a relationship edge); telephone-game degradation
  AddEvidence ── integer evidence counts -> confidence; band transitions logged
  ApplyDailyDecay ── DayChanged subscription: silent fading, one-shot distortion, foggy floor
CognitionState (on GameState) ── NextMemoryId · CognitionRng · Memories · Beliefs · KnownEvents · PlayerJournal
PlayerView ── KnownEventIds / JournalEntries: the knowledge-gated "player knows" layer
```

## ADR-016 — Knowledge has exactly one gate: Perceive
`KnownEvents[character]` (the "knows" layer) is written only by `CognitionService.Perceive`; `TellRumor`
routes through the same gate. Nothing else in the codebase may grant knowledge, so "an NPC cannot know
what it never received" holds by construction, not by convention — the exit criterion is a structural
property, and a test pins it. Perceive is idempotent: re-perceiving a known event changes nothing.

## ADR-017 — The five knowledge layers have five homes
Audit §B's layers map to: truth → `WorldTruth` (never read by cognition code), "knows" → `KnownEvents`,
"believes"/"suspects" → `Beliefs` confidence bands (≥60 believes, 40-59 suspects, <40 dismissed),
"player knows" → `PlayerJournal`, read only through `PlayerView.KnownEventIds()/JournalEntries()`.
`IsFoggy` is derived from confidence (never stored), per the "no derived data" rule.

## ADR-018 — Belief math is integer, documented, and explainable
`confidence = clamp(50 + 8·(for − against), 5, 95)`: one supporting item (58) is a suspicion, two (66) a
belief, two refuting items (34) a dismissal; clamping means no belief is ever absolute or impossible.
Only band *transitions* are logged (`cognition.belief_changed` with for/against counts); quiet
accumulation stays in state. A belief is therefore always explainable as "N for, M against".

## ADR-019 — Rumors need contact and degrade like a telephone game
`TellRumor` requires the teller to know the event and the pair to have met (same `CurrentLocationId`
or a relationship edge in either direction) — otherwise it throws. Each hop loses 0-15 confidence and
has a 50% chance to garble the retelling ("[retold]" prefix), drawn from the cognition RNG stream.
The spread is logged as `cognition.rumor_spread` with a `CausedBy` link, extending the causal chain.

## ADR-020 — Memories fade silently, distort once, and never vanish
Daily decay on `DayChanged`: witnessed −2/day, told/inferred −4/day, floored at 5 (a trace always
remains). The first time confidence sinks below 40, one RNG roll (50%) may distort the memory
("[hazy]" prefix, `IsDistorted` set — never retried, never stacked). Decay is silent like social drift
(ADR-013): no per-memory log spam. Starting confidence: witnessed 90, told 70, inferred 55.

## ADR-021 — Cognition has its own persisted RNG stream
`CognitionState.CognitionRng` is derived from the run seed (`"cognition.memory"`) at `NewRun` and
persisted in the save, so rumor/distortion draws neither shift the main sim stream (ADR-002) nor
diverge after save/load — proven by a save→continue hash-identity test. The v3→v4 migration derives
the same stream from the old save's run seed, so migrated saves keep working deterministically.

## Save format v4
Adds `State.Cognition` (memories, beliefs, known events, journal, cognition RNG). Migration v3→v4
initializes empty containers plus the derived RNG stream. The validator checks: memory id range and
uniqueness, character/event references, confidence 0-100, non-empty summaries and propositions,
non-negative evidence counts, and a non-zero cognition RNG.

## Deferred / known limits
* Memories are never forgotten outright (floor 5); Phase 5+ may add consolidation/pruning caps.
* `TellRumor` re-telling to someone who already knows is a no-op; reinforcement ("heard it twice")
  is future work.
* Belief bands feed `ComplianceEvaluator.extraFactors` in Phase 6 (dialogue), per ADR-014's plan.

## Note — why the secrets data file is called `character_secrets.json`
The Phase 2 patch shipped without its secrets data because `.gitignore` (rightly) ignores `secrets.json`
as a credentials pattern, so the file was silently never committed and 61 of 74 tests failed on a fresh
clone. Game content about characters' private lives is not a credential, but the name collides with the
pattern that secret scanners and global gitignores match. The file is therefore `data/character_secrets.json`
and the credential ignore rule is untouched. Rule of thumb: never name game data after a credential pattern,
and always verify a change on a fresh clone, not only in the working tree.
