# Round 3 — the strip moves into the Codex composer toolbar

Everything in this folder was produced by running against the **real** Codex Desktop window
(`OpenAI.Codex 26.928.3736.0`, 2048×1224 at 150% scaling, Light theme). Nothing here is a mock-up.

## What changed

The strip used to float over the top of the Codex window. It now sits inside the composer's bottom
toolbar row, immediately left of the official **Context usage** indicator, on the same line as the
model selector, microphone and send button — and it is sized, coloured and positioned from
measurements of those controls.

```
[ + ] [⚠ 完全访问]                    ⚡ 192 tok/s · 2.4M tok · Cache 95%   ◔   deepseek-v4.1-flash 高 ⌄   🎙   ↑
└──────────── composer's own controls ────────────┘└──── docked strip ────┘└──────── official right cluster ────────┘
```

## Files

| file | what it is |
|---|---|
| `live-docked-full-window.png` | the deliverable, captured from the screen: strip, Context ring, model selector, mic and send all visible |
| `rc1-live-docked-full-window.png` | the same capture from the **1.0.0-rc1 release binary** (`release\CodexStatusbar.exe`, started through `release\start-monitor.bat --debug`) — the acceptance screenshot for the release candidate |
| `strip-zoom8x.png` | the strip's own pixels, magnified 8× — where the typography is judged |
| `nativeModel-zoom8x.png` | the same magnification of Codex's model selector, the reference |
| `live-type-metrics.json` | `measure_native_type.py` output for both of the above, from the same capture |
| `live-narrow-900px-level3.png` | the responsive policy at 900 px: `2.4M · 95%`, adjacent to nothing it should not touch |
| `composer-dock-verification.json` | 66 live checks across 8 window geometries (resize, maximise, restore, move) |
| `position-probe.json` | 48 headless anchor-model checks |
| `render-probe.json` | 20 offscreen surfaces at 96/120/144 DPI, both themes, plus the clipping invariant |
| `debug-live-position.log` | the `[position]` blocks the running overlay wrote |
| `settings.json` | the migrated v3 settings file |

## The measurements that decided the design

### The reference

Read live from Codex's UI Automation tree, not inferred:

```
Reference source:    uia-context
   Name:             上下文用量：13%
   AutomationId:     (none)
   ControlType:      Image
   ClassName:        icon-xs inline-flex items-center justify-center align-middle text-codex-description
Reference rect:      (1708, 1396) 25x25
Row rect:            (1738, 1385) 254x42     <- the model selector, i.e. the toolbar row's own box
Composer rect:       (995, 1292) 1105x147
Left cluster rect:   (995, 1385) 190x42      <- ends at 1185, the permission chip's right edge
```

The Context indicator is a **direct child of the composer card**, and it survived every geometry
change tested. The model selector's cached element goes stale after a relayout (React replaces the
node), so it is only ever used as a fallback with a fresh walk.

### Typography

| property | value | where it came from |
|---|---|---|
| family | **Segoe UI** | Codex's own `--font-sans-default: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`; on Windows only the third entry resolves |
| size | **13 DIP** | `[data-codex-window-type=electron] { --text-sm: 13px }` in Codex's bundle, confirmed twice on screen |
| line height | 18 DIP | `leading-[18px]` in the control's class list; the permission label's UIA text box is 27 px = 18 DIP |
| weight | Regular | FreeType comparison below |
| colour (light) | `#1a1c1f` | `--app-color-text-foreground`; the darkest pixel measured in Codex's own body text is rgb(26,28,31) |
| colour (dark) | `#dfdfdf` | `--color-text` resolves to `--gray-fixed-150` in dark |
| shadow | off | docked on Codex's own background, so the theme match carries readability |

Size was cross-checked three independent ways:

1. **CJK advance.** `完全访问` is four full-width ideographs; its UIA text box is 78 px wide, so one em
   is 19.5 px — exactly 13 DIP at 150%.
2. **CSS token.** `--text-sm` is overridden to `13px` for the Electron window type.
3. **Rasterised comparison.** `tools/compare_ui_font.py` renders `deepseek-v4.1-flash` in each
   candidate and compares ink extents with the screenshot:

   | font | size | ink width | vs native 172 px |
   |---|---|---|---|
   | **Segoe UI Regular** | **13 DIP** | **170.0** | **−2.0** |
   | Segoe UI Regular | 14 DIP | 182.8 | +10.8 |
   | Segoe UI Variable | 13 DIP | 167.0 | −5.0 |
   | Segoe UI Semibold | 13 DIP | 182.0 | +10.0 |

   Blink rounds each glyph advance to a whole device pixel, which accounts for the remaining 2 px.

Measured on the finished strip and on the native label **in the same screenshot**:

