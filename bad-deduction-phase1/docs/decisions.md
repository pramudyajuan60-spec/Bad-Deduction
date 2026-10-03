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

---

# Phase 8 — Investigation

```
InvestigationService (session.Investigate) ── the investigator's toolkit
  SurveyLocation ── presence reconstructed purely from the movement log (SurveillanceRecord)
  Interview ── structured topics (event / person / whereabouts) answered from the speaker's
                 own knowledge; free text stays in DialogueOrchestrator.Exchange (Phase 6)
  Interrogate ── interview under pressure: flagged on the event + a suspicion nudge
  RecordStatement ── testimony (not ground truth) -> contradiction check (difficulty-gated)
  CheckAgainstSurveillance ── investigator-initiated alibi check; always raised when it hits
  ProposeHypothesis / AttachEvidence ── board theories; integer confidence via BeliefConfidence
  GetCaseTimeline ── incident chain merged with the case's investigation actions, time-ordered
InvestigationState (on GameState) ── NextStatementId/HypothesisId/ContradictionId ·
  Statements · Contradictions · Hypotheses. No RNG anywhere in this subsystem.
```

## ADR-040 — Surveillance is a derived query, not a system
`SurveyLocation` reconstructs presence from `character.moved`/`character.departed` events only:
the initial position is recovered by walking moved-events backward from the character's current
position (departures never change `CurrentLocationId` — only arrivals do), then a forward pass
builds the location timeline, with travel intervals as genuine absences. No new persisted state,
no RNG, fully deterministic. The request itself is logged as `investigation.surveyed` so it
appears on the case timeline. The record is deliberately setting-agnostic (audit §C-1): CCTV
footage, a watchman's log or gate ledgers are skins over the same mechanic.

## ADR-041 — Interviews are structured; free text stays in the dialogue pipeline
`Interview` answers three topic shapes — `event:{id}`, `person:{id}`, `whereabouts:{minute}` —
from the speaker's own knowledge: event answers quote the speaker's memory summary (or the
canonical "don't know"), person answers describe the relationship (kind + trust band) or admit
strangership, whereabouts answers reconstruct the speaker's own movement record into a
structured claim (`at:{loc}@{min}[#{detail}]` / `traveling:{from}>{to}@{min}`). NPCs are
currently truthful about whereabouts (their own record); deliberate lies arrive with hidden
objectives in Phase 10. Free-text conversation remains `DialogueOrchestrator.Exchange` (Phase 6):
the two pipelines share the knowledge gate but serve different UI (interrogation panel vs
dialogue panel). `Interrogate` is an interview under pressure — flagged in the event data and
carrying a real +5 suspicion nudge toward the interrogator via `SocialService.Adjust`.

## ADR-042 — Contradictions are scored, then gated by difficulty
The incompatibility model is deliberately small and exact: whereabouts claims are structured,
so "two places at once" is arithmetic (|Δminutes| < window, different places → Blatant, 100);
same place + same window + conflicting details → Subtle (50); denied-knowledge-then-claimed
(or the reverse) on one topic → Subtle (60); changed accounts on one topic → Subtle (50).
A clash is flagged when its score reaches `101 - ContradictionSensitivity(difficulty)`:
Easy(25)→76 blatant-only, Medium(50)→51 adds denial tells, Hard(75)→26 adds all subtle,
Genius(100)→1 flags everything and widens the alibi window to 120 minutes (vs 60).
Every detected clash is persisted; sub-threshold ones are kept unflagged and unlogged —
so Easy and Genius runs over identical testimony produce the measurably different detection
rates the vertical slice's acceptance metrics demand. `CheckAgainstSurveillance` is the
investigator explicitly checking an alibi: a hit is always Blatant and always raised.

## ADR-043 — Hypotheses reuse belief math; false evidence may support them
A hypothesis is a board-level theory: confidence is derived (never stored) from attached
evidence counts via `CognitionRules.BeliefConfidence` — the same `50 + 8·(for−against)` integer
formula as character beliefs, so theories and minds speak the same language. Only band
transitions log `investigation.hypothesis_updated`. Evidence must exist and be discovered
(ADR-037's knowledge gate extends to theorizing), but FALSE evidence attaches freely — that
is the core of the game — and its authenticity never changes (ADR-035). Proposition ids are
unique: one card per theory.

## ADR-044 — The case timeline is a merge, not a store
`GetCaseTimeline` filters the event log for the crime id carried in every crime and
investigation event's data (explicit `crimeId` parameter beats the auto-derived link from
incident-event topics) and orders by (timestamp, id). Nothing new is persisted: the timeline
is a read-only view for the Phase 11 investigation board, and `CausalChain` still reproduces
any of its threads root-first.

