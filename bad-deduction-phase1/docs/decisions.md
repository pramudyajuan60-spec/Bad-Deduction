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

---

# Phase 5 — World simulation

```
WorldSimulation (session.Simulate) ── the world tick
  Advance ── wraps TimeSystem.Advance minute-by-minute, then runs the sim for that minute
  TierOf ── Spotlight (player's location, 1-min) / Near (everyone else, 15-min) / Background (reserved)
  EvaluateRoutine ── schedule blocks -> departures (blockEnd - travelMinutes) -> activity
  TravelState (on WorldState.ActiveTravels) ── persisted in-progress trips
  character.departed -> character.moved (CausedBy-linked) -> character.activity_changed
  witnessing ── departures seen at the origin, arrivals at the destination, via Cognition.Perceive
DefaultRoutine ── home-centric fallback for characters without an authored schedule
```

## ADR-022 — Two live tiers, one reserved seam
`SimulationTier.Spotlight` (characters sharing the player's location) evaluates routine decisions
every sim-minute; `SimulationTier.Near` (everyone else) every 15 sim-minutes, so background
characters react to schedule changes up to 14 minutes late — an honest, documented cost of the
coarser tier. `SimulationTier.Background` (daily cadence) exists in the enum and the cadence map
but is unassigned at the 22-character scale: no off-slice population exists yet, and inventing one
would be fake granularity. Travel completion is tier-independent (a per-minute countdown), so
arrivals stay minute-exact for every tier and the tier difference is purely decision latency,
which tests pin down (1-minute vs 15-minute reaction).

## ADR-023 — Simulate.Advance wraps Time.Advance; Time.Advance stays sim-free
The simulation does NOT subscribe to `MinuteElapsed`. `WorldSimulation.Advance(n)` advances
`TimeSystem` one minute at a time and runs the tick after each minute, so day/hour boundaries,
social drift and memory decay all fire in their established order before the sim sees the new
minute. `Time.Advance` keeps its Phase 1 contract — raw time with no characters moving under it —
which existing systems and tests (notably the cognition "decay is silent" test) rely on. The game
loop (Phase 11) calls `session.Simulate.Advance`; anything calling `session.Time.Advance` directly
opts out of the living world deliberately.

## ADR-024 — Travel takes real minutes and survives save/load
Departures follow ADR-008: a block's `Start` is the arrival deadline, so the sim leaves
`TravelMinutes(here, next)` before the next block starts; displaced or late characters head
straight for the current block. The in-progress trip (`TravelState`: from/to, departure/arrival
minutes, departure event id) is persisted on `WorldState.ActiveTravels`, so saving mid-travel and
continuing is hash-identical to an uninterrupted run — pinned by a test. All location changes go
through `WorldService.MoveCharacter` (validation + `character.moved` preserved). The sim uses no
RNG at all: every decision is a pure function of state, content and time, so no new RNG stream
was needed; personality flavor lives in the generated schedules (Phase 2), not in movement.

## ADR-025 — Departures and arrivals are witnessed where they happen
Starting a trip records `character.departed` (origin, from/to, departure/arrival minutes) and the
living characters at the origin — traveler included — `Perceive` it immediately. On arrival,
`character.moved` is recorded with `CausedBy` pointing at the departure, and the traveler plus
everyone at the destination `Perceive` it. The dead perceive nothing. Witnessing lives in the
simulation, not in `WorldService.MoveCharacter`, so hand-driven moves (tests, later UI) stay quiet
and existing move semantics are untouched. Knowledge still flows only through the Phase 4 gate.

## ADR-026 — Event-volume policy
`character.activity_changed` is logged only on actual transitions (departure, arrival, block change),
never per minute; daily decay-style silence applies. A trip therefore costs exactly: one
`character.departed`, one `character.moved`, two activity transitions, and one memory per witness —
all of them real happenings a debugger or timeline wants to see.

## Save format v5
Adds `World.ActiveTravels`. Migration v4→v5 adds the empty table (older saves could not have been
mid-travel). The validator checks: known character/location references, id match, no nowhere-trips,
non-negative times, arrival after departure, a recorded departure event, and the two-way invariant
(active trip ⟺ Traveling and alive).

## Deferred / known limits
* The player character is never simulated (a human drives them in Phase 11); in headless runs they
  idle where placed but still witness and journal like anyone present.
* Schedule-less characters run a home-centric `DefaultRoutine` (no travel); it is a fallback for
  hand-built worlds, not the plausibility showcase — the generated cast carries real schedules.
* Interruptions (being stopped, detained, lured away) are Phase 6+; the sim currently always
  follows the schedule, recovering by heading for the current block when displaced.
* No memory consolidation/pruning caps yet (carried over from Phase 4).

---

# Phase 6 — AI dialogue

```
DialogueOrchestrator (session.Dialogue)
  Exchange ── context → provider → validator → apply via Social/Cognition/EventSystem
  ContextEngine ── prompt from ONE NPC's knowledge only (no GameState/WorldTruth/DebugAccess refs)
  IAIProvider ── synchronous; AIResponse is Accept(output) or Refuse(reason)
  DialogueValidator ── malformed / role-revealing / unknown-entity checks; deltas clamped ±20
  MockAIProvider ── FNV-1a(seed, request) templates; the CI baseline
  DifficultySystem ── memory/belief context caps + contradiction-sensitivity hook (Phase 8)
dialogue.exchanged ── every exchange stored as a world event; the sim never re-calls the provider
```

## ADR-027 — The provider interface is synchronous
`IAIProvider.Complete` is a plain synchronous call returning `AIResponse` (accept or refuse).
No real network exists in this codebase, and async would add nothing but ceremony; when a real
provider arrives it can block its own thread or run on a worker — the pipeline shape does not
change. Refusal is a normal result (risk 5), never an exception.

## ADR-028 — Truth cannot reach a prompt by construction
`ContextEngine` takes only truth-free surfaces (`PlayerView`, `CognitionService`,
`SocialService`, `RelationshipGraph`, `ContentDatabase`, the profiles table, and two plain
delegates for location/clock). It holds no `GameState`/`WorldTruth`/`DebugAccess` reference —
there is simply nothing to leak through. A reflection test asserts no type in `BadDeduction.AI`
has a field, property or constructor parameter of those types, and a dedicated leak test builds
prompts for every NPC with roles assigned and asserts no role word or holder-linkage appears.
The speaker's own secrets are deliberately excluded from the prompt for now: guarding them is
Phase 10 (hidden objectives) work, and no validator rule could catch a slip today.

## ADR-029 — The validator rejects lies about identity, clamps loud numbers
`DialogueValidator` rejects output that is malformed (empty/over-long reply, too many or
over-long facts), role-revealing (the whole words "malvr"/"lumiel", or any forbidden phrase —
e.g. "{name} is Malvr"), or entity-leaking (a capitalized name in new facts/memories that
appears nowhere in the speaker's known texts: names, places, their own memories, beliefs, the
utterance). The forbidden set is derived from hidden roles in `GameSession` — OUTSIDE the AI
namespace — and passed in as plain strings, so the boundary stays clean. Trust/suspicion
deltas are *clamped* to ±20, not rejected: a loud number is a calibration issue, not a lie.
The entity check is intentionally conservative (it can false-positive on sentence-initial
verbs); fail-safe beats clever here, and the fallback path keeps the game moving.

## ADR-030 — Mock determinism without RNG streams
`MockAIProvider` is a pure function of `(runSeed, speaker, listener, conversation, utterance)`
via FNV-1a 64-bit hashing: fixed reply/fact templates with small integer deltas (±4). It never
touches an RNG stream, so adding dialogue can never shift another system's draws (ADR-002), and
mock-driven runs are replay-identical — the CI baseline for risk 1. Templates are written to
pass the validator (no role words, only entities the speaker knows: the listener's name and
lowercase prose).

## ADR-031 — One fallback for every failure mode
Refusals (risk 5), validation rejections, and budget exhaustion (risk 4) all funnel into the
same deterministic fallback: a canned in-fiction line picked by `StableHash(seed, conversation,
exchangeIndex)` — zero deltas, still recorded as a `dialogue.exchanged` event and perceived by
both participants. The game never throws at the player for a provider problem. The
per-conversation budget (10 exchanges) is transient on the orchestrator, never saved:
conversations are UI-session scoped, and a fresh budget after load is documented behavior.

## ADR-032 — Difficulty scales cognition, never omniscience
`DifficultySystem` maps Easy/Medium/Hard/Genius to context memory caps (3/5/8/12) and belief
caps (2/4/6/10): a Genius NPC remembers more of what it perceived, but still only what it
perceived (ADR-003/ADR-016). `ContradictionSensitivity` (25/50/75/100) is exposed now as the
Phase 8 hook; nothing reads it yet.

## ADR-033 — Accepted output is stored; the sim never re-calls the provider
Every exchange — accepted or fallback — is persisted as a `dialogue.exchanged` world event
(utterance, reply, fallback flag) with both participants perceiving it through the Phase 4
gate, trust/suspicion applied via `SocialService.Adjust` ("dialogue"), and heard facts added
as listener evidence via `CognitionService.AddEvidence`. Save → load → continue is therefore
hash-identical without any provider involvement, pinned by a test. No save-format change was
needed: conversations are transient; their outcomes are ordinary world events.

## Deferred / known limits
* Real providers (Claude/OpenAI/Gemini/local) are future `IAIProvider` implementations; the
  pipeline shape already fits them (structured output + refusal as a value).
* Provider timeouts/retries and per-conversation token budgets are not modeled; the exchange
  budget is a count cap only.
* The speaker's secrets and hidden objectives do not enter the prompt yet (Phase 10).
* Contradiction detection is Phase 8; the sensitivity knob is parked until then.

## Note — assembly split not attempted (ADR-003 candidate)
ADR-003 floated enforcing the truth boundary with a separate `Core.Abstractions` assembly in
Phase 6. Not done: the reflection test (`Ai_namespace_never_touches_truth_types`) enforces the
same invariant — no `GameState`/`WorldTruth`/`DebugAccess` in any `BadDeduction.AI` member
signature — with far less build complexity, and it fails loudly the moment someone adds a
violating member. An assembly split remains an option if engine-side code ever needs the same
guarantee, but nothing in Phase 6 justified the packaging cost.

---

# Phase 7 — Crime

```
CrimeService (session.Crime) ── the only sanctioned mutator of CrimeState
  GenerateIncident ── data-driven CrimeDefinition -> victim (never the player) -> sealed scene
                      + evidence with drawn, immutable authenticity; own RNG stream
  CheckDiscovery ── arrival-driven via the WorldEventRecorded bus (character.moved);
                     discovery delay honored; witnesses Perceive through the Phase 4 gate
  DiscoverEvidence ── knowledge-gated: you cannot investigate what you never heard of
  GetWitnesses ── derived from KnownEvents (living perceivers only); query-only
  GetTimeline ── incident -> discovery -> evidence discoveries, time-ordered, causal
CrimeState (on GameState) ── CrimeRng · NextCrimeId/NextEvidenceId · Crimes · Scenes · Evidence
data/crimes.json ── murder (first-class, fatal) + arson (variant, surviving victim)
```

## ADR-034 — Incidents are data, victims are drawn, the player is never eligible
`CrimeDefinition` (`data/crimes.json`) carries victim kinds, fatality, location tags, the discovery
delay and evidence templates — the slice incident is data, not code (audit §C-2). Victim selection
is a seeded draw over living non-player characters (preferred at tag-matching locations, with a
documented fallback to anyone eligible), so the same seed always yields the same incident. The
player character is excluded structurally, whatever the definition says. Nothing in the draw reads
`WorldTruth`: role assignments cannot change the incident, pinned by a test (ADR-003/ADR-015).

## ADR-035 — Authenticity is immutable by construction
`Evidence.Authenticity` has no setter and no method anywhere assigns it; it is fixed by the
`[JsonConstructor]` on load and by the weighted draw at creation. "False evidence never auto-becomes
true" is therefore a structural property: the test suite asserts the missing setter via reflection
and runs every public mutator (discovery, evidence examination, rumor, 20k minutes of decay) over a
false item without it changing. A hand-tampered save (unknown authenticity string) fails loudly at
deserialization through the strict schema (ADR-005).

## ADR-036 — Discovery rides the arrival bus
`CrimeService` subscribes to `WorldEventRecorded` and runs `CheckDiscovery` on every
`character.moved` event — so the Phase 5 simulation and hand-driven moves (tests, later UI) discover
scenes with identical semantics, and `WorldSimulation` needed no changes. Discovery requires: a
living character, an undiscovered scene at their location, and the definition's discovery delay
elapsed. On discovery, `crime.discovered` is recorded (CausedBy → the incident) and everyone
present perceives the incident through the Phase 4 gate — the discoverer, the other witnesses and
the future timeline all flow from that one gate (ADR-016/ADR-025).

## ADR-037 — Evidence examination is knowledge-gated
`DiscoverEvidence` requires the scene to be discovered, the examiner to be physically at the scene,
and — via `CognitionService.Knows` — to actually know about the incident. A late arrival who never
heard of the crime cannot investigate its evidence until someone tells them (rumor, dialogue).
The find is logged as `crime.evidence_discovered` (CausedBy → the scene's discovery event) and
perceived by everyone present. Authenticity is recorded in the event data — the log is ground truth,
not knowledge — and is never modified.

## ADR-038 — Witnesses are derived, timelines are views
`GetWitnesses` returns the living characters who know the incident event: no redundant witness
table to drift out of sync, and no event spam. `GetTimeline` filters the event log by the crime id
carried in every crime event's data and orders by (timestamp, id); because discovery links
CausedBy → incident and evidence discovery links CausedBy → discovery, `CausalChain` on the last
timeline event reproduces the timeline root-first. Both are read-only views for the Phase 8+
investigation board.

## ADR-039 — Crime has its own persisted RNG stream
`CrimeState.CrimeRng` is derived from the run seed (`"crime.incident"`) at `NewRun` and persisted,
so victim/evidence draws never shift the main sim stream (ADR-002) and continue identically after
save/load — proven by a save→load→continue hash-identity test mid-investigation. The v5→v6
migration derives the same stream from the old save's run seed.

## Save format v6
Adds `State.Crime` (crimes, scenes, evidence, counters, crime RNG). Migration v5→v6 initializes
empty containers plus the derived RNG stream. The validator checks: id/key consistency, victim and
location references, incident/discovery event references, timestamp ranges, discovery-state
consistency (discovered ⟺ discoverer + timestamp + discovery event), evidence→scene references,
non-empty labels/summaries, and defined authenticity values. `ValidateAgainstContent` checks crime
definition ids against `data/crimes.json`.

## Deferred / known limits
* The seal (`LocationState.IsSealed`) is currently a marker only: nothing stops characters walking
  into a sealed scene. Cordon enforcement is Phase 9 (police AI).
* Characters present at the incident location when it happens do not auto-discover; discovery
  needs an arrival after the delay (or an explicit `CheckDiscovery` call). "Was present at the
  incident" logic belongs to Phase 8 investigation.
* One victim per incident in Phase 7; multi-victim incidents (e.g. larger arsons) are future work.
* Unsealing / case resolution states are not modeled yet (Phase 9+).
