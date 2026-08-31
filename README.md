# FaceDiversityApp

A personal-use tool for generating **NPC face-diversity** replacers for modded Skyrim
(SE / VR). It harvests female faces from mods you already have and applies them across a
target group (bandits, guards, all named males, …), optionally feminizing and renaming,
with a local web UI over a .NET/[Mutagen](https://github.com/Mutagen-Modding/Mutagen) engine.

> **Personal use only — do not redistribute the generated output.** The engine reads your
> local mods and produces plugins/textures that *reference or contain third-party mod
> assets*. Those generated outputs live in `out/` and are intentionally **git-ignored**.
> Only the engine source is tracked here.

## Run

```
run-ui.cmd
```

Builds the engine (`dotnet build -c Release`), starts the UI server on
`http://localhost:8930/`, and opens your browser. Requires the .NET 8 SDK.

## Configure

Per-machine paths (which Skyrim, the MO2 mods/profiles folders, the Bodyslide preset dir)
live in `config/settings.json`, which is **not** tracked (paths differ per PC). Either:

- copy the template — `cp config/settings.example.json config/settings.json` — and edit it, or
- just fill in the **⚙ Setup** card in the web UI (it writes `settings.json` for you).

## Two-PC workflow

This repo is synced between an SE box and a VR box. Everything machine-specific is confined
to `config/settings.json` (ignored), so a `git pull` never clobbers the other PC's paths —
each machine keeps its own local `settings.json`.

## Layout

| Path | What |
|------|------|
| `engine/` | .NET 8 engine + local web server (`*.cs`, one `.csproj`) |
| `web/` | Vanilla HTML/JS UI (no build step, no `node_modules`) |
| `config/` | Category/voice/name YAML + `settings.json` (ignored) |
| `out/` | Generated mod outputs (ignored) |
| `DESIGN.md` | Design notes |
