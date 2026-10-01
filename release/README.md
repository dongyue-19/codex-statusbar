# CodexStatusbar — 1.0.0-rc1

Release candidate. **Feature-frozen**: no further functional change to the TPS algorithm, the
token/cache algorithm, IPC conversation binding, the UIA `ComposerContextLeft` positioning, the
responsive strategy, typography/baseline/theme, or the click-through behaviour.

A read-only, always-visible 3-metric status strip for the **official OpenAI Codex Desktop client on
Windows**. It is a separate companion process: it does not patch, replace, inject into or hook the
Codex binary. It reads data Codex already writes, and places one small overlay window next to
Codex's own composer toolbar using public Win32 APIs.

```
⚡ 158 tok/s · 2.6M tok · Cache 90%
```

---

## 1. The three metrics — exact definitions

### TPS

```
TPS = 最近一次模型生成的 model throughput，
      基于官方 output token usage 和模型活动区间推导。
```

Formally: `1000 × Σ(official output_tokens of the last turn's model responses) ÷ |union of that
turn's Reasoning and AgentMessage item windows|`.

- The numerator is **always** the official `output_tokens` reported by Codex — never a character
  count, never `chars / 4`, never a web-search token estimate.
- The denominator is the **time the model actually spent emitting output**. Tool execution,
  user think time, queue time and whole-turn wall clock are excluded by construction.
- The denominator is an **interval union**, not a sum: two overlapping model windows merge, so
  `[0s,5s]` + `[4s,10s]` is 10 s, not 11 s.
- Shown with `~` when the value is being held across a tool call; `-- tok/s` when no official
  output token count exists yet (a brand-new turn, or a Codex version without turn timing).
  It is never estimated and never rendered as `0`.

### Total Token

```
Total Token = 当前 conversation 累计 input + output，
              cached input 不重复计算。
```

- `total = Σ input_tokens + Σ output_tokens`. It is **never** `input + cached + output` — that
  would count the cached prefix twice.
- Cumulative for the whole conversation, not per turn, and it follows conversation switches.
- De-duplicated across the two usage carriers Codex writes (`token_count` and
  `token_usage_record`), so a re-emitted record never counts twice.
- Compacted for display: `843 tok`, `12.4K tok`, `5.7M tok`.

### Cache

```
Cache = 当前 conversation 累计 cached_input / input。
```

- `Σ cached_input_tokens ÷ Σ input_tokens × 100%`, rounded for display.
- Rendered as `--` — never `0%`, never `NaN` — when `input == 0` (brand-new conversation) or when
  the cached field is absent.

---

## 2. Default UI

```
⚡ 158 tok/s · 2.6M tok · Cache 90%
```

One line, always visible, no click, no interaction. Defaults:

| default | value |
|---|---|
| **Dock** | docked **left of Codex's Context usage indicator**, inside the composer's bottom toolbar — the same row as the model selector, microphone and send button |
| **Auto theme** | follows the Windows/Codex appearance; light `#1a1c1f`, dark `#dfdfdf`, Codex's own `--app-color-text-foreground` |
| **Full click-through** | every pixel, *including the glyph pixels*, is transparent to the mouse; the strip never takes focus and never appears in Alt+Tab |
| **Responsive variants** | four measured widths; the strip degrades to a shorter form instead of overlapping the composer's left-hand controls, and the full form is used whenever there is room |

The strip is text only — no capsule, no background, no border — with real per-pixel transparency, so
Codex's own chrome shows through.

Responsive ladder (all four are measured, not estimated):

| level | rendering |
|---|---|
| 0 | `⚡ 192 tok/s · 2.4M tok · Cache 95%` |
| 1 | `⚡ 192 t/s · 2.4M · 95%` |
| 2 | `192 t/s · 2.4M · 95%` |
| 3 | `2.4M · 95%` |

**Click-through consequence:** because the locked strip is fully mouse-transparent, the detail panel
cannot be opened by clicking the numbers. It is opened from the **tray icon →
`详情面板 · 展开 / 收起`** instead. That is the deliberate trade for glyph-pixel click-through.

Manual positioning is always available as a fallback: tray → *调整位置*, then drag; the strip drops
`WS_EX_TRANSPARENT`/`WS_EX_NOACTIVATE` for exactly as long as the editor is open and restores them
the moment it closes.

---

## 3. Settings

```
%LOCALAPPDATA%\CodexStatusbar\settings.json
```

Created on first run (`settingsVersion: 3`). It stores the appearance, the theme selection, the
position mode and the manual anchor/offset. Delete the file to return to defaults; it is plain JSON
and is written atomically.

