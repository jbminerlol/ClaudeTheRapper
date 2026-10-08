# PaRappa PC port prototype

The six song stages now play from the original data. At runtime the game reads each stage's rhythm chart from its `COMOD*.BIN` overlay and decodes the stage music (all four rank mixes) from the raw disc image, so nothing from the disc is stored in this program. The U-Rappin' rank system is ported from the executable (see Scoring.cs); the rap voice samples, stage scenes and character animation are not ported yet.

## Original chart and music data

- **Charts:** each stage overlay's setup code stores table pointers into executable globals at `0x800943c0`. `0x800943e4`/`0x800943e8` hold the expected presses as (position in 16th notes, button id) pairs; `0x800943c4`/`0x800943c8` hold the 24-byte-per-bar list (section, buttons used, sound set, timing-window row). Button ids: 1 △, 2 ○, 3 ✕, 4 □, 5 L, 8 R. Every press matches its bar's button mask on all six songs (573 presses). See `../chart/extract_charts.py`.
- **Music:** `STAGE*.XA` is mono 37.8 kHz XA-ADPCM with interleaved channels. The extracted files lost the XA sub-headers, so the game reads raw sectors from `game/source/*.bin`. The executable's table at `0x8005541c` maps rank to channel: Cool 5, Good 1, Bad 2, Awful 3. See `../chart/xa_decode.py`.
- **Clock:** the stage overlay computes song ticks as `tempo × cd_position / 4500 + offset` (0x180 ticks per bar). Tempos measured from the music: 110, 107, 78, 100, 96 and 102 BPM. The audio offset is calibrated per stage in game with `[` and `]` (Shift for 1 ms steps) and saved to `%APPDATA%\PaRappaPC\sync-offsets.json`.

## Rank system (ported from the executable)

`Scoring.cs` follows the decompiled code: presses land in per-bar 16th-note slots (FUN_80014614); a line scores 3 points per on-beat slot using one of the line's buttons, needs every button in the line used at least once, earns a rhythm-variety bonus when its key phrase is hit, and loses points for off-beat presses or mashing (FUN_80014d58/FUN_80014a80). A line is good if it gains points; pairs of lines move the rank (two good up, two bad down, mixed no change). Good to Cool needs a pair to beat the bar's Cool threshold and Cool must be unlocked (cleared all six stages); in Cool, two lines under 66 points drop back to Good. Bars flagged 0x10 fail you at Bad/Awful, bars flagged 0x08 fail you at Awful after a silent or losing pair. The press window (±8 ticks) and each stage's tempo and start offset come from the executable's stage parameter records at 0x8005474c (BPM ×100 at +6, offset at +8 and +10). The one deliberate change is scoring just after slot 14 rather than exactly on it.

`PaRappaPC.exe --selftest report.txt` also simulates perfect, silent and button-mashing players on every stage.

## Key mapping

Edit `controls.json` next to the exe (created on first launch). Keyboard names are .NET `Keys` names; controller names are A, B, X, Y, LB, RB, DPadUp/Down/Left/Right, Start, Back, LeftStick, RightStick.

## Controls

△ `W`/`↑`, ○ `D`/`→`, ✕ `S`/`↓`, □ `A`/`←`, L-shoulder `Q`, R-shoulder `E`. An Xbox-style controller works through XInput (Y △, B ○, A ✕, X □, LB, RB). `Esc` returns to stage select.

`PaRappaPC.exe --selftest report.txt` loads every stage's chart and music and writes a summary.

## Animation research

The stage `COMPO*.INT` bundles contain VDF vertex-difference streams in their unindexed trailing data. A scanner recovered 259 streams across nine bundles. See the workspace `VDF_FORMAT_NOTES.md` and `outputs/vdf-extracted/manifest.json` for the recovered record layout and offsets.

The same six song bundles also contain TMD model data. Run `python ..\tools\extract_tmd.py "<game>\assets" --out ..\outputs\tmd-extracted` to export supported mesh geometry as OBJ. The current index contains 149 model objects. In the VDF lab, choose a stream and use **Match COMPO model…** to find meshes with the same VDF object index and exact vertex span, then preview the animation over the extracted base mesh. This is a data-based candidate match; the runtime asset pointer association is still unresolved.

## Current features

- Play all six song stages with their original charts and music; rank changes switch between the four original music mixes.
- Use the Awful, Bad, Good, and Cool rank meter; rank movement resolves over two-line pairs, and a stage clears at Good or Cool.
- Stage cards and the asset browser map song order to the disc's COMPO01, 02, 03, 05, 06, and 07 bundles.
- Open the asset browser and VDF animation browser from the stage-select screen.
- Browse TIM textures extracted from the PlayStation disc's INT bundles.
- Extract and preview polygon meshes from the original stage TMD data, preserving the vertex order used by VDF deltas.
- Open a Wavefront OBJ file in the custom mesh preview.
- Load a VDF file and scrub or play its per-object vertex-difference records over a base OBJ with matching vertex order.
- Match a selected VDF to stage TMD mesh candidates by object index and exact vertex span.
- Orbit the mesh with the left mouse button and zoom with the wheel.
- Choose window aspect, 4:3, 16:9, or 21:9. The model camera keeps its vertical field of view and expands horizontal framing for wide views.

The TMD exporter currently handles the unlit polygon packets used by the matched character meshes and renders them with flat preview colors; it does not yet apply TIM textures or assemble full stage scenes. Candidate VDF-to-TMD links use exact vertex spans and object indices, not a recovered runtime pointer table. Rank thresholds and timing windows are approximations; rap voice samples, stage scenes and in-game character animation still need to be ported. See `REVERSE_ENGINEERING_STATUS.md` for the stage-to-bundle map and Ghidra findings.

## Reverse-engineering exports

Ghidra exports the main executable and all nine available `COMOD` overlays into `../outputs/`. The executable export covers 777 detected functions (776 decompiled); the overlay exports cover 264 more detected functions. One executable function, `FUN_80021e60`, is not decompiled because Ghidra reports a backward-normalization error. The exports are useful pseudocode for the port, not original source code. Chart tables are decoded (see above).

The latest Windows x64 executable is in `src/PaRappaPC/build-data/PaRappaPC.exe`.

## Build

From this directory, build for 64-bit Windows with:

```powershell
dotnet build .\PaRappaPC.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true -o build-data
```

The generated executable targets Windows x64 and requires the .NET 9 desktop runtime.