| | x-height band (screen y) | baseline |
|---|---|---|
| docked strip | 1401–1413 | **1413** |
| Codex model label | 1398–1412 | **1412** |

Baseline error **1 px**; both reach the same darkest ink `rgb(26, 28, 31)`.

### Geometry

| | value |
|---|---|
| gap (Context.left − strip.right) | **15 px = 10 DIP**, identical in all 8 geometries |
| vertical centre error (`|row centre − strip centre|`) | **0.5 px** in all 8 geometries |
| strip size | 312×33 px = the measured text plus 6 DIP of padding each side |
| overlap with the composer's own controls | none, in any geometry |

### Click-through

The strip is a layered window built with `UpdateLayeredWindow`, and Windows hit-tests such a window
**against its own alpha**. So the mouse is taken exactly where the glyphs are and passes straight
through every transparent pixel inside the rectangle — a stronger guarantee than a rectangular input
region. Measured by sampling 36 points across the strip for each geometry: some resolve to the
overlay (the glyphs), the rest to Codex.

### Responsive behaviour

The verbosity ladder, widest first, measured at 13 DIP:

| level | text | width (DIP) |
|---|---|---|
| 0 | `⚡ 192 tok/s · 2.4M tok · Cache 95%` | 195.9 |
| 1 | `⚡ 192 t/s · 2.4M · 95%` | 121.3 |
| 2 | `192 t/s · 2.4M · 95%` | 112.6 |
| 3 | `2.4M · 95%` | 63.1 |

The budget is the space between the composer's own left-hand controls and the Context indicator. When
even level 3 does not fit, the strip hides rather than printing over Codex's buttons.

## Live verification

`tools/verify_composer_dock.ps1` — **66 checks, 0 failures**:

| step | window | level | gap | centre error |
|---|---|---|---|---|
| A current | 2048×1224 | 0 (full) | 10 DIP | 0.5 px |
| B 1100 px wide | 1100×1224 | hidden (budget 61.3 < narrowest 63.1 DIP) | — | — |
| C 900 px wide | 900×1224 | 3 (totals only) | 10 DIP | 0.5 px |
| C 1700 px wide | 1700×1224 | 0 (full) | 10 DIP | 0.5 px |
| D maximized | 2582×1550 | 0 (full) | 10 DIP | 0.5 px |
| E restored | 1700×1224 | 0 (full) | 10 DIP | 0.5 px |
| F moved +120,+60 | 2048×1224 | 0 (full) | 10 DIP | 0.5 px |
| G frame restored | 2048×1224 | 0 (full) | 10 DIP | 0.5 px |

Every step also re-reads the Context rectangle independently through UI Automation and checks it
against what the overlay logged.

## Cost

**31.2 ms of CPU over 45 s = 0.069 % of one core** while visible, re-laying out every 350 ms with the
UI Automation tracker running. Unchanged from the previous round, so the automation does not
measurably cost anything.

## Reproducing this folder

```powershell
pwsh -File tools\probe_codex_uia.ps1                       # dump the bottom band of the UIA tree
pwsh -File tools\probe_codex_composer.ps1                   # the composer subtree and its identities
pwsh -File tools\probe_uia_stability.ps1                    # does the reference survive a resize?
pwsh -File tools\capture_codex.ps1 -Out shot.png            # screenshot the real Codex window
python tools\measure_native_type.py --image shot.png --origin 308,244 --box "x=1708,1396,25,25"
python tools\compare_ui_font.py --target native.json --target-key modelNameLatin
pwsh -File tools\verify_composer_dock.ps1                   # the live acceptance suite
```
## Round-3 release-candidate checks

Two final checks were run before freezing, and both changed the code.

### 1. Locked = completely click-through

Previously the strip relied on a layered window's alpha hit testing, which passes clicks through
transparent pixels but still **swallows the ones that land on a glyph**. That is not "click-through"
in the sense that matters: clicking a number in Codex's toolbar with the strip's `192` under the
cursor would have gone to the strip instead of to Codex.

Now `WS_EX_TRANSPARENT` is set alongside `WS_EX_NOACTIVATE` while locked and `WM_NCHITTEST` answers
`HTTRANSPARENT` unconditionally. Both are dropped by `BeginEditMode` and restored by `EndEditMode`
(`CreateParams` is re-evaluated by the `RecreateHandle()` those already call, so the switch is atomic
with the handle that carries it).

Verified by sampling **72 points** across the strip's rectangle in each of eight window geometries
and asserting that **none** resolve to the overlay — `locked is completely click-through`, 8/8.

The consequence, stated plainly: the detail panel could previously be opened by clicking the numbers.
**Tray → `详情面板 · 展开 / 收起`** is now its entry point. Same panel, same toggle.

### 2. The responsive anomaly was a real bug

