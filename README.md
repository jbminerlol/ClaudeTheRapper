"# 

An unofficial Windows port of the PlayStation rhythm game *PaRappa the Rapper* (NTSC-U, SCUS-94183), built from reverse engineering of the original executable and stage overlays.

**No game data is included.** The port reads everything from your own copy of the disc at runtime: charts, music, rap voice samples and HUD textures. This repository contains only original source code and research notes.

## What works

- All six song stages, with charts read from each stage's `COMOD*.BIN` overlay.
- Stage music decoded from the disc image (CD-XA ADPCM), including the four rank mixes. The music switches mix when your rank changes, as in the original.
- Rap voice samples decoded from each stage's VAB sound bank, played through the original per-bar voice tables.
- The U-Rappin' rank system, ported from the decompiled executable: line scoring, rhythm bonus, pair-based rank changes, Cool mode and fail checkpoints.
- Original tempo, start offset and timing window for every stage, taken from the executable's stage parameter records.
- HUD drawn with the stage's own TIM images on a 320×240 layout: beat bar, button icons, U-Rappin' meter and score.
- Keyboard and XInput controller support, with remapping through `controls.json`.

Not included: stage scenes, character models and animation, lyrics, and FMVs."
this is due to claude being a jerk about it. -JB

"## Requirements

- Windows 10/11 x64 with the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- Your own dump of the NTSC-U disc, arranged as:

```
game\
  assets\manifest.json     extracted disc files plus a manifest with each file's LBA and size
  assets\S\, S2\, S3\ ...  stage folders (COMOD*.BIN, COMPO*.INT, ...)
  source\PARAPPA.BIN       raw 2352-byte-sector image (needed for XA music)
  source\PARAPPA.CUE
```

Put the `game` folder next to `PaRappaPC.exe`, or pick it on first launch. The choice is saved to `%APPDATA%\PaRappaPC\game-folder.txt`.

## Build

```
dotnet build src\PaRappaPC\PaRappaPC.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true -o build
```

`PaRappaPC.exe --selftest report.txt` loads every stage and runs perfect, silent and button-mashing simulations through the rank engine.

## Controls (defaults)

| PS1 | Keyboard | Controller |
|---|---|---|
| △ | W / ↑ | Y / D-pad up |
| ○ | D / → | B / D-pad right |
| ✕ | S / ↓ | A / D-pad down |
| □ | A / ← | X / D-pad left |
| L | Q | LB |
| R | E | RB |

`[` and `]` shift audio sync by 10 ms (Shift for 1 ms), saved per stage. `Esc` returns to stage select. Edit `controls.json` next to the exe to remap; it is created on first launch.

## Layout

| Path | Contents |
|---|---|
| `src/PaRappaPC` | The port: data loading (`GameData.cs`), rank engine (`Scoring.cs`), audio (`SongPlayer.cs`), HUD (`HudSprites.cs`), game screen (`RhythmGameForm.cs`), controls (`Controls.cs`) |
| `tools/chart` | Python research tools: chart extractor, XA music decoder, VAB sound ripper, tempo estimator, a minimal MIPS disassembler |
| `tools/*.py` | INT bundle, TMD and VDF extractors |
| `tools/blender`, `tools/roblox` | VDF animation import and conversion tools |
| `docs` | Reverse-engineering notes: data formats, addresses, decoded functions |

## Legal

This is a fan project for preservation and research. It is not affiliated with or endorsed by Sony Interactive Entertainment, NanaOn-Sha or the game's rights holders. *PaRappa the Rapper* and its characters, music and lyrics belong to their respective owners. Do not distribute game data with this project." -Claude
