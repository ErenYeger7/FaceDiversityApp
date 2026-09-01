# FaceDiversityApp — Design

Personal-use utility (NOT a mod) that harvests NPC faces from installed replacers and bakes them
into one standalone mod that increases face variety among generic/unnamed NPC classes (bandits,
forsworn, guards, necromancers, vampires, vigilants, world-encounters, thalmor, …).

Install context: Skyrim **SE** (not VR), portable MO2 "SME" at `C:/Modlists/SME`. Faces are harvested
from the "Literal Who" series (author rsbot) and other replacers; sources may then be **disabled** —
so output must be **standalone** (deep-copy the replacers' own records + assets; keep only shared
resource packs, still enabled, as masters).

## Proven pipeline (spike, in-game PASS 2026-08-13)
Override a vanilla appearance-leaf `NPC_` record; deep-copy the source's own `HDPT` records to new
ESL FormKeys via Mutagen `Duplicate()` + `RemapLinks()`; keep Skyrim.esm head-part links; ship the
baked FaceGen (`facegeom/Skyrim.esm/<targetFormID>.nif` + facetint dds) and head-part assets loose;
ESL-flag. Result masters **Skyrim.esm only**. Backend = **.NET + Mutagen 0.52.0 (net8; latest needs
net9+)**.

## Locked decisions
- Apply = **override the fixed-face target set** (vanilla randomizes across these → diversity for free).
  v1.1 adds **leveled-list injection** to exceed the vanilla ceiling.
- Standalone vs. harvested replacers (deep-copy records+assets); **keep shared resource packs enabled**
  and master them; emit a **README manifest** (masters + resource packs) for portability (e.g. VR PC).
- **Feminization** (v1): turn male targets female = set Female flag + remap voice (`voice_map.yaml`)
  + transplant a **race-matched female face** + ship female FaceGen. No matching-race face → **skip + report**.
- Previews deferred to v2 (needs a NIF renderer; Blender not installed).
- Output **ESL** when possible.

## Bandit facts (verified from this install's Skyrim.esm)
- 319 `EncBandit` records: **213 own-traits (override targets) = 138 M + 75 F**; 106 template-inherited (v1.1).
- Male voices: MaleNord 47, MaleBandit 34, MaleCommoner 28, MaleKhajiit 12, MaleOrc 11, MaleArgonian 6.
- Male races: Nord 47, Imperial 22, Redguard 22, **Khajiit 12, Orc 11, Argonian 6**, WoodElf/DarkElf/Breton 6 each.
- Literal Who female faces cover Nord/Imperial/Redguard/WoodElf/DarkElf/Breton only → **no beast-race
  female faces**. All-female ladder: **184 F** (Literal Who only) → **213 F** (+beast female faces) → **319 F** (v1.1).

## Coverage matrix (UI + engine output — REQUIRED)
Per race, show: target **M** count, target **F** count, **selected faces** available, **assigned**,
**skipped** (and why). Drives the UI supply/demand panel and the README report. `engine analyze`
emits this as a table + JSON.

## Roadmap
- **v1 engine (CLI-first):** analyze (coverage matrix) ✔ next → generate (N faces → race-matched
  override set, optional feminization, one ESL + README).
- **v1.1:** leveled-list injection (raise ceiling beyond vanilla count); verify other categories'
  target prefixes.
- **Phase 3 (GUI):** 3-pane app (sources → filter by race/voice → cart → settings/create) on the engine,
  with the coverage panel. Face previews = v2.

## Not covered yet: template/leveled "filler" (future feature)
The current engine only diversifies **own-traits** records (an NPC with its own face). The most
*ubiquitous* NPCs — city hold guards and generic Civil War soldiers — are deliberately built the
other way: a per-hold/per-role record inherits **Traits** from a base template, which itself often
chains to a **leveled character list**; its `Race` is the `FoxRace`/`DefaultRace` placeholder and the
actual face resolves at spawn time from the template leaf. Verified in Skyrim.esm (2026-08-31):

| Group | Records | Template-based | Own-traits (covered) |
|-------|--------:|---------------:|---------------------:|
| Hold guards (`Guard*`) | 295 | 277 | 18 |
| Generic CW soldiers (`CWSoldier*`) | 18 | 18 | 0 |

The `guard`/`soldier` categories catch the **own-traits** war NPCs (`EncGuard` battle guards,
`EncSoldier`/`EncSiege` patrols — both sides). The template filler above needs a **different
mechanism**, tracked here as a separate future feature:

- **Approach:** reface/feminize the shared **template base** (and/or the leveled-list leaves) rather
  than per-NPC overrides — one edit flips the whole squad. A single template touches many spawns.
- **Hazard 1 — voice:** template soldiers/guards are **sex-locked** by voice type; flipping one female
  without a female voice type = silent/mis-voiced NPC. Vanilla *does* ship female CW voices
  (`CWVoiceTypeSoldierFemaleNord`/`…FemaleCommander`, `VoiceTypeNPCFemaleSoldier`), so it's feasible —
  the feature must assign them.
- **Hazard 2 — squad uniformity:** editing one template makes every inheritor share a face. Real
  diversity needs either several template variants or a leveled list of faces, not a single swap.
- **Hazard 3 — quest/faction:** guards carry hold/quest/dialogue assumptions; changing sex/appearance
  on the base can ripple into scripted scenes. Scope carefully and test.
- **Scope note:** this is CW/guard-specific and higher-risk than the override path; keep it a distinct
  opt-in mode, not folded into the own-traits categories.

## Layout
- `config/` — `voice_map.yaml`, `categories.yaml` (tweakable).
- `engine/` — .NET CLI (Mutagen). Commands: `analyze`, later `generate`.
- `out/` — generated mods + coverage JSON (gitignore).
