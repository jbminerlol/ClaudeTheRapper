# PaRappa Windows port: reverse-engineering status

## Main six song stages and asset bundles

The disc's numbered bundle IDs do not match the six-song order without gaps. The extracted disc files map as follows:

| Song stage | Song / teacher | Asset folder | Visual bundle | Dialogue data | Stage audio file |
|---|---|---|---|---|---|
| 1 | Kick! Punch! / Chop Chop Master Onion | `S` | `COMPO01.INT` | `COMOD1.BIN` | `STAGE1.XA` |
| 2 | Driver's Test / Instructor Mooselini | `S2` | `COMPO02.INT` | `COMOD2.BIN` | `STAGE2.XA` |
| 3 | Flea Market / Prince Fleaswallow | `S3` | `COMPO03.INT` | `COMOD3.BIN` | `STAGE3.XA` |
| 4 | Seafood Cake / Cheap Cheap the Cooking Chicken | `S5` | `COMPO05.INT` | `COMOD5.BIN` | `STAGE5.XA` |
| 5 | Full Tank / All Master | `S6` | `COMPO06.INT` | `COMOD6.BIN` | `STAGE6.XA` |
| 6 | PaRappa's Live Rap / MC King Kong Mushi | `S7` | `COMPO07.INT` | `COMOD7.BIN` | `STAGE7.XA` |

The current disc extraction contains no `COMPO04.INT`; stage 4's files are named with ID 05. `COMPO00` is a non-song opening/story scene, and IDs 08–09 are present as additional game content outside the six songs listed on Wikipedia. The stage selector and asset browser use the six-song mapping above.

The named entries in the COMPO bundles are primarily TIM images; their unindexed tails contain VDF vertex-difference streams. `COMOD*.BIN` begins with null-terminated dialogue strings and is not itself the visual bundle. The `.XA` files are stage audio streams; their timing has not yet been decoded. Findings above come from the extracted disc at `<game>\assets` and the strings in the corresponding COMOD files.

## Gameplay facts and current approximation

Wikipedia describes the teacher showing a symbol sequence, the player responding with timed button presses, and a four-level Awful/Bad/Good/Cool performance meter. The article says rank effects resolve over pairs of lines and a song must end at Good or Cool to clear. See [PaRappa the Rapper — Gameplay and Songs](https://en.wikipedia.org/wiki/PaRappa_the_Rapper#Gameplay).

`PaRappaPort/RhythmGameForm.cs` now has a playable Windows rhythm loop and selectable slots for all six songs. It uses short hand-authored four-symbol patterns and placeholder tempos. The rank thresholds, complete charts, original music, character animation, and stage scenes are not recovered yet. The app labels those patterns as placeholders rather than passing them off as disc data.

## Ghidra findings

The retail PS-X executable is imported into Ghidra as MIPS little-endian from file offset `0x800`, length `0x5f000`, at address `0x80010000`. Each `COMOD*.BIN` module was imported separately at its runtime overlay base `0x801c3870`; separate programs avoid address collisions between overlay files.

The main executable export contains 777 functions after explicitly recovering several missed helper functions. Ghidra decompiled 776; `FUN_80021e60` fails with a low-level "Backward normalization not implemented" error. The nine available `COMOD` overlays contain another 264 auto-detected functions, all decompiled:

| Module | Functions decompiled |
|---|---:|
| `COMOD0.BIN` | 24 |
| `COMOD1.BIN` | 30 |
| `COMOD2.BIN` | 26 |
| `COMOD3.BIN` | 26 |
| `COMOD5.BIN` | 32 |
| `COMOD6.BIN` | 41 |
| `COMOD7.BIN` | 31 |
| `COMOD8.BIN` | 29 |
| `COMOD9.BIN` | 25 |

The gameplay path is now traced across the executable and overlays. `FUN_801c7a60` in the stage overlay polls the controller and calls executable function `FUN_80014614` when the input state changes. The evaluator uses a `0x180`-tick bar, divides it into sixteen `0x18`-tick positions, and applies an input-window check within each position. It selects a 6-byte line descriptor from the active record at `param + 0x44`, using the line index at `param + 0x50`. Descriptor bytes at offsets `+0x0c` and `+0x0d` select a 9-slot pointer table and a compact pattern table. The pointer rows have a `0x24` stride; the pattern rows have a `0x10` stride, with the evaluator reading one byte for each pair of tick positions. A byte value of `2` enters the scoring path; `0` is skipped. The table contents and the descriptor selectors have not yet been mapped to actual button sequences for all six songs.

Replay inputs are stored separately as timestamp/button-mask pairs, so that buffer is not the original chart. Rank changes are computed by another routine, but the complete chart-to-rank behavior has not been reconstructed. The stage modules contain code, dialogue, and pointer-linked data; their data sections need further labeling before the chart tables can be exported accurately.

Decompilation reports are under `outputs/`, including [the executable export](outputs/base-executable-decompilation.txt), stage module exports, [the recovered chart helper functions](outputs/recovered-chart-helpers.txt), and the targeted timing/rank reports. The Ghidra projects are `ghidra-project-chart-research/` for the executable and `ghidra-project-stages/` for the stage overlays.

## TMD models and VDF links

The six song `COMPO` bundles also contain unindexed TMD object data after the named TIM resource section. `extract_tmd.py` scans that trailing data, exports supported polygon geometry as OBJ while preserving vertex order, and writes a model index to `outputs/tmd-extracted/manifest.json`. The current pass exported 149 mesh objects: 25 from `COMPO01`, 21 from `COMPO02`, 23 from `COMPO03`, 29 from `COMPO05`, 22 from `COMPO06`, and 29 from `COMPO07`.

The VDF and TMD data share object indices and exact vertex spans for numerous stage assets. The model index records those candidate links. The VDF lab can open a linked mesh directly with its animation; when more than one mesh has the same span, it presents the candidates. This is a static-data match, not a recovered runtime pointer binding. The exporter currently renders unlit TMD triangle/quadrilateral packets as flat-color geometry; TIM UV/page integration, lit packet variants, scene assembly, and runtime animation events remain incomplete.

## Windows port status

The Windows x64 build is `PaRappaPort/build-gameplay-final/PaRappaPC.exe`. It has a six-stage playable rhythm prototype, the stage-to-bundle mapping above, TIM texture browsing, a VDF animation lab, and TMD model extraction with VDF-to-mesh preview. Its note patterns and timings are still placeholders. It does not yet reproduce the original stage scenes, in-game character animation, complete charts, song audio, progression, or all game behavior, so it is not a complete port. The source and remaining tasks are documented in `PaRappaPort/README.md`.
