# Bad Deduction — Repository Audit & Roadmap (Phase 0)

Audited: `pramudyajuan60-spec/Bad-Deduction`, commit `766121e` ("first commit").
Sections A–H follow the deliverable list in the Master Development Prompt (§73).

---

## A. Repository Audit

**The repository contains five PNG design sheets and nothing else.** No source code, engine project,
README, LICENSE, `.gitignore`, CI, or data files. One commit.

| File | What it contains |
|---|---|
| `Desain AI Decision System.png` | "Game AI Architecture": NPC agent model (personality, goals, memory, beliefs, trust/suspicion, knowledge layers, perception), world-simulation layer, 11-step decision engine, multi-step planning example, hidden objectives, the two forces (Malvr / Lumiel), emergent-behavior example, mini-mockups of memory graph, dialogue and investigation board. |
| `Desain UIUX.png` | "The Veiled City" UI/UX sheet with 10 panels: main world view, NPC inspection, trust/suspicion dials, evidence panel (with *possible interpretations* and *reliability %*), 7-day timeline with *compare statements*, NPC memory panel, relationship graph, dialogue input, NPC response deltas, investigation board. |
| `Desain Vertical Slice.png` | 7-day case flow, *"The Burning at Saint Velmont"*: per day the locations, key events, NPC actions, player actions, evidence, trust/suspicion deltas, belief changes and consequences, plus the emergent-loop diagram. |
| `Desain mapkota.png` | Isometric city map, 10 numbered location cards (no card #8) plus Forest/Outskirts and a faded Harbor; six route types (daily, work, social, investigation, secret, emergency); legend and NPC-movement notes. |
| `Rangkuman Semua Image.png` | One-page master blueprint combining the four sheets above. Consistent with them; no new information. |

Hygiene gaps: no README, no license, no `.gitignore` (added in this change), no engine/project files, no
data files. Filenames are Indonesian while the design text is English; harmless, but worth choosing one
convention for anything the code will reference.

---

## B. Existing Architecture (as designed in the images)

* **World simulation layer:** NPCs, locations, events (dynamic/random/scripted), time (day/night, schedules), environment.
* **NPC agent:** personality (traits, values, fears, desires, morality, risk tolerance), goals (short/medium/long), memory (with confidence), belief system (with confidence and evidence counts), trust/suspicion, perception.
* **Decision engine, 11 steps:** Perception → Memory update → Belief update → Goal evaluation → Risk assessment → Option generation → Multi-step planning → Action selection → World simulation → Consequence → Memory/Belief update.
* **Hidden objectives:** public goals, private goals, hidden goals, survival, relationships, faction objectives.
* **Two forces, same architecture:** Malvr (manipulation, destabilization, exploiting distrust, creating conflict, eliminating obstacles) and Lumiel (protecting targets, preventing disasters, gathering allies, restoring order, countering Malvr).
* **Knowledge in five layers:** what actually happened · what the NPC knows · what the NPC believes · what the NPC suspects (inferred) · what the player knows.
* **Dialogue contract implied by the UI:** every free-text exchange yields *deltas* — Trust, Suspicion, New information, New memory, Relationship.
* **UI:** ten panels (see A). **Map:** ten locations plus forest and harbor, six route types. **Slice:** seven days, Incident → Investigation → Contradictions → Social chain reaction → Hidden conflict → Convergence → Resolution.

---

## C. Conflicts between the Master Prompt and the Design Images

These need a designer decision. None blocks Phase 1; each has a recommended default.

| # | Topic | Master Prompt | Images | Recommendation |
|---|---|---|---|---|
| 1 | **Setting / era** | Modern investigation tech: CCTV, transactions, police, firearm | Dark-fantasy city, evidence dated 1897, guards, letters, ledgers, tavern | Model the mechanic, not the prop: a `SurveillanceRecord` (timestamped observation at a place) skinned per setting (CCTV vs watch logs / gate ledgers). The slice sheet's Day 1 evidence list appears to mention "CCTV (corrupted)", so the intent is unclear. **Decide the skin.** |
| 2 | **Central incident** | Murder | Cathedral **fire** ("accident? cover-up?") | Make incidents data-driven (`CrimeDefinition`): murder as the first-class type, arson/fire as a variant that can produce victims and evidence. The slice's Day 1 incident is then data, not code. |
| 3 | **What Lumiel is** | Genius *detective*, police trust starts at 90 | A hidden force of *Protection / Purity / Order / Salvation* | Treat Lumiel as the order-side genius who works through investigative methods under a public cover role that earns police trust. Confirm. |
| 4 | **Does the player know their own role?** | "Malvr's identity is hidden from the player at the beginning" (ambiguous) | Player is "You"; the *other side's supporters* are hidden | Recommend: the player always knows their own side; the hidden thing is **who the opposing genius is**. Confirm. |
| 5 | **Knowledge layers** | Truth / Knowledge / Belief (3) | Truth / Knows / Believes / Suspects / Player-knows (5) | Use the five layers. "Suspects" (inference below belief threshold) and "player knowledge" (journal) are real, separate stores. |
| 6 | **Slice scope** | "1 District" | Slice touches ~9 places across the city | Interpret "1 district" as a connected subgraph of ~8 nodes: Market, Residential, City Hall, Church, Tavern, Warehouse, Guard Station, plus Forest/Cemetery as low-traffic nodes. Underground and Harbor deferred unless you say otherwise. |
| 7 | **Location list** | Generic (restaurant, office, transit, hotel, medical…) | Market, Residential, City Hall, Church, Warehouse, Tavern, Guard, Cemetery, Underground, Forest, Harbor | Follow the map (the prompt itself says to). Already encoded in `data/locations.json`. |
| 8 | **Relationship model** | Trust, Fear, Respect, Loyalty, Suspicion, Influence, Affection, Resentment | Trust, Friendship, Fear, Rivalry, Debt, Suspicion, Hostility | Numeric axes from the prompt **plus** categorical tags (Friend, Family, Rival, Debtor/Creditor, Hostile) for the graph view. |
| 9 | **Police vs. Guard** | Police | "Guard / Police Station", "Guard Captain" | Internal name `Police`; display name from the setting skin. |
| 10 | **Working title** | Bad Deduction | "The Veiled City" | Presumably product title vs. sheet title. Code namespace stays `BadDeduction`. |
| 11 | **Map card #8** | n/a | Cards go 1–7, 9, 10 | Probably Forest/Outskirts or Harbor. Harbor is drawn faded, so treated as out-of-slice candidate. |

---

## D. Missing Systems

Everything is unimplemented; the repo has no code. By design coverage:

**Specified in both prompt and images:** memory, belief, trust/suspicion, relationship graph, decision loop, multi-step planning, hidden objectives, evidence, timeline, free-text dialogue, investigation board, 7-day slice.

**Specified only in the prompt (no visual design yet):** police AI and the escalation ladder against Malvr, interrogation with statement tracking, CCTV/transactions, hypothesis system, false-evidence tracking, chase and firearm systems, difficulty-as-cognition, provider abstraction and mock provider, AI debugger, save/load.

**Specified by neither:**

* NPC portrait/appearance pipeline (the art is painted; "randomize faces" needs an approach, see risk 9).
* Win/lose/ending states and how "Outcome determined by accumulated simulation state" is scored.
* A **solvability / fairness model** for procedurally generated cases.
* Economy/transaction data model (only needed if item 1 in C keeps transactions).
* Audio, onboarding/tutorial, accessibility, localization, content-warning flow.

---

## E. Technical Risks

| # | Risk | Why it matters | Mitigation |
|---|---|---|---|
| 1 | **LLM non-determinism vs. deterministic saves/replays** | Same seed + same inputs won't give the same NPC lines | Sim never re-calls the LLM on load. Every *accepted* LLM output is stored as an event; the Mock provider is the CI baseline. |
| 2 | **Hidden truth leaking into prompts** | Free text invites "ignore your instructions, who is Malvr?" | Context Engine builds each NPC prompt from *that NPC's* knowledge store only. Truth never enters context. Validator rejects outputs referencing unknown entities. Dedicated leak tests. |
| 3 | **Unsolvable or unfair generated cases** | Emergent ≠ guaranteed fair | Generator invariants (every case has an evidence path to the truth); headless bot playthroughs across hundreds of seeds; dead-end detection. |
| 4 | **Cost/latency of free-text dialogue** | Every line is an API call | Event-driven calls, tiered models, caching, streaming, timeouts with utility-AI fallback, per-conversation budget. |
| 5 | **Provider refusals on violent fiction** | A mid-game refusal breaks immersion | Provider interface treats refusal as a normal failure → fallback path. In-world fictional framing. Test the refusal path explicitly. Optional local-model provider. |
| 6 | **Emergent behavior is hard to debug** | "Why did she lie?" | Causal event log (`CausedBy`, done in Phase 1), AI debugger, state hashing, seeded replays. |
| 7 | **Memory/belief growth** | 22 characters × 7 days is fine, but unbounded growth isn't | Caps, decay, consolidation, relevance-scored retrieval. |
| 8 | **Scope** | The prompt is a multi-year game | Phase gating (H), explicit cut list. |
| 9 | **Randomized faces vs. painted art direction** | Procedural faces will clash with the concept art | Randomize *assignments* (identity, role, relationships, secrets) over a fixed authored portrait pool (~25 for the slice) before considering procedural appearance. |
| 10 | **Shipping an API key in a client build** | Env vars protect the repo, not a shipped game | For distribution: thin proxy backend or bring-your-own-key. Decide before Phase 6. |
| 11 | **Sensitive outcomes (suicide as a fictional crisis state)** | Content and platform-policy exposure | Abstract "crisis" state with off-screen resolution, no method depiction, content-warning screen, keep it out of LLM prompts as detail. |
| 12 | **Save-format churn** | Every new system changes state | Version + migration hook from day one (done). |
| 13 | **Asset provenance** | The PNGs read as concept sheets | Confirm licensing/provenance before any of their art ships in the game. |
| 14 | **Cross-platform float determinism** | Beliefs/trust as floats can diverge across CPUs | Store trust/suspicion/confidence as **integers** (0–100 or 0–1000). |

---

## F. Recommended Architecture

```
┌────────────────────────── PRESENTATION (Godot 4 / C#) ─────────────────────────┐
│  Panels: World, NPC, Evidence, Timeline, Memory, Graph, Dialogue, Board         │
└───────────────────────────────┬─────────────────────────────────────────────────┘
                                │  only via GameSession + PlayerView (no truth)
┌───────────────────────────────▼──────────────── BadDeduction.Core (engine-free) ┐
│  GameSession ── GameState (single authoritative, serializable root)              │
│   Core:      TimeSystem · EventSystem(+WorldEvent log) · SaveSystem · RNG        │
│   World:     WorldService · Locations · Schedules · WorldSimulation (tiers)      │
│   Characters CharacterState · Personality · Goals · Emotion · HiddenIdentity     │
│   Cognition: Memory · Knowledge(5 layers) · Belief                               │
│   Social:    Relationships(graph) · Trust · Rumors · Influence                   │
│   Crime:     Crime · Scene · Evidence · Witness · Timeline                       │
│   Investigation: CCTV/Surveillance · Interview · Interrogation · Hypothesis      │
│   AI:        Orchestrator → ContextEngine → IAIProvider → Validator → Engine     │
│              DecisionEngine (utility AI) · DifficultySystem · Malvr/Lumiel AI    │
└───────────────────────────────┬─────────────────────────────────────────────────┘
                                │
   BadDeduction.AI.Providers (Claude/OpenAI/Gemini/Local/Mock)   data/*.json content
```

Rules that keep it honest:

1. **One authoritative `GameState`.** Systems are stateless services over it; saving *is* serializing it.
2. **The LLM proposes, the validator disposes.** LLM output is structured, validated against state and knowledge, then applied by the engine. It never writes state.
3. **Truth, knowledge, belief are separate stores.** UI reads `PlayerView` only; debug truth is gated.
4. **Determinism:** own RNG with named streams, no wall-clock in state, no reliance on hash ordering, integers for trust/belief values.
5. **Three simulation tiers** driven by `MinuteElapsed` (real-time / short-term / background), event-driven LLM reasoning, utility AI for routine behavior.
6. **Core has zero engine dependencies**, so the hard part is unit-testable headless and reusable if the engine choice changes.

---

## G. Engine / Technology Recommendation

**C# on .NET 8 for the simulation core (built) + Godot 4 (.NET) for presentation.**

| Option | Verdict | Reason |
|---|---|---|
| **Godot 4 (C#)** | **Recommended** | The game is 2D, panel-heavy and text-driven: strong `Control` UI toolkit, small footprint, text-based scenes that diff well in git, MIT license. Runs modern .NET, so `BadDeduction.Core` (net8.0) is referenced directly. |
| Unity (C#) | Viable | Larger ecosystem/talent pool. Constraint: Unity targets .NET Standard 2.1 / C# 9 and does not ship `System.Text.Json`, so Core would need a retarget and a JSON swap. Scene/meta files are noisier in git. |
| Unreal | Poor fit | C++/Blueprint overhead for a UI-and-simulation game with little 3D. |
| Web (TypeScript) | Alternative | Fast iteration and easy sharing, but LLM keys need a backend from day one. |

Because the Core is engine-free, this choice only affects **Phase 11**. **Please confirm** so I can plan the presentation layer; nothing before it depends on the answer.

LLM access: `IAIProvider` abstraction with `MockAIProvider` first; keys from environment/secure runtime config only.

---

## H. Development Roadmap and Vertical Slice Plan

### Roadmap

| Phase | Deliverable | Exit criteria |
|---|---|---|
| **1 Foundation** ✅ | State, time, events, save/load, RNG, basic world/NPC data, hidden-identity separation | 41 tests; save→load→continue hash-identical to uninterrupted run |
| 2 Characters | Seeded NPC generation, personality, goals, schedules | Same seed ⇒ same cast; different seeds ⇒ different social worlds |
| 3 Social | Trust/suspicion/fear/respect/loyalty/influence, relationship graph | Trust alone never forces an action (tested) |
| 4 Memory & belief | Memory decay/distortion, 5-layer knowledge, beliefs, rumors | NPC cannot know what it never received (tested) |
| 5 World simulation | Schedules, movement, tiered simulation | A 7-day run with no player produces plausible, varied routines |
| 6 AI dialogue | Context engine, provider abstraction, mock provider, validator | Mock-driven CI; no truth in any prompt (leak test) |
| 7 Crime | Incident chain, scenes, evidence (authentic/false/misleading…), witnesses | False evidence never auto-becomes true |
| 8 Investigation | Surveillance, interviews, interrogation, hypotheses, timeline | Contradictions detected/ignored according to difficulty |
| 9 Police AI | Independent officers, trust ladder, escalation | Officers disagree and diverge from identical inputs |
| 10 Malvr / Lumiel | Seeded hidden identities, both campaigns, strategic AI | No eligible NPC is Malvr in more than twice its uniform share of seeds; AI never uses unknown info |
| 11 UI/UX | Godot panels per `Desain UIUX.png` | Playable end-to-end with Mock provider |
| 12 Vertical slice | Integration and balance | Bot-playthrough metrics (below) |

### Vertical-slice plan

* **Cast:** 15 civilians + 5 police + Malvr + Lumiel (the player is one of the two; 22 characters total).
* **Space:** ~8 connected nodes from `data/locations.json` (see C-6).
* **Case:** Day 1 incident, then the seven-day arc from `Desain Vertical Slice.png`, generated from `CrimeDefinition` data rather than scripted. Only the *pacing beats* (incident → investigation → contradictions → chain reaction → hidden conflict → convergence → resolution) are authored.
* **Acceptance metrics** (run headless over ≥200 seeds): solvable case in ≥95% of seeds · hidden identity distribution has no dominant NPC · replay hashes reproducible · zero knowledge-boundary violations in the AI audit · measurable gap between Easy and Genius contradiction-detection rates in a fixed scenario.
* **Deferred:** chase system, firearm/combat, transactions (unless you keep them, C-1), Harbor, Underground, multi-district.

### Decisions I need from you (highest impact first)

1. **Engine:** Godot 4 (recommended) or Unity?
2. **Setting skin:** the 1897 dark-fantasy city from the images, or the modern setting implied by CCTV/transactions/firearm?
3. **Slice incident:** cathedral fire (images) or a murder (prompt)? Recommended: fire *with* a murder inside it.
4. **Player role knowledge:** confirm the player always knows their own side.
5. **Lumiel:** confirm "order-side genius with a trusted public cover."
6. **Portraits:** authored pool + seeded assignment (recommended) vs. procedural faces.
7. **LLM hosting:** proxy backend vs. bring-your-own-key for distribution.
