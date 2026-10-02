# Talking to a real AI — Ollama setup

By default, NPC dialogue in Bad Deduction is **scripted** (template lines from the
built-in mock provider). It is deterministic and replay-safe, but every
conversation sounds the same. If you want NPCs that actually improvise, point the
game at **Ollama** — a free app that runs open language models on your own PC.
No account, no API key, nothing leaves your machine.

> The game's validator still guards every AI reply: an NPC can never invent facts
> it doesn't know, reveal hidden roles, or break the simulation. Ollama only
> changes *how* NPCs talk, never *what they are allowed to know*.

## Step 1 — Install Ollama

1. Go to **https://ollama.com** and download the installer for your system
   (Windows / macOS / Linux).
2. Install it and make sure it is running:
   - Windows/macOS: it lives in the system tray after install.
   - Linux: run `ollama serve` in a terminal (keep it running).

## Step 2 — Download a model

In a terminal (or CMD on Windows), run:

```
ollama pull llama3.1:8b
```

This downloads ~5 GB once. Recommended alternatives if your PC is modest:

| Model tag        | Size  | Notes                              |
|------------------|-------|------------------------------------|
| `llama3.1:8b`    | ~5 GB | Default. Best all-round quality.   |
| `qwen2.5:7b`     | ~5 GB | Good alternative, strong dialogue. |
| `llama3.2:3b`    | ~2 GB | Lighter; faster on weak hardware.  |

Any model tag Ollama knows will work — just type its tag into the game's
**Model** field.

## Step 3 — Enable it in the game

1. In Godot, open the project and press **F5**.
2. On the **New Run** screen, set **Dialogue AI** to **Ollama (local LLM)**.
3. Leave **Model** as `llama3.1:8b` (or your chosen tag) and **Endpoint** as
   `http://localhost:11434`.
4. Start the run and talk to NPCs.

## What to expect

- The **first reply** can take 10–60 seconds while the model loads; later replies
  are faster. The game waits for the model, so don't click away mid-reply.
- If Ollama isn't running (or the model tag is wrong), the game **quietly falls
  back to scripted dialogue** — you'll see a small `(offline dialogue)` chip next
  to those replies. The game never crashes or freezes permanently because of this.
- Dialogue is **no longer deterministic** with Ollama on: the same seed + same
  actions can produce different conversations. Replays and the 200-seed balance
  metrics assume the scripted default.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `(offline dialogue)` on every reply | Ollama isn't running — start it / run `ollama serve`. Check the endpoint in settings. |
| Very slow replies | Use a smaller model (`llama3.2:3b`), or close other heavy apps. A GPU helps a lot. |
| Replies ignore the "structured fields" and feel flat | Some small models follow instructions poorly — try `llama3.1:8b` or `qwen2.5:7b`. The validator still keeps them honest. |
| `connection refused` in a terminal test | Run `curl http://localhost:11434/api/tags` — if that fails, Ollama isn't listening. |