Logs, when `--debug` is used, go to `%LOCALAPPDATA%\CodexStatusbar\debug.log`.

---

## 4. Start

Double-click, or from a terminal:

```
start-monitor.bat
```

With the diagnostics block:

```
start-monitor.bat --debug
```

`start-monitor.bat` starts `CodexStatusbar.exe` from this folder with the arguments you pass
through, and returns immediately. Start it **after** Codex Desktop; it attaches as soon as a Codex
window is present.

Requirements: Windows 10/11 x64 and Codex Desktop running under the same user account. The published
build is self-contained — no .NET runtime installation is required.

No administrator rights are needed and nothing is written outside
`%LOCALAPPDATA%\CodexStatusbar\`.

---

## 5. Diagnostics kept in this build

All probes are headless and change nothing:

| command | what it does |
|---|---|
| `CodexStatusbar.exe --self-test [fixturesDir]` | drives the real parser, formatter, position calculator and settings serialiser; **183 checks**, exit code 0 on success |
| `--debug` | writes the metric block plus the `[position]` geometry block to `%LOCALAPPDATA%\CodexStatusbar\debug.log` |
| `--no-overlay` | headless monitor, debug log only |
| `--ipc-probe <seconds>` | dumps every frame Codex sends on `\\.\pipe\codex-ipc` and the resolved conversation |
| `--thread <id>` | pins the monitor to one conversation id |
| `--sessions <path>` | overrides the sessions root (default `$CODEX_HOME\sessions` or `~\.codex\sessions`) |
| `--settings <path>` | uses an alternate settings file |
| `--position-probe out.json` | the anchor/offset model: 48 checks (anchors, resize stability, DPI, persistence) |
| `--render-probe <dir>` | real layered surfaces at 96/120/144 DPI, both themes, with the ink-extent check |
| `--show-probe <dir>` | the real window, screenshotted from the screen |
| `--form-probe out.json <request>` | drives the strip window directly |
| `--hotkey-probe out.json` | checks that `Ctrl+Alt+Shift+P` can be registered (fails by design while a strip is already running) |

`--debug` output is the intended way to check the dock without a screenshot: it prints the
`Reference source` (`uia-context` / `uia-model` / `uia-composer` / `window-fallback`), the reference
rectangle, the strip rectangle, the gap, the responsive level, the four variant widths and the
remaining width budget. A Codex update that changes its accessibility tree shows up there instead of
as a strip that mysteriously moved.

The `tools\` directory in the source repository additionally holds the UIA/IPC/live-acceptance
scripts (`probe_codex_uia.ps1`, `probe_codex_composer.ps1`, `probe_responsive.ps1`,
`verify_composer_dock.ps1`, `probe_codex_ipc.mjs`, …) and the evidence captured for this build.

---

## 6. Autostart

**Not enabled.** This release installs nothing and registers nothing; it has no autostart
capability of its own and adds no entry to `Run`, Task Scheduler, or the Startup folder.

To start it at login yourself, put a shortcut to `CodexStatusbar.exe` in `shell:startup`
(<kbd>Win</kbd>+<kbd>R</kbd> → `shell:startup`). Do that only once you are happy with the strip's
position.

---

## 7. Verify this exact build

```
CodexStatusbar.exe --self-test
```

Expected: `checks: 183   failures: 0` / `RESULT: PASS`, exit code 0.

Integrity of the shipped binary — see `SHA256.txt` next to this file:

```
Version:      1.0.0-rc1
Build time:   2026-10-01 19:46:19 (+08:00)
EXE:          CodexStatusbar.exe
Size:         64,281,008 bytes (61.3 MB)
SHA-256:      B5ADA92900B202BCD4956294CDAB97B61DE55EFD6B16FDF8AB66208348A9BCA2
```

The executable is not code-signed, so Windows SmartScreen may warn on first launch
(*More info → Run anyway*). That is expected for this release candidate.

---

## 8. Known limitations

- Fallback positioning levels (`uia-model`, `uia-composer`, `window-fallback`) use measured DIP
  constants — graceful degradation, not equivalent to the UIA Context reference.
- The `⚡` glyph depends on GDI+ font fallback; on a system without an emoji/symbol fallback font it
  renders as a box. The numbers are unaffected.
- `Ctrl+Alt+Shift+P` may already be taken by another application; the tray menu still works.
- Codex must be running and must have written a rollout for the active conversation before any
  number becomes meaningful; otherwise every field stays `--`.
- The executable is unsigned (see §7).

---

## 9. License

MIT — see `LICENSE` in the source repository. Portions derive from
`LICENSE-codex-token-overlay`.