The earlier report showed `1100 px → hidden` while `900 px → level 3`, which is not monotone. The
sweep (`tools/probe_responsive.ps1`) records, at each width, the Context and model rectangles re-read
independently through UI Automation *and* the overlay's own composer rectangle, left-cluster boundary,
width budget, level, visibility and reference source:

| width | context | model | composer | UIA boundary | overlay limit | budget | level | visible |
|---|---|---|---|---|---|---|---|---|
| 2048 | (1708,1396) | (1738,1385) | (995,1292) 1105x147 | 1185 | 1191 | 334.7 | 0 full | yes |
| 1700 | (1534,1396) | (1564,1385) | (821,1292) 1105x147 | 1011 | 1017 | 334.7 | 0 full | yes |
| 1400 | (1287,1396) | (1317,1385) | (768,1292) 1104x147 | 958 | 964 | 205.3 | 0 full | yes |
| 1200 | (1086,1396) | (1116,1385) | (768,1292) 1104x147 | 874 | 880 | 127.3 | 1 | yes |
| 1100 | (987,1396) | (1017,1385) | (768,1292) 1104x147 | 874 | 872 | 66.7 | 3 | yes |
| 1000 | (887,1396) | (917,1385) | (410,1292) 1104x147 | 600 | 606 | 177.3 | 1 | yes |
| 900 | (786,1396) | (816,1385) | (410,1292) 1104x147 | 600 | 606 | 110 | 3 | yes |
| 800 | (687,1396) | (717,1385) | (410,1292) 1104x147 | 516 | 522 | 100 | 3 | yes |

**Nothing is hidden at any width**, and the overlay's left-cluster limit agrees with an independent
UI Automation measurement at all eight (within 2 px). Two separate causes were found and fixed:

1. **The overlay counted `display: contents` wrappers as barriers.** Those elements have no layout box,
   so Chromium synthesises a rectangle for them that is wider than any of their children — which
   shrank the budget by 100 px or more in earlier runs. The left cluster is now restricted to real
   `Button` / `Image` children, the same rule the independent checker uses.
2. **One tree walk is not enough after a relayout.** The Context indicator's rectangle updates
   *before* its siblings', so the single walk triggered by the reference moving could read the
   previous composer layout for the left cluster and then keep it (the 20 s resync was the only thing
   that fixed it). The tracker now takes two walks after a geometry change; the second sees the
   settled layout. This is what produced `1100 px → budget 243.3 → level 0` (the full strip over the
   permission chip's neighbourhood) where the truth is `66.7 → level 3`.

A third, defensive change: a budget is only ever *raised* on a frame where the reference rectangle has
stopped moving, so a stale read can shorten the strip but never lengthen it.

**What remains non-monotone is Codex's own layout.** The composer's left edge jumps 410 → 768 between
1000 and 1100 px (sidebar collapses/expands) and 768 → 821 between 1400 and 1700. Its own left-hand
cluster jumps with it — 600 → 874, then 958 → 1011 — and it moves *further right* than the Context
indicator does (274 px vs 100 px across that breakpoint). Since the budget is
`context.left − gap − cluster.right`, it necessarily dips at 1100 (66.7 DIP) and recovers at 1200.
That is Codex's responsive layout, measured, not our arithmetic.

### A harness bug worth recording

Driving a window through maximise/restore without capturing `WINDOWPLACEMENT` left the Codex window
parked at `(-32000, -32000)` with `IsWindowVisible = False` and a clobbered restore size — the sentinel
Windows uses for a minimised window. Both tools now capture the placement up front, repair an unusable
starting state (including `SW_SHOW` first), and restore in a `finally` that also re-checks visibility.
The overlay was hardened to match: a `(-32000, -32000)` host or reference rectangle is recognised as
unusable and the strip hides cleanly with a sane log, instead of reporting
`budget -21052.7 DIP` and sending a future diagnosis down the wrong path.

## Not published

Four files referenced above are **not** in the public repository. They were screenshotted from a
live desktop, so they contain the author's real conversation — including the titles of other
conversations in Codex's sidebar — and a screenshot cannot be un-published:

| withheld file | regenerate it with |
|---|---|
| `live-docked-full-window.png` | `pwsh -NoProfile -File tools\capture_codex.ps1 -Out <path> -Window codex` |
| `rc1-live-docked-full-window.png` | same |
| `live-narrow-900px-level3.png` | same, after `tools\probe_responsive.ps1` resizes the window to 900 px |
| `debug-live-position.log` | run the overlay with `--debug`; the `[position]` blocks go to `%LOCALAPPDATA%\CodexStatusbar\debug.log` |

The three 8× type crops (`strip-zoom8x.png`, `nativeModel-zoom8x.png`, `nativeContext-zoom8x.png`)
crop to the toolbar row only and *are* committed — they are what the baseline and ink measurements
below were read from, together with `live-type-metrics.json`.