## Save format v7
Adds `State.Investigation` (statements, contradictions, hypotheses, counters). Migration
v6→v7 initializes empty containers with fresh counters — there is no RNG in this subsystem,
so nothing needs seeding. The validator checks: id/key consistency, speaker/character/event/
crime references, valid topic grammar, non-empty claims within the length cap, valid severity
values, hypothesis proposition uniqueness, and evidence references that exist and are not
double-attached.

## Deferred / known limits
* NPCs do not lie about whereabouts yet; contradiction-vs-surveillance only bites on
  injected/testimony claims until Phase 10 gives characters hidden objectives.
* Statements have no per-statement reliability % yet (the UI panel in `Desain UIUX.png`
  shows one); confidence lives on memories and hypotheses, not on testimony.
* No statement retraction/editing: testimony is append-only, like the event log.
* Background-tier surveillance (city-wide, low-detail) awaits off-slice populations (Phase 12).

---

# Phase 9 — Police AI

```
PoliceService (session.Police) ── independent officers, the alert ladder, cordons, duties
  OpenCase / AssessEvidence ── per-officer case files (owned hypotheses, namespaced
    "{officer}:suspect:{subject}:{crime}"); knowledge-gated; deterministic readings
  EvaluateSubject ── each officer's own theories move trust/suspicion (Phase 3 ladder follows)
  EvaluateAlert ── Calm → Alert → Manhunt from case facts (event-driven + daily)
  CanEnter ── cordon rule wired into WorldService.AccessCheck + the sim's travel check
  AssignDuties / DutyLocationFor ── Guard/Patrol/Investigate roster; duty steers work blocks
  Arrest ── bounded: Suspect stance + Believes theory + 2 supporting evidence → InCustody
PoliceState (on GameState) ── Alert · OfficerRng (lazy per-officer streams) · Duties · Cases
```

## ADR-045 — Officers diverge by knowledge and by reading, never by omniscience
Two legitimate divergence sources, both truth-free. (1) Knowledge: officers form and assess
hypotheses only from evidence *they* know (`CognitionService.Knows` on the evidence's discovery
event) — an officer who missed a discovery works a thinner file, by construction. (2) Reading:
`ReadingOf(officer, evidence, subject)` is a pure function of `(runSeed, officer, evidence,
subject)` via `DeterministicRandom.Derive` — Supports/Inconclusive/Refutes at 70/20/10 — so two
officers given identical evidence can attach different for/against counts and land at different
confidences, deterministically and order-independently. Nothing here reads `WorldTruth`; a
reflection test pins the same ban the AI namespace carries.

## ADR-046 — Owned hypotheses namespace the board
`Hypothesis.OwnerId` (null = the shared investigator board; otherwise an officer id) lets each
officer keep their own case file while reusing the whole Phase 8 machinery — confidence math,
band transitions, evidence attachment rules. Officer propositions are namespaced
`{officerId}:suspect:{subjectId}:{crimeId}`, so the global proposition-uniqueness rule still
holds. `OpenCase` is knowledge-gated: an officer who never heard of the incident cannot open
the file. `AssessEvidence` attaches only known evidence, read the officer's own way, then
updates the minimal case state (Open → PersonOfInterest on a Believes-band theory).

## ADR-047 — The trust ladder is driven per officer by their own theories
`EvaluateSubject` turns an officer's case file into relationship changes: each owned Believes
hypothesis moves Trust −8 / Suspicion +10 toward the subject, Suspect-band moves −3/+4, and a
Dismissed theory with 2+ refuting items partially exonerates (+5/−3). The Phase 3 stance ladder
(cooperate/question/verify/suspect) then follows from the numbers — no separate police attitude
store to drift out of sync. Nudges double during a Manhunt. All changes go through
`SocialService.Adjust` with reason "police case evaluation", so they are logged and explainable.

## ADR-048 — The alert ladder steps on facts, never jumps
`AlertLevel` (Calm → Alert → Manhunt) recomputes from case facts and moves at most one rung per
evaluation: a fatal incident undiscovered for 720 minutes or 2+ open cases raises toward Alert;
2+ open fatal cases or 3+ open cases raise toward Manhunt; it steps back down only when nothing
is open. Evaluation is event-driven (`crime.incident`, `crime.discovered`, `police.arrest`) plus
a daily pass for the undiscovered-timeout. Effects are concrete and implemented: duty coverage
scales (Guard on every sealed scene at Alert+, patrol slots 1/2/all by level) and evaluation
nudges double at Manhunt. This required one ordering fix in Phase 7: `GenerateIncident` now
builds the crime/scene/evidence records *before* publishing `crime.incident`, because subscribers
must see the state the event describes — the same state-first convention `WorldService` already
followed.

