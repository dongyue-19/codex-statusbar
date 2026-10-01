# Round 2 UI evidence — transparency, position model, resize stability

Everything here is produced by the code in `src/CodexStatusbar` through the probes and the live
application. Nothing is hand-drawn or retouched. Regenerate with the commands at the bottom.

## Transparency and text quality

| file | what it shows |
|---|---|
| `locked-transparent-dark-144dpi.png` | the raw ARGB surface the window blits (dark theme) |
| `locked-transparent-light-144dpi.png` | the same surface, light theme |
| `locked-transparent-dark-144dpi-over-{dark,light,busy}.png` | the surface composited over a dark, a light and a noisy backdrop — the capsule is gone in all three |
| `locked-opaque-{dark,light}-144dpi.png` | the `transparentBackground = false` fallback, for comparison |
| `locked-transparent-{dark,light}-{96,120}-dpi.png` | the same at 100 % and 125 % scaling |
| `edit-mode-{dark,light}-144dpi.png` | the faint outline + drag hint shown only while positioning |
| `edit-mode-dark-144dpi-over-{light,dark}.png` | that outline on a light and a dark backdrop |
| `waiting-transparent-dark-144dpi.png` | before any conversation is known |
| `neutral-text-dark-144dpi{,-over-busy}.png` | bolt removed, so the edge-colour measurement is not polluted by the glyph's own artwork |

**The rejected alternative, for comparison:**

| file | what it shows |
|---|---|
| `rejected-transparencykey-halo.png` | the same line drawn with GDI `TextRenderer` on a magenta key colour and punched out |
| `rejected-transparencykey-halo-over-busy.png` | the same over noise — the coloured fringe surviving the punch-out is plainly visible |

Measured (`render-probe.json`): the key-colour approach leaves **1746 halo pixels, max channel spread
171**; the layered surface leaves **max channel spread 10**, and **5** for the neutral-glyph case
(the palette colour is `(245,245,247)`, already 2 apart, plus un-premultiplication rounding). The
capsule line measures **278.3 DIP** at every scaling, against 310 DIP of usable width — so the text
cannot reflow when the DPI changes.

## The real window on screen

| file | what it shows |
|---|---|
| `live-over-real-codex.png` | the running application over the real Codex Desktop window, captured from the desktop |
| `onscreen-over-codex.png` | the same capture taken by the live verification script |
| `onscreen-over-codex-moved.png` | after Codex was moved, with the strip following |
| `onscreen-{dark,light}-over-{dark,light,busy}.png` | the real layered window over controlled backdrops, captured from the screen |

`show-probe.json` records, per backdrop, how many pixels of the strip's rectangle differ from the
backdrop alone: **14.5–15.3 %** (only the glyphs and their shadow, ~88 % of the rectangle untouched),
and **0** pixels matching the old capsule colour `rgb(36,38,45)`.

## Position model

`position-probe.json` — 48 checks over `ManualAttachmentCalculator` and `OverlayLayoutCalculator`.
The interesting ones: a drag into the top-right region yields `TopRight, (-24, +16) DIP`; 25 resize
cycles leave the saved attachment byte-identical; a clamp never contaminates it; the same offset is
24 DIP at 96/120/144/192 DPI; and crossing 100 % → 150 % shifts the strip by exactly the DPI scaling
and by the same amount whatever the window width (which a percentage model would get wrong).

`form-probe.json` — the real window's styles and input behaviour: `WS_EX_TOOLWINDOW | WS_EX_LAYERED |
WS_EX_NOACTIVATE`, `WM_MOUSEACTIVATE` answered with `MA_NOACTIVATE` while locked, the window region
equal to capsule ∪ panel, `WindowFromPoint` returning the strip inside its own rectangle and the
window underneath outside it, and edit mode dropping `WS_EX_NOACTIVATE` then restoring it.

`debug-live-position.log` — the `[position]` sections the running application wrote, including the
`Theme: Light (from Auto -> Codex config)` line.

`settings.json` — exactly what a fresh install persists.

## Live verification

`tools\verify_overlay_live.ps1` drives the real Codex window (foreground, shrink, grow, maximise,
restore, move), reads the geometry the application reports, and compares. 22 checks, all passing:

```
before          Codex (321,153) 1920x1224  TopCenter (0,10)   requested (1034,168)  actual (1034,168)
after shrink    Codex (321,153)  738x900   TopCenter (0,10)   requested  (443,168)  actual  (443,168)
grown back      Codex (321,153) 1920x1224  TopCenter (0,10)   requested (1034,168)  actual (1034,168)
maximized       Codex (-11,-11) 2582x1550  TopCenter (0,10)   requested (1033,  4)  actual (1033,  4)
restored        Codex (321,153) 1920x1224  TopCenter (0,10)   requested (1034,168)  actual (1034,168)
moved +240,+135                            strip delta (240,135) == Codex delta (240,135)
```

## Regenerating

```powershell
dotnet run --project src\CodexStatusbar -c Release -- --self-test fixtures
dotnet run --project src\CodexStatusbar -c Release -- --position-probe   $env:TEMP\position-probe.json
dotnet run --project src\CodexStatusbar -c Release -- --render-probe     $env:TEMP\render-probe
dotnet run --project src\CodexStatusbar -c Release -- --show-probe       $env:TEMP\show-probe
dotnet run --project src\CodexStatusbar -c Release -- --form-probe       $env:TEMP\form-probe.json fixtures\form-probe-request.json
pwsh -NoProfile -File tools\verify_overlay_live.ps1
```

## Not published

Four files in the table above are **not** in the public repository, because they were captured from
a live desktop session and contain the author's real conversation content — window titles, message
text and local file paths — and a screenshot cannot be un-published:

| withheld file | regenerate it with |
|---|---|
| `live-over-real-codex.png` | `pwsh -NoProfile -File tools\verify_overlay_live.ps1` (or `tools\capture_codex.ps1`) |
| `onscreen-over-codex.png` | same |
| `onscreen-over-codex-moved.png` | same |
| `debug-live-position.log` | run the overlay with `--debug`; the `[position]` blocks are appended to `%LOCALAPPDATA%\CodexStatusbar\debug.log` |

Everything else listed here — the synthetic-backdrop renders, the offscreen surfaces and the probe
JSON — is committed, and every measurement quoted in the main README is one of those.