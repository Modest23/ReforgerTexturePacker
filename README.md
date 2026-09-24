# Reforger Texture Packer

**by Modest23** (ReforgedZ)

A small Windows desktop tool that packs loose PBR texture maps into **Arma Reforger**'s
packed texture layouts, saved as **8-bit RGBA TIFF with LZW compression** — the format the
Enfusion Workbench importer prefers.

No install, no runtime downloads — just run `ReforgerTexturePacker.exe`
(uses .NET Framework 4.8, which ships with Windows 10/11). It also has a live PBR 3D preview of
your model, per-material texture sets, a Mask / VFX generator and project files — the 3D features
read `.fbx` / `.blend` / `.obj` through **Blender** (any recent version; found automatically).

| Dark | Light |
|------|-------|
| ![Dark theme](docs/screenshot-dark.png) | ![Light theme](docs/screenshot-light.png) |

## Download

**[Download ReforgerTexturePacker.exe](../../raw/main/ReforgerTexturePacker.exe)** (~220 KB)

No installer — save it anywhere and double-click.

## What it outputs

| Suffix | R | G | B | A |
|--------|---|---|---|---|
| `_BCR` | Albedo | Albedo | Albedo | Roughness |
| `_NMO` | Normal +X | Normal −Y | Metalness | Ambient Occlusion |
| `_BCA` | Albedo | Albedo | Albedo | Opacity mask |
| `_VFX` | Dirt mask (generated) | Mud mask (generated) | — | — |
| `_GLOBAL_MASK` | Material 2 mask | Material 3 mask | Material 4 mask | — |

Files are written as `<BaseName>_BCR.tif`, etc. Workbench assigns the correct import profile
(compression + color space) automatically from the suffix when you register the texture.

## Usage

1. **Drag any texture of a PBR set into the window** (or click *Auto-Fill Set…*, or drag a
   file onto the exe itself). The rest of the set is matched by filename suffix:
   `_BaseColor` / `_Albedo` / `_Diffuse`, `_Roughness` / `_Gloss`, `_Normal`, `_Metallic`,
   `_AO` / `_Occlusion`, `_Opacity`, plus combined `_ORM` / `_ARM` maps
   (AO=R, Rough=G, Metal=B — the channel pickers are pre-assigned for you).
2. Or drop/browse files onto individual slots. `Ch:` picks which channel of the source file
   to read (R/G/B/A/Luma).
3. Options:
   - **Invert (gloss)** — auto-checked when a glossiness/smoothness map was matched.
   - **Flip green (OpenGL → DirectX)** — Reforger wants green = −Y; auto-checked when the
     normal map's filename says OpenGL.
   - **Default** values fill empty slots (roughness 0.5, metalness 0, AO 1).
   - **Max size** downscales the output (aspect kept); it never upscales.
4. **Export All** writes `_BCR` + `_NMO` (and `_BCA` when an opacity map is loaded).

Reads `.png .tif .tiff .tga .jpg .bmp` sources. Non-power-of-two outputs get a warning.
Theme (dark/light) is switchable in the header and remembered between runs.

## 3D preview

The right-hand panel shows the model with the loaded maps on it. **Load model…** takes an
`.fbx` / `.blend` / `.obj` (read through Blender in the background, then cached). It renders the
full material with OpenGL - GGX specular from roughness and metalness, the normal map, AO and sky
reflections - using the same channels and defaults the export uses. **Show** switches between the full
material, base color, roughness, metalness, AO, normal map and clay (+ normal detail). Only the faces of this texture
set's material and UDIM tile are textured - the rest is grey. The model is remembered per
texture folder and reloads automatically. Drag = rotate, right-drag = pan, wheel = zoom,
double-click = reset.

**Multi-material models:** once a model is loaded, the panel lists one *texture set* per material
(and UDIM tile). Click a set and the slots on the left switch to its textures - edits apply to that
set. **Match textures** fills every empty set from the texture folder by material name + tile;
**Export all sets** writes `_BCR` / `_NMO` (/ `_BCA`) for every set that has textures, each with its own
output folder and base name. All sets show on the model at once, and they're remembered per model.
A warning appears when AO / roughness / metalness read the same channel of one packed file.

