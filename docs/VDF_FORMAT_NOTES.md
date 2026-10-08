# PaRappa VDF animation findings

I scanned the raw stage `COMPO*.INT` files under `<game>\assets` and extracted 259 contiguous VDF streams into `outputs/vdf-extracted/`. Their byte offsets and per-frame metadata are in `outputs/vdf-extracted/manifest.json`. The scanner is `scan_vdf.py`.

The normal INT directory declares TIM resources, but the declared payloads end well before the end of each `COMPO*.INT`; the VDF streams were found in this trailing raw data. So the earlier TIM-only extraction was incomplete for animation assets.

## Record layout recovered

The records match the VDF reader in [PSXPrev's VDFParser.cs](https://github.com/rickomax/psxprev/blob/no-deps/Common/Parsers/VDFParser.cs) and the PSY-Q documentation that describes VDF as vertex-difference data for MIME animation.

All values are little-endian:

| Offset | Size | Field |
| --- | ---: | --- |
| `0x00` | 4 | frame count (`u32`) |
| `0x04` | variable | frame records, repeated `frameCount` times |

Each frame record is:

| Size | Field |
| ---: | --- |
| 4 | object/TMD ID (`u32`) |
| 4 | vertex byte offset (`u32`, divisible by 8) |
| 4 | vertex count (`u32`) |
| `vertexCount * 8` | vertex deltas |

Each vertex delta is three signed 16-bit components (`x, y, z`) followed by a 16-bit pad. The byte offset divided by 8 gives the first vertex slot; the parser places the frame's vertices starting at that slot. A frame therefore occupies `12 + vertexCount * 8` bytes, and the whole VDF occupies `4 + sum(frame sizes)` bytes.

## Extraction results

| Stage bundle | Streams |
| --- | ---: |
| `COMPO00.INT` | 8 |
| `COMPO01.INT` | 49 |
| `COMPO02.INT` | 36 |
| `COMPO03.INT` | 41 |
| `COMPO05.INT` | 34 |
| `COMPO06.INT` | 39 |
| `COMPO07.INT` | 35 |
| `COMPO08.INT` | 5 |
| `COMPO09.INT` | 12 |
| **Total** | **259** |

## TMD base meshes

`extract_tmd.py` scans the unindexed portion of the six song bundles for TMD object tables. The parser uses the documented TMD header (`0x41`), object-table-relative offsets, 8-byte vertex records, and variable-length primitive packets. The format details are described in the [PSY-Q 3D Graphics Tools manual](https://psx.arthus.net/sdk/Psy-Q/DOCS/FileFormat47.pdf), sections 2-24 through 2-34.

The exporter writes 149 OBJ mesh objects under `outputs/tmd-extracted/` and preserves each model's vertex order. It supports the unlit triangle and quadrilateral packet types used by the current VDF-linked meshes; previews use flat colors and do not yet sample TIM textures. The manifest links VDF files to TMD objects when the object index and maximum VDF vertex span exactly match. These are strong static candidates, but the game's runtime pointer table has not been traced to prove each association.

| Bundle | Exported mesh objects |
| --- | ---: |
| `COMPO01.INT` | 25 |
| `COMPO02.INT` | 21 |
| `COMPO03.INT` | 23 |
| `COMPO05.INT` | 29 |
| `COMPO06.INT` | 22 |
| `COMPO07.INT` | 29 |

The VDF lab's **Match COMPO model…** action loads a selected candidate and applies the stream's frame deltas. TMD coordinates are converted from PlayStation Y-down to preview Y-up, and the VDF Y deltas receive the same conversion.

The extractor validates frame bounds, object IDs, 8-byte alignment, vertex counts, and file bounds before writing a stream. In Ghidra, the main-disc path lookup at `0x800381f8` resolves backslash-separated paths against the disc directory; `0x8001ac18` retries resource loading through the common CD/resource layer. I have not yet identified a call site in the stage overlay that binds each extracted VDF to its base TMD and playback event, so that mapping remains open.

## References

- [PSY-Q 3D Graphics Tools manual](https://psx.arthus.net/sdk/Psy-Q/DOCS/Devrefs/3dgraph.pdf): MIMEFILT creates VDF vertex-difference data relative to TMD geometry.
- [PSXPrev VDF parser](https://github.com/rickomax/psxprev/blob/no-deps/Common/Parsers/VDFParser.cs): concrete reader layout and sparse vertex placement.
- [PSXPrev format support](https://github.com/rickomax/psxprev): lists both VDF and TOD as separate animation formats.
