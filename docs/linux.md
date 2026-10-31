# Converting on Linux

The converter is plain .NET except Oodle, the compression of the game's archives, which is the player's own Windows DLL
(`oo2core_*_win64.dll`, not redistributable). On Linux `Formats/Oodle.cs` loads it through linoodle (McSimp/linoodle,
MIT), which maps the Windows DLL into a Linux process.

- Build linoodle with PokeSim's patches (`Tools/Linoodle/build.sh` in the PokeSim repo): they make it load the DLL from a
  path (`Linoodle_Load`, called by `Oodle.cs`) and work with `oo2core_9_win64.dll` (Scarlet/Violet's: the Universal CRT
  imports it needs, a fuller fake TEB).
- Put `liblinoodle.so` beside `pokemodelexporter`, or set `LINOODLE` to its path.
- Publish the CLI for Linux: `dotnet publish Cli -c Release -r linux-x64 --self-contained true -o <dir>`.
- Run it as on Windows, with `--game <scarlet-violet folder>` and `--oodle <oo2core_9_win64.dll>`.

Checked 2026-10-31 (WSL Ubuntu 24.04): `glb 25` gave the same bytes as on Windows (SHA-1 130cdd19...).
