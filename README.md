# PokeModelExporter

Converts Pokémon models from **your own extracted game files** into glTF binary (`.glb`) plus a JSON sidecar: meshes, skeleton, textures, materials, every animation clip and its extra data. It also finds the game's sounds (Wwise events: battle sounds, cries) for decoding. Currently supports **Pokémon Scarlet/Violet** (Trinity engine).

The tool never touches encryption or console keys. You give it an already-extracted RomFS folder; how you get that from your own copy is up to you.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An extracted Scarlet/Violet RomFS: a folder containing `arc/data.trpfd` and `arc/data.trpfs`. Use the RomFS with the latest update applied; DLC Pokémon are in the update.
- `oo2core_9_win64.dll` (Oodle decompression; it can't be redistributed). Unreal Engine 5.7+ ships it in `Engine/Binaries/DotNET/AutomationTool/`.

## Usage

```
dotnet run --project Cli -- glb 654 --game <RomFS folder> --oodle <oo2core_9_win64.dll> --out braixen.glb
```

Pokémon are chosen by **National Dex number** (plus optional form and gender numbers). The game keys its catalog by its own internal species numbers, which differ from the Dex for most of Gen 9; the game's species table (`personal_array.bin`) links the two (Tinkaton #959 is internal 1002), and the catalog then gives the model folder (Braixen #654 lives in `pm0718`). The mapping was verified by base stats for all 733 species in the game, and `SvPersonal.Stats` lets callers check it against their own data.

| Option | Effect |
|---|---|
| `--shiny` | shiny materials |
| `--lods` | also write the lower-detail meshes as `<name>_lod1.glb`, `_lod2.glb`... (off by default) |
| `--no-anims` | skip animation clips |
| `--no-sidecar` | skip the JSON sidecar |
| `--hashes <hashes_inside_fd.txt>` | full file list ([PokeDocs](https://github.com/pkZukan/PokeDocs) `SV/Hashlists/FileSystem`): lets the sidecar and extra images cover every file in the model folder |
| `--schemas <dir>` | a local PokeDocs `SV/Flatbuffers` folder; otherwise the needed schemas are downloaded once from PokeDocs into `%LOCALAPPDATA%/PokeModelExporter/schemas` |

Other commands:

| Command | Does |
|---|---|
| `actor <dex> [form] [gender] [--shiny] --out <dir>` | `model.glb` + `actor.json` the way a game imports them (battle use: clip events, visibility/material tracks, locators, size) |
| `audio <BANK> <EVENT>... --out <dir> [--switch INDEX,FORM00] [--vgmstream <vgmstream-cli.exe>]` | the sounds a Wwise event plays, as `.wem` (and `.wav` through [vgmstream](https://github.com/vgmstream/vgmstream)); e.g. `audio BATTLE_SYSTEM PLAY_BA_SYS_HIT_M`, or a cry: `audio PM_VOICE PLAY_PV_0718 --switch INDEX` (internal species number) |

and `info`, `catalog [dex...]`, `model <dex> [--params]`, `events <dex>`, `audit <dex>`, `bones <dex>`, `materials <trmtr path>`, `extract <path> <out>`, `texture <file.bntx> <out.png>`, `scan`, `has <path>...`.

`Cli/Blender/render_preview.py` renders a GLB (optionally posed at a clip/frame) from the command line for quick checks:
`blender --background --factory-startup --python Cli/Blender/render_preview.py -- model.glb out.png [clip] [frame] [--show-ao]`

## What's in the output

**GLB**
- Skinned meshes (full detail), skeleton, and effect locators (`eff_mouth01`, `eff_rangeattack01`... as nodes with `extras.locator`).
- Materials: base color, normal map, roughness/metallic, ambient occlusion (its own texture). Layered materials (eyes, flames) are baked from their layer masks and per-layer colors, including the eye highlight; unlit shaders use `KHR_materials_unlit`. All original material parameters are kept in `material.extras`.
- Every texture the materials, icons or folder hold that has no glTF role (subsurface masks, second normal maps, displacement, icons...) as images with `extras.role = "unreferenced"`.
- Every animation clip, plus in `animation.extras`: `events` (notifies: cries, sound effects, effect-start triggers, with times in seconds), `materialTracks` (e.g. eye UV offsets), `visibilityTracks` (meshes shown/hidden over time) and `morphTracks`. Meshes hidden by default use `KHR_node_visibility`.

**JSON sidecar** (`<name>.json`)
Everything glTF can't hold: the catalog entry, the animation controller (state machine, layers, parameters, look-at, spine, slope, motion detector), the Pokémon's config (size class, camera framing), the material table and every material set (normal, rare, edit). Files with a PokeDocs schema are decoded to JSON; files without one are stored as base64.

Most viewers (Blender included) ignore the custom extras; they're there for engines and tools that read them.

## Sounds

Scarlet/Violet's audio is a loose folder next to `arc/`: `audio/wwise/bin/Switch/` with Wwise soundbanks (`BATTLE_SYSTEM.bnk`, `PM_VOICE.bnk` cries, `PM_SKILLS.bnk` move sounds...) and packages (`Stream.pck`...). `WwiseSounds` resolves an event name (Wwise IDs are the FNV-1 hash of the lowercase name) through its Play actions and switch containers to the audio data in a bank or package. The audio is Wwise Vorbis or Platinum ADPCM; vgmstream decodes both. Event names: the community list [wwiser-utils](https://github.com/bnnm/wwiser-utils) `wwnames/Pokemon Scarlet + Violet (Switch).txt`.

## Code

- `Runtime/`: engine-independent C# library (no dependencies, C# 9 / .NET Standard 2.1 compatible). Also usable as a Unity package (`package.json`). Entry points: `SvExporter` (Dex number -> GLB + actor manifest, in one call), `SvPersonal` (species mapping), `WwiseSounds` (event -> sound data).
- `Cli/`: the command-line tool. It builds into `.build/` (Unity ignores dot-folders; `bin/` inside the package would be imported as a plugin).

Formats are implemented from public research: the [PokeDocs](https://github.com/pkZukan/PokeDocs) FlatBuffer schemas and hash lists, the published BC7 specification (tables as in Rich Geldreich's bc7decomp, MIT/public domain) and the Tegra X1 block-linear layout.

## Legal

Game assets belong to Nintendo / Game Freak / The Pokémon Company. Only convert files from copies you own, and don't share the output.