## ADR-049 — Cordons guard discovered scenes; the player holds a consultant's pass
`PoliceService.CanEnter` denies non-police entry to locations whose sealed scene is *discovered*:
an undiscovered scene has no cordon because nobody knows to guard it yet — which also preserves
Phase 7 discovery semantics exactly (the first arrival must be able to walk in). Police officers
and the player character (the civilian consultant) pass. Enforcement hooks into
`WorldService.MoveCharacter` via a nullable `AccessCheck` delegate (null = allow-all, so all
pre-cordon behavior and hand-built test worlds are untouched): denials log
`police.access_denied` and return null. The simulation checks the same gate *before* starting
travel, so cordoned civilians idle instead of spamming denials, and mid-travel arrivals that go
bad cancel cleanly.

## ADR-050 — Duties steer work blocks; rosters are opt-in
`AssignDuties()` builds one duty per officer per day — Guard on sealed scenes (round-robin by id
order), Patrol on a deterministic per-officer draw over unsealed locations, Investigate at the
officer's station for the rest — and logs `police.duty_assigned`. `DutyLocationFor` is the seam
`WorldSimulation` reads: an officer's `Working` blocks run at the duty location instead of the
station. Rosters are opt-in per run (no auto-assign in `NewRun`, so no existing test's event log
changed); `DayChanged` re-assigns daily once a roster exists. Per-officer RNG streams are derived
lazily (`"police.{officerId}"`) and persisted, used only for tie-breaks and patrol choices.

## ADR-051 — Arrest is bounded and logged; guilt is not decided here
`Arrest` requires: a living police officer, a living subject not already detained, the officer's
stance at SuspicionOfSubject, their own hypothesis at the Believes band, and ≥2 discovered
supporting evidence items. It sets the minimal case state (InCustody), logs `police.arrest` with
the hypothesis and confidence, and the simulation skips detained characters. A wrong arrest is
possible — the theory may rest on false evidence — and is simply part of the log; trial,
sentencing and win/lose stay out of scope (audit §D).

# Phase 10 — Malvr/Lumiel: hidden identities, objectives, strategic AI, deception

## ADR-052 — Seeded role assignment: player knows their side, the rival is drawn
The player's genius side IS their campaign (`GameMeta.Campaign` — no new API; the player
always knows their own side per the audit). The opposing genius is drawn deterministically
from the `(runSeed, "agenda.roles")` RNG stream, uniformly among eligible NPCs. Eligible =
living, non-player characters (police included — a hidden genius with a badge is legitimate;
documented as a design choice). The uniform-share exit criterion (no NPC gets Malvr more than
2× uniform share over 240 seeds) is enforced by test, not by weighting. Manual `AssignRole`
remains for tests; `AssignHiddenRoles()` is the canonical seeded path.

## ADR-053 — Objectives are truth-side; lifecycle events never name roles
`HiddenObjective` {Id, HolderId, Kind, TargetId?, TargetId2?, CrimeId?, Status, CreatedAt} lives
in `HiddenAgendaState` (truth-side like `WorldTruth`, only visible via `DebugAccess`). Status
transitions log `agenda.objective_completed` / `agenda.objective_failed` with objective id and
kind ONLY — never the holder's role. Incident attribution (which genius orchestrated which crime)
is stored ONLY in `HiddenAgendaState.IncidentAttribution`, never in event data; the crime itself
is public, the link to the orchestrator is hidden. The holder is never in the incident's
participants.

## ADR-054 — The strategic tick drives the NPC-held genius, one action per day
`HiddenAgendaService.StrategicTick()` runs on `DayChanged` (and manually). It drives ONLY
NPC-held geniuses — the player's own genius side is player-driven (documented). Each holder
takes at most ONE action per day (`LastActionDay` gate). Action priority: Malvr —
DeflectAttention (if heat) → EliminateObstacle → SowDistrust → SpreadRumor; Lumiel —
ProtectTarget → PursueLead → GatherAlly. All actions use only the holder's own knowledge
(`CognitionService`) plus public roster/contact info.

