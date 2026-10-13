# PokeModelExporter

Converts Pokémon models from **your own extracted game files** into glTF binary (`.glb`) plus a JSON sidecar: meshes, skeleton, textures, materials, every animation clip and its extra data. Currently supports **Pokémon Scarlet/Violet** (Trinity engine).

The tool never touches encryption or console keys. You give it an already-extracted RomFS folder; how you get that from your own copy is up to you.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An extracted Scarlet/Violet RomFS: a folder containing `arc/data.trpfd` and `arc/data.trpfs`. Use the RomFS with the latest update applied; DLC Pokémon are in the update.
- `oo2core_9_win64.dll` (Oodle decompression; it can't be redistributed). Unreal Engine 5.7+ ships it in `Engine/Binaries/DotNET/AutomationTool/`.

## Usage

```
dotnet run --project Cli -- glb 654 --game <RomFS folder> --oodle <oo2core_9_win64.dll> --out braixen.glb
```

Pokémon are chosen by **National Dex number** (plus optional form and gender numbers). The game's own catalog maps them to its internal model folders (Braixen #654 lives in `pm0718`).

| Option | Effect |
|---|---|
| `--shiny` | shiny materials |
| `--lods` | also write the lower-detail meshes as `<name>_lod1.glb`, `_lod2.glb`... (off by default) |
| `--no-anims` | skip animation clips |
| `--no-sidecar` | skip the JSON sidecar |
| `--hashes <hashes_inside_fd.txt>` | full file list ([PokeDocs](https://github.com/pkZukan/PokeDocs) `SV/Hashlists/FileSystem`): lets the sidecar and extra images cover every file in the model folder |
| `--schemas <dir>` | a local PokeDocs `SV/Flatbuffers` folder; otherwise the needed schemas are downloaded once from PokeDocs into `%LOCALAPPDATA%/PokeModelExporter/schemas` |

Other commands: `info`, `catalog [dex...]`, `model <dex> [--params]`, `events <dex>`, `audit <dex>`, `bones <dex>`, `materials <trmtr path>`, `extract <path> <out>`, `texture <file.bntx> <out.png>`, `scan`, `has <path>...`.

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

## Code

- `Runtime/`: engine-independent C# library (no dependencies, C# 9 / .NET Standard 2.1 compatible). Also usable as a Unity package (`package.json`).
- `Cli/`: the command-line tool.

Formats are implemented from public research: the [PokeDocs](https://github.com/pkZukan/PokeDocs) FlatBuffer schemas and hash lists, the published BC7 specification (tables as in Rich Geldreich's bc7decomp, MIT/public domain) and the Tegra X1 block-linear layout.

## Legal

Game assets belong to Nintendo / Game Freak / The Pokémon Company. Only convert files from copies you own, and don't share the output.