UDIM sets (`_BaseColor.1001.png`, `_Normal.1001.png` …) auto-fill per tile, and the tile is
kept in the output name (`..._1001_BCR.tif`).

## Mask / VFX generator

The **Mask / VFX…** button (needs a Normal map; Color / Rough / Metal / AO all help) opens a
resizable editor with a zoomable preview (wheel = zoom, right-drag = pan, right-click = pick
region, double-click = fit) and three tabs.

### Materials (`_GLOBAL_MASK`)

`MatPBRMulti` reads the global mask as **black = Mat 1, R = Mat 2, G = Mat 3, B = Mat 4** —
a material-ID map, not a dirt mask.

1. **Split the texture into regions** — either *auto-clustered* (k-means over albedo colour,
   roughness, metalness and normal-map surface detail, with speckle cleanup), or from an
   **ID map** (a flat-colour-per-material bake from Blender / Substance; anti-aliased edge
   pixels snap to the nearest ID colour).
2. **Assign regions to materials** — pick Mat 1–4 (buttons or keys `1`–`4`), then left-click:
   *Assign whole region*, *Fill connected area* (just the patch/island you click),
   *Paint brush* or *Erase paint* (`[` / `]` resize; optional "stay inside the region").
   Each region also has a dropdown in the list. **Ctrl+Z** undoes. *Auto-assign* merges
   similar regions down to four as a starting point.
3. **How each channel is filled** — level, plus optional modulation (edges, crevices,
   occlusion, noise, scratches, UV gradient). In-game each channel is cut by the material's
   tiling `Mask_N` (`MaskSharpness_N` / `MaskOffset_N`), so a gradient here turns into
   peeling / wear along it; solid = full coverage. Optional edge softness.

### Dirt / Mud (`_VFX` red / green)

`_VFX` R drives the material's Dirt layer (`DirtBCRMap`, `DirtMaskMap`, `DirtOpacity`,
`DirtLayerSharpness`), G the Mud layer. Keep them soft gradients — the shader thresholds
them against its own tiling mask for the fine pattern. Each layer combines:

- **Where it collects:** everywhere (base), crevices and edges (multi-scale relief from the
  normal map, fine → broad), occlusion (AO map, or estimated), UV gradient.
- **Pattern:** fractal noise, procedural scratches (density / length / angle / spread /
  edges-only), your own tileable grunge texture.
- **Breakup & streaks:** patchy knock-out, and drips running down the UVs.
- **Surface weighting:** roughness, dark albedo, and a multiplier **per material** from the
  Materials tab (e.g. no mud on Mat 3 glass).
- **Output levels:** black / white point, curve, output max (vanilla rarely hits white), softness.

Presets: grime, mud splash, edge wear + scratches, dust, rain streaks, scratches only.

### Bake from 3D model (needs Blender)

**Bake from 3D model…** runs Blender in the background (found automatically, or you point to
`blender.exe` once) on your `.fbx` / `.blend` / `.obj`. You pick which of the model's materials
use this texture set (auto-guessed from the texture name); the UDIM tile comes from the texture
name (`.1001`, `.1002`…). Blender bakes real ambient occlusion (the rest of the model still
shades it), which way each pixel faces, and how high it sits on the model — about 10–15 s.
Dirt / Mud then get **Faces up**, **Faces down**, **Low on model** and **Keep to these**
(e.g. mud patches only underneath and low down), and the presets use them. The bake is
remembered per texture set.

### Export

**Work res** (512/1024/2048) is what everything is computed at; **Export size** resamples the
result (what you see is what you get). Settings, region assignments and paint are remembered
per texture set in `%APPDATA%\ReforgerTexturePacker\masks\` — nothing is written next to the textures.

Note: `_GLOBAL_MASK` needs its compression set manually in Workbench import settings
(`RedHQCompression` for an R-only mask, `ColorHQCompression` for an RGB mask) — the wiki
chart marks channel masks "must set manually".

## Building from source

```powershell
.\build.ps1
```

That's it — it compiles with the C# compiler bundled with every Windows install
(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). No SDK, no NuGet, no Visual Studio.

## Notes

- Windows blocks drag & drop from Explorer into apps running *as administrator* — run it normally.
- Channel data is copied byte-exact (no premultiplied-alpha drift); LZW TIFF output is lossless.
