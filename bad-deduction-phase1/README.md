# Bad Deduction

A systemic psychological-crime and social-deduction simulation: autonomous characters with memory,
beliefs, secrets and relationships; two hidden geniuses (**Malvr** and **Lumiel**); and investigations
that emerge from the simulation instead of a scripted solution.

* Design references (unchanged, at repo root): `Desain AI Decision System.png`, `Desain UIUX.png`,
  `Desain Vertical Slice.png`, `Desain mapkota.png`, `Rangkuman Semua Image.png`
* Audit, conflicts, roadmap and open questions: [`docs/00-repository-audit.md`](docs/00-repository-audit.md)
* Architecture decisions: [`docs/decisions.md`](docs/decisions.md)

## Status

**Phase 1 (Foundation) complete.** Deterministic RNG, game time, event bus + causal world-event log,
versioned save/load with state hashing, location content, basic character/world state, and the
truth-vs-player-view separation. 41 tests. No presentation layer yet (Phase 11).

## Layout

```
src/BadDeduction.Core/     engine-free simulation library (net8.0)
  Core/  World/  Characters/  Content/
tests/BadDeduction.Tests/  dependency-free test runner
data/                      data-driven content (locations.json)
docs/                      audit, roadmap, decisions
```

## Build and test

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build BadDeduction.sln
dotnet run --project tests/BadDeduction.Tests            # all tests
dotnet run --project tests/BadDeduction.Tests -- Save    # only tests whose name contains "Save"
```

## Rules for contributors

* Simulation code must be deterministic: use `DeterministicRandom`, never `System.Random`,
  `DateTime.Now` or `Guid.NewGuid()`.
* Trust/suspicion/confidence are integers.
* Hidden truth is only reachable through `DebugAccess`; UI code uses `PlayerView`.
* The LLM never mutates `GameState`; its output is validated first.
* **Never commit API keys or credentials.** Use environment variables or secure runtime settings.