## ADR-055 — Knowledge discipline: the genius AI never reads WorldTruth
The director may read the role roster (to know WHO the holders are), but each genius's DECISIONS
use only: their own role (identity), their own knowledge via `CognitionService` (the ONLY knowledge
source), and public information (roster, contacts, locations). The AI never reads other characters'
hidden roles, secrets, or WorldTruth. `TellRumor` enforces the knowledge gate (throws if the speaker
doesn't know the event). Tested by asserting every rumor/action references only known entities/events.

## ADR-056 — Deception is a seam in the interview path, not a special case
`HiddenAgendaService.MaybeDeceive(characterId, topic, truthfulClaim)` returns an alternate claim
or null. It fires only when: the speaker secretly holds a genius role (never the player), the topic
is whereabouts, and the topic minute is within 180 minutes of a crime the speaker knows. The
decision is deterministic via `StableHash(seed, "deceive", speaker, topic)`. The false claim is
recorded NORMALLY via the standard statement path — so Genius-difficulty contradiction detection
(`CheckAgainstSurveillance`) can catch it. That's the intended gameplay loop: the lie is a fair
clue, not a cheat.

## ADR-057 — Both campaigns tick the NPC-held genius
With campaign=Malvr, the player IS Malvr (knows it via `GameMeta`), and the NPC Lumiel runs the
Lumiel AI. With campaign=Lumiel, vice versa. `StrategicTick` drives whichever genius is NPC-held.
No special-casing by campaign in the action logic — the role determines the behavior.

## Save format v9
Adds `State.Agenda` (objectives, incident attribution, per-holder last-action days, agenda RNG
stream). Migration v8→v9 installs an empty agenda state; the RNG stream derives from
`(runSeed, "agenda.roles")`. The validator checks: objective holder/target references, valid
kind/status values, attribution crime references, and non-zero RNG stream.

## Deferred / known limits (updated)
* Officers do not yet act autonomously on their case files (no self-directed interviews or
  evidence hunts); `OpenCase`/`AssessEvidence` are explicit calls for the Phase 11+ game loop
  or AI director to drive.
* No police hierarchy yet (captain vs guards); duty assignment is flat round-robin.
* Unsealing scenes and full case resolution (trial/win/lose) are future work.
* The interrogation-pressure effect of higher alert levels is currently expressed through
  doubled evaluation nudges; direct pressure mechanics belong to a future pass.
* Phase 10: genius objectives are fixed at seeding (2 per holder); dynamic objective generation
  mid-run (e.g. new obstacles emerging) is future work. The player's own genius actions are
  player-driven (no AI assistance); a "suggest move" helper is future work.

## Deferred / known limits
* Officers do not yet act autonomously on their case files (no self-directed interviews or
  evidence hunts); `OpenCase`/`AssessEvidence` are explicit calls for the Phase 11+ game loop
  or AI director to drive.
* No police hierarchy yet (captain vs guards); duty assignment is flat round-robin.
* Unsealing scenes and full case resolution (trial/win/lose) are future work.
* The interrogation-pressure effect of higher alert levels is currently expressed through
  doubled evaluation nudges; direct pressure mechanics belong to a future pass.

## Phase 11 — Godot 4 presentation layer

### ADR-052 — Godot 4.7 (.NET) project layout
`godot/` holds a standard Godot 4 project: `project.godot` (main scene
`res://scenes/Main.tscn`, autoload `Game`), `BadDeduction.Godot.csproj` targeting
net8.0 with `Godot.NET.Sdk` 4.7.2 and a `<ProjectReference>` to
`../src/BadDeduction.Core`. The editor version must match the SDK (4.7.x); the
`.NET`-flavored editor build is required (the plain editor cannot compile C#).
`data/*.json` is a committed snapshot copy of `../data/` — one source of truth
lives at repo root; the copy exists because Godot exports pack `res://`.

### ADR-053 — UI truth-gating rule
UI scripts may read the session only through sanctioned services
(`View`, `Cognition`, `Social`, `Relationships`, `Investigate`, `Crime`,
`Dialogue`, `Simulate`, `Police`, `World`, `Content`, `Cast`, `Agenda` for the
player's OWN objectives). `State.Truth`, `DebugAccess`, `WorldTruth` and
`HiddenRole` are forbidden in `godot/scripts` (checked by grep). Gameplay state
(`State.World.Characters`, `State.Police.Alert`) is not truth and may be read
where no service accessor exists. The player's side is known via
`GameMeta.Campaign`, chosen at New Run.

### ADR-054 — Panel → service mapping
World→View/World/Content/Agenda; NPC inspect→View/Social/Investigate;
Evidence→Crime/Investigate/View; Timeline→View/Events/Investigate;
Memory→Cognition/View; Graph→Social/View (custom `_Draw`); Dialogue→Dialogue
pipeline; Board→Crime/Investigate/View. Each panel implements `IPanel.Refresh()`;
`Main` switches and refreshes.

### ADR-055 — Reliability % is knowledge-based
The sheet's "reliability %" is NOT the hidden `Authenticity` (that would leak
truth). It is `clamp(40 + 15×witnesses + 10 if you found it, 5, 95)` —
corroboration-based. Interpretations come from hypotheses referencing the item.

### ADR-056 — What "playable" covers (and doesn't)
Playable = New Run (seed/campaign/difficulty) → travel, talk, interview/
interrogate, examine evidence, pin/attach hypotheses, time controls (+1h/+8h/
Next Day), save/load, Day-1 murder, Resolution screen of player-known facts at
Day 8, all with the Mock provider. Simplified: node-link graph spokes (no force
layout), corkboard as hypothesis cards (no draggable strings), letter
placeholders for portraits, instant player travel, dossier-style NPC memory
panel. Real LLM providers plug into `IAIProvider`; win/lose stays out of scope.

### ADR-057 — Headless smoke test
`godot --headless --path . -- --autotest` starts a seeded run, refreshes every
panel, runs a dialogue exchange and an interview, advances 3 days, saves, and
quits — any script error fails the run. This is the closest CI gets to "playable"
without a display.

### ADR-058 — Vertical slice assembly ("The Burning at Saint Velmont")
`BadDeduction.Core/Slice/SliceScenario.cs` builds the canonical slice so the Godot
UI can offer "Play vertical slice" (scenario-building lives in Core; only the
metrics harness lives in tests). 22 characters (15 civilians + 5 police via
`CastGenerator` with a fixed `CastSpec`), 8 connected locations, Day-1 21:00 fatal
arson (`arson_fatal`) at `loc_cathedral`. The player is one genius per campaign;
the opposing genius is seeded. The slice's hidden truth: Malvr set the fire —
recorded ONLY as `Agenda.IncidentAttribution` (never in events), with the holder
knowing their own deed via a bland inference. Victim and witnesses are chosen on
dedicated `DeterministicRandom` streams (`slice.victim`) so setup draws never
disturb the sim stream (ADR-002). New content: `loc_cathedral` in
`data/locations.json`, `arson_fatal` in `data/crimes.json` (content, not code).

### ADR-059 — SliceBot: deliberately mediocre, player-legal only
`BadDeduction.Core/Slice/SliceBot.cs` is a deterministic headless player with a
fixed simple policy (scene visit → interviews → evidence → hypothesis → alibi
sweep → review). It uses ONLY the services the UI uses — never `WorldTruth`.
Mediocre on purpose: it exercises the loop, it does not solve optimally. Policy
documented in `docs/VERTICAL_SLICE.md`.

### ADR-060 — Solvable = a fair path exists (omniscient grader)
`SliceMetrics.IsSolvable` is a TEST-HARNESS-ONLY grader that may read ground truth,
clearly marked as such. Solvable = scene discovered by end of Day 2 AND ≥2
authentic evidence items discovered AND ≥1 living witness at the end. Rationale:
two authentic items attached as supporting reach the Believes band (50+8·2=66), so
a competent player's theory CAN harden. Solvable does not require the mediocre bot
to solve it.

### ADR-061 — Beats are checks, not scripts
`SliceBeats` defines the seven pacing beats (incident → investigation →
contradictions → chain reaction → hidden conflict → convergence → resolution) as
observable conditions over the event log at day boundaries. The sim must produce
them emergently; a systematically failing beat means tuning, never scripting.
Vetted seeds (4, 11, 20 hit all seven; 13 shows the Easy/Genius gap) are
documented in `docs/VERTICAL_SLICE.md`, not special-cased in code.

### ADR-062 — Phase 12 tuning log
(a) `arson_fatal` evidence authenticity weights (content): 75/50/65 over 3
templates → 90/75/85/80 over 4 templates. Measured solvable rate 70.5% → 98.5%
(bar ≥95%). The old weights made one case in three unfairly thin for the
signature crime. (b) `ActDeflectAttention` crash fix: the patsy pick could equal
the rumor listener → `SocialService.Adjust` threw on self-relationship. Fixed with
`PickLiving(holderId, listeners[0])`. No `*Rules.cs` constant needed changing.

### ADR-063 — Slice performance
Single-seed 7-day run ≈ 143 ms; the 200-seed metrics suite finishes in ~29 s
(CI budget <10 min). No optimization was needed; per-minute sim ticks over
~10,440 minutes × 22 characters stay cheap because idle re-evaluation is already
gated. Measured 2026-10-02 on the dev machine; re-measure on CI.

### ADR-064 — Ollama as the local LLM provider (user choice)
The user playtested and confirmed all dialogue is templates (the mock). Given the
cloud-vs-local choice, they picked **Ollama**: free, local, no API key, no data
leaving the machine. Rationale recorded: privacy-by-default fits a game about
secrets, and zero cost removes the hosting question for a vertical slice. Cloud
providers remain possible later — the provider slot is interface-based.

### ADR-065 — Ollama provider: same slot, same prompt, sync contract kept
`BadDeduction.AI.OllamaDialogueProvider` implements the existing `IAIProvider`
(drop-in). It sends `AIRequest.ContextPrompt` VERBATIM as the single user message
to `POST {endpoint}/api/chat` (`stream: false`); it never invents prompt content,
so ContextEngine's truth/knowledge separation holds unchanged (pinned by a
prompt-parity test: outgoing body content == the request's ContextPrompt, plus the
role-word leak check). Pure BCL (`HttpClient` + `System.Text.Json`) — Core stays
dependency-free. The interface is synchronous (ADR-027), so the provider blocks on
the async HttpClient API via `GetAwaiter().GetResult()`; this is deadlock-safe
because HttpClient never marshals continuations back to a sync context, and unlike
`HttpClient.Send` it works with ANY `HttpMessageHandler` (the sync `Send` virtual
has no default delegation and throws `NotSupportedException` — found via a failing
test, fixed, test added).

### ADR-066 — Fallback policy: any failure → mock, never crash
On ANY transport or content failure (not running, timeout, non-2xx, malformed
JSON, empty/unparseable content) the provider delegates to its fallback
(normally the mock) and marks the result `AIResponse.UsedFallback = true`
(additive flag; the mock never sets it). The orchestrator copies it to
`DialogueResult.UsedFallback`, which the Godot UI shows as an "(offline dialogue)"
chip. The game NEVER throws or hangs because Ollama is missing. Distinction kept
deliberate: `UsedFallback` = "the model was unreachable" (offline indicator),
`FallbackReason` = "output refused/rejected/budget" (deflected indicator).

### ADR-067 — Determinism boundary: replay hashes hold only with the mock
With Ollama active, dialogue output is inherently non-deterministic, so identical
seeds no longer produce identical state hashes. The mock REMAINS THE DEFAULT;
Ollama is opt-in per run from the New Run panel (provider choice is UI-session
state, not saved — loading a save re-applies the current panel selection, with
the run seed re-read so the mock fallback matches). Mock-driven CI is unaffected:
all 324 tests use the mock or fake handlers; no test ever hits a real server.

### ADR-068 — Default model and parser judgment calls
Default `llama3.1:8b`: the best quality/size trade-off widely available in Ollama
at the time; any tag works via settings (lighter: `llama3.2:3b`, `qwen2.5:7b`).
Free-text parsing is lenient and line-based (reply = text before the first
`trust_delta:`/`suspicion_delta:`/`new_facts:`/`memory_summary:`/`relationship_note:`
header; missing fields degrade to zero deltas, not refusal). The provider does
NOT truncate over-long fields — over-length stays the validator's documented
rejection, keeping one gate for output shape. `new_facts` splits on newlines and
semicolons only (never commas, which appear inside facts).

## Phase 13 — 2D open-world presentation + smarter NPC dialogue

### Save format v9 (unchanged)
Phase 13 adds `State.Initiative` (NPC-initiative queue, cooldowns, RNG stream) and `PoliceState.Disturbances`, both purely additive: property initializers supply defaults for older saves, and `GameSession` zero-guards the initiative RNG stream (deriving `(\runSeed, "npc.initiative")` when absent). No migration needed; `GameStateValidator` additionally checks disturbance references.

## ADR-080 — 2D presentation / simulation separation

**Context.** Phase 13 adds a 2D top-down explorer on top of the existing simulation.
The sim already enforces UI_RULES.md (no `State.Truth` / `WorldTruth` in view code).

**Decision.** The 2D layer is presentation-only: it may move the camera, draw
programmer art, and run pure functions of (seed, location, game time), but it NEVER
writes sim state directly. All mutations go through the already-sanctioned services:
`World.MoveCharacter` (travel, honoring police cordons), `Simulate.Advance` (time),
`Dialogue.Exchange` / `Dialogue.OpeningLine` (talk), `Initiative.Evaluate` /
`TryAccept` (NPC-initiated chats). Grep checks from UI_RULES.md apply unchanged.

**Consequences.** The 2D view cannot desync hidden truth because it cannot touch it;
any Core-side balance change propagates automatically. Visual bugs stay visual.

## ADR-081 — Per-location procedural scenes

**Context.** 12 location nodes need walkable grounds without hand-authored art.

**Decision.** Each location is generated in code at `BuildLocation`: a 1600×1000
dark-fantasy ground, 6–10 decor shapes, a name banner, and one gold exit marker per
neighbor from `Content.Neighbors`. Layout comes from
`DeterministicRandom.Derive(runSeed, "2d." + locationId)` (decor/exits) and
`Derive(runSeed, $"2d.{locationId}.npc.{npcId}")` per NPC — never `System.Random`,
whose seeded sequence is not stable across runtimes (see DeterministicRandom docs).
Same seed + location ⇒ identical grounds on every run and every rebuild.

**Consequences.** Zero art assets to maintain; layouts are reproducible for bug
reports. Programmer art is visibly programmer art (documented in 2D_CONTROLS.md).

## ADR-082 — Time-scale model for the explorer

**Context.** Walking in real time while the sim thinks in minutes needs a bridge.

**Decision.** While the Explore panel is visible and no overlay (New Run /
Resolution / Dialogue) is open, each real second advances
`GameController.MinutesPerSecond` game minutes (default 1) via the existing
`AdvanceMinutes` path — so the `TimeAdvanced` signal keeps every panel in sync.
Travel through exits uses the real neighbor minutes from `Content.Neighbors`.

**Consequences.** Time only flows while exploring, never while reading panels or
mid-conversation. The scale is one tunable int; NPC routines and the Day-7
resolution behave exactly as with the menu time buttons.

## ADR-083 — Proximity + initiative UX

**Context.** Two conversation triggers must coexist: player-initiated (walk up, press
E) and NPC-initiated (the NPC wants to talk).

**Decision.** Proximity (< 96 px) shows a contextual prompt; `E` opens normal
dialogue via the existing `SelectedNpcId` path. NPCs with a pending initiative
(`Initiative.Evaluate`, re-run on every Refresh and time advance) render a gold "!"
bubble; `E` near them runs the accept flow — `TryAccept` → `OpeningLine` →
`Main.OpenDialogueWith`, which stashes the opener on
`GameController.PendingNpcOpener` for `DialoguePanel.Refresh` to consume once,
rendering the NPC's line before the player types. If the NPC left, no bubble
renders; `E` with nobody near does nothing.

**Consequences.** One key, two flows, no modal popups interrupting movement. The
opener seam is a single consume-once property — simple, but a second opener
overwrites the first (acceptable: initiatives are accepted immediately at the NPC).

## ADR-084 — Why SubViewport for the 2D world

**Context.** The explorer needs its own camera, world coordinates, and draw order
without disturbing the panel UI.

**Decision.** Host the world in a `SubViewportContainer` + `SubViewport` (2D, own
viewport, GUI input disabled) with a `Camera2D` (limits = world bounds, position
smoothing) following the player; HUD is a mouse-ignoring `Control` overlay on top.
All world art is drawn by lightweight `Node2D` classes in code — no `.tscn` scene
graph to keep in sync with 12 procedural locations.

**Consequences.** Camera, zoom, and (later) lighting/shaders are trivially
available; the HUD never intercepts clicks meant for panels. Cost: one extra
viewport render target — negligible at this scene complexity.

## ADR-085 — Threat detection is a deterministic keyword classifier, not a model call

*Context.* Playtesting showed NPCs ignoring threats ("I want to kill you" got a
non-answer). The player may threaten in English or Indonesian. Detection must run
*before* the provider is consulted (it steers the prompt and the game event), so
it cannot depend on any LLM — mock, local or otherwise.

*Decision.* `AI.ThreatDetector.Detect` is a pure function: lowercase, expand a
fixed contraction list (`i'll`→`i ll`, `don't`→`dont`, …), replace non-letters
with spaces, collapse whitespace, pad with spaces, then phrase-match with word
boundaries. Three levels, checked most-severe-first: DeathThreat → ExplicitThreat
→ Menacing. Phrase lists cover English and Indonesian (`kubunuh`, `akan kubunuh`,
`membunuhmu`, `mati kau`, `awas kau`, `peringatan terakhir`, …).

*Consequences.*
- Same utterance → same level on every platform, no RNG, no state (ADR-002 spirit).
- Word boundaries prevent substring false positives: "skill issue" never trips on
  "kill", passive "dibunuh" never trips on "kubunuh" — pinned by tests.
- The classifier is deliberately dumb: sarcasm, negation ("I would never kill
  you") and novel phrasings are missed. That is accepted — a false negative only
  means "no threat pipeline this exchange", and the prompt still tells the NPC to
  address what was said directly (ADR-086's INSTRUCTIONS change).

---

## ADR-086 — A detected threat is a game event, not just prompt flavor

*Context.* A threat should change the world: the target's fear spikes, witnesses
grow wary, the NPC remembers being threatened (so it can threaten back later),
and a death threat in public is a police matter.

*Decision.*
- `DialogueOrchestrator.Exchange` classifies the utterance and passes the level to
  `ContextEngine.BuildPrompt` (new optional `threat` parameter — existing 5-arg
  call sites compile unchanged). When the level is not None, the prompt gains a
  THREAT section plus a strengthened INSTRUCTION ("Address what they just said
  FIRST and DIRECTLY…"). The THREAT section uses only speaker-known data (their
  own fear of the listener, their own courage, the public location name, and a
  public-visibility note from `ContentDatabase`) — the leak test passes on it.
- After the exchange event is recorded, `Social.ThreatService.HandleThreat`
  records `dialogue.threat` (participants `[threatener, target]`, level/severity/
  witness-count data, `CausedBy` → the exchange), applies social deltas
  (target→threatener: Fear +40/25/12, Trust −15/−10/−5, Suspicion +20/12/6;
  each witness→threatener: Suspicion +10/6/3, Fear +8/5/2 — Death/Explicit/
  Menacing), routes the event through the Phase 4 gate (`Perceive`, Witnessed)
  for target + witnesses, and calls `PoliceService.ReportDisturbance` when the
  level is DeathThreat AND the location is public or any witness is police.
- `HandleThreat` no-ops (never throws) on self-threats or `ThreatLevel.None`.

*Consequences.* Threats now compose with the rest of the sim: fear feeds
compliance (ADR-014), memories feed initiative motives (ADR-088), disturbances
feed the alert ladder (ADR-087). Severity is an integer 1–3 (`SeverityOf`).

---

## ADR-087 — Disturbance reports step the alert ladder to Alert, never to Manhunt

*Context.* ADR-048: "the alert ladder steps on facts, never jumps." A shouted
death threat is a fact, but it is not a murder investigation — it must not be
able to push the city to Manhunt by itself.

*Decision.*
- `PoliceState` gains `Disturbances: List<DisturbanceReport>` (timestamp,
  location, subject, severity — plain data, additive).
- `PoliceService.ReportDisturbance` records `police.disturbance_reported`,
  appends the report and calls `EvaluateAlert()`.
- `EvaluateAlert` counts recent severe disturbances (age < 1440 min, severity ≥ 2;
  new `PoliceRules` constants, no existing constant changed). A count ≥ 2 acts
  exactly like `open.Count >= AlertOpenCrimeCount`: it can step Calm → Alert and
  hold Alert, but the Manhunt branch is untouched — threats alone can never reach
  Manhunt. With no disturbances on record the computation is bit-for-bit the old
  one.

*Consequences.* Two public death threats (or death threats in front of officers)
raise the city's alertness one rung — more patrols via the existing duty scaling
— without inventing a crime case. Disturbances age out after a day, so the ladder
can step back down.

---

## ADR-088 — NPC initiative: UI-driven evaluation, motives as tags, NPC speaks first

*Context.* The player asked for NPCs that can start conversations. Initiative is
a UI-session concern (the dialogue panel needs "who wants to talk"), but motives
must come from real sim state, and the resulting conversation must go through
the same provider → validator → apply pipeline as player-initiated dialogue.

*Decision.*
- New `Initiative.NpcInitiativeService` with a persisted `NpcInitiativeState` on
  `GameState` (queue, per-NPC cooldowns, dedicated `InitiativeRng` stream derived
  from `(runSeed, "npc.initiative")` — ADR-002 pattern, so initiative draws never
  shift other streams; old saves get the same stream via a zero-guard in
  `GameSession`, no migration needed since property initializers supply defaults).
- `Evaluate()` is called by the UI, not the sim: for each living NPC at the
  player's location (excluding the player), skipped when already pending or on
  cooldown (1440 min). Motives derive from real data — ThreatenBack (fear ≥ 60),
  Confront (suspicion ≥ 70), Plead (civilian, trust < 30, fear ≥ 40), Warn
  (affection ≥ 60 + a confidence ≥ 60 memory), ShareRumor (a Told-source memory
  the player lacks) — each with one seeded draw (`NextInt(0,100) < weight`:
  70/50/45/40/30). At most one motive per NPC per Evaluate (highest-priority
  passing motive wins); the queue caps at 3, dropping lowest-priority beyond the
  cap (priority = enum order, later = more severe). Deterministic: same state +
  same stream position → same queue (tested).
- `MotiveDetails` is a motive *tag*, not free text: one of the NPC's own memory
  summaries (ThreatenBack anchors on an actual threat memory; ShareRumor on the
  rumor itself) or a line built from their own relationship values and the
  player's public name. Speaker-known only — safe to hand to the prompt builder.
- Enqueue/dequeue record nothing in the event log (a pending decision is not a
  world fact). `DialogueOrchestrator.OpeningLine(npc, player, initiative)` has the
  NPC speak first via a stage-direction utterance through the shared pipeline and
  records `dialogue.exchanged` with `initiative=true` + motive data. The threat
  pipeline is deliberately NOT run on opening lines: the motive already encodes
  intent, and `dialogue.threat` describes player threats.

*Consequences.* The Godot UI calls `session.Initiative.Evaluate()` when the
dialogue panel opens, shows pending NPCs, and on accept calls
`session.Initiative.TryAccept` + `session.Dialogue.OpeningLine`. Save/load
preserves the queue (tested round-trip).

---

## ADR-089 — The mock provider answers threats from a dedicated template pool

*Context.* The mock is the CI baseline and the default experience (ADR-030). A
threatened NPC answering "Interesting. Tell me more" breaks the fiction the
whole phase is trying to fix.

*Decision.* `MockAIProvider.Complete` branches on `ThreatDetector.Detect`: threat
utterances get four dedicated replies ("W-wait. Take that back — I don't want
trouble.", "Threaten me again and I'll scream for the guard!", …), picked by the
same hash (still a pure function of seed + request, still no RNG stream), with
fixed trust −3 / suspicion +4 and zero or one fact ("{listener} threatened me.").
Templates are validator-safe (no role words; only the known listener name in
facts/memories).

*Consequences.* Mock-driven runs stay replay-identical and validator-clean, and a
threat now visibly lands even with no LLM configured.
