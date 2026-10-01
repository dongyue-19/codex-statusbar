# CodexStatusbar

<https://github.com/dongyue-19/codex-statusbar> · MIT licensed · **v1.0.0-rc2** (release candidate,
feature-frozen)

**English** · [简体中文](README.zh-CN.md)

![CodexStatusbar docked in Codex's composer toolbar, immediately left of the Context usage indicator](docs/images/docked-strip.png)

**Get the binary:** download `CodexStatusbar.exe` from the
[latest release](https://github.com/dongyue-19/codex-statusbar/releases) — a self-contained 61 MB
single file, no .NET runtime needed — drop it next to `start-monitor.bat` and run that. The exe is
a release asset rather than a committed file, so the repository stays small. `release\` keeps the
launcher, the release notes and the SHA-256 of the shipped binary.

**Build it yourself:** `pwsh -File build.ps1` (needs the .NET 10 SDK) — see §3.

A lightweight, always-visible status strip for the **official OpenAI Codex Desktop client on
Windows**. It shows three numbers, directly, without any click:

```
⚡ 243 tok/s   ·   5.7M tok   ·   Cache 98%
```

By default it docks **inside Codex's own composer toolbar** — immediately left of the Context usage
indicator, on the same line as the model selector, microphone and send button. It draws text only
on a fully transparent background, so it reads as part of Codex's chrome rather than as a HUD, and
when the toolbar gets narrow it steps down to a shorter form instead of overlapping the controls to
its left. A manual nine-anchor mode with a DPI-aware offset is still there as a fallback (§4).

It is a **separate companion process**. It does not patch, replace, inject into, or hook the
Codex binary. It reads data that Codex already writes, and it attaches a small overlay window
next to the Codex window using public Win32 APIs.

---

## 1. The three primary metrics — strict definitions

### A. TPS — model output speed

```
turn_tps = 1000 × Σ(official output_tokens of the turn's model responses)
                ÷ |union of the turn's Reasoning and AgentMessage item windows|
```

In words: **official output tokens divided by the time the model actually spent emitting
output.** Only `Reasoning` and `AgentMessage` item windows count. `CommandExecution`,
`FileChange`, `McpToolCall`, `WebSearch`, `UserMessage`, `ImageView` durations are **excluded by
construction** — no tool execution, no user think time, no queue time, no whole-turn wall clock
ever enters the denominator.

**The denominator is an interval union, not a sum.** `Reasoning` and `AgentMessage` windows can
overlap or touch, and the merged length is what counts: two windows covering `[0 s,5 s]` and
`[4 s,10 s]` are **10 s** of model activity, not `5 + 6 = 11 s`. Tool windows are never merged in,
so a tool call sitting *between* two model windows does not bridge them.

This is **not** an estimate and **not** a character-count proxy — the numerator is always an
official token count and the denominator is always measured model time. It is also not
byte-identical to the speed you would measure off the raw SSE stream, so it is described as
*model throughput derived from official token usage* rather than as being mathematically exact.

**Measured accuracy.** `tools/groundtruth_tps.mjs` starts its own app-server with
`experimentalRawEvents`, runs varied turns, and for each turn records both the speed measured
directly from the streaming deltas and the speed this formula derives — over the same numerator,
so the only difference is how the elapsed time was obtained. 14 turns (short/long prose,
reasoning-heavy, code-heavy, CJK, mixed-language, markdown tables, two effort levels):

| statistic | value |
|---|---|
| turns measured | 13 (a 2-token reply had no usable window) |
| **median relative error** | **0.04 %** |
| **P95 relative error** | **16.7 %** |
| **max relative error** | **16.7 %** |
| turns within 0.2 % | 11 of 13 |

Every turn of realistic length agrees to within 0.0–0.2 %. The single outlier is a 57-token answer
whose entire model window was **18 ms** — there the resolution of the timing signal, not the
formula, is the limit. Practical consequence: treat any reading whose model window is under about
0.5 s as noisy; the expanded panel and the debug log both print the window length so you can judge.

**Token semantics, verified.** `output_tokens` is everything the model emitted, and
`reasoning_output_tokens` is a **subset** of it, so `visible = output − reasoning`. The numerator
therefore includes the hidden reasoning tokens, and the denominator includes the `Reasoning`
window, so both sides cover the same span. Checked on every measured turn
(`reasoning_output_tokens <= output_tokens` held in all 14).

* The numerator is **always an official token count**. Tokens are never estimated from
  characters, and no tokenizer is used anywhere in this project.
* `live_tps` (`Current realtime TPS`) is the same ratio recomputed over the **in-flight turn so
  far**; it advances every time a model response completes.
* `last_turn_tps` (`Last completed TPS`) is the final value of the turn that just finished. It is
  **retained after generation stops** — the strip does not reset to `--`.
* Successive samples are smoothed with an EMA (`alpha = 0.35`, under ~3 samples of lag) so the
  number does not jitter on response boundaries.
* A leading `~` (`⚡ ~192 tok/s`) marks a value that is *held*: generation has moved on (a tool
  call completed, or more model time accrued) since the value was measured.
* If it cannot be computed it is `--`. A non-finite, zero or negative value is `--`, and a ratio
  above 100 000 tok/s is refused outright rather than displayed. **Nothing is ever fabricated.**

**Why there is no raw streaming delta.** Codex Desktop persists no streaming deltas: the rollout
JSONL contains `item_completed` records only (`item_updated` / `agent_message_delta` do not exist
as persisted records), and the desktop's own `codex app-server` is stdio-bound to the Electron
main process, so an external process cannot join its event stream. `item_completed` does carry
`started_at_ms` / `completed_at_ms`, which is the official per-item model-output window used above.

**TPS state machine.** `idle` → `waiting` → `generating` → `completed`, reported verbatim in the
debug log:

| state | what the strip shows |
|---|---|
| `idle` — no turn seen yet | `-- tok/s` |
| `waiting` — new turn, no model output yet | `-- tok/s` (never the previous turn's value) |
| `generating` — model output measured, turn live | the live value, updated per response |
| `generating`, tool running | the last model value is **kept**, never `0 tok/s` |
| `completed` — turn finished | the turn's final value, retained |

### B. Total tokens — current conversation, cumulative

```
conversation_total_tokens = Σ input_tokens + Σ output_tokens
```

This is **not** context-window usage and **not** the current prompt length. It is everything the
model has processed in this conversation since it was created.

`cached_input_tokens` is a **subset of** `input_tokens`. Therefore:

```
total = input + output          ✔  correct
total = input + cached + output ✘  double-counts the cache, must never be used
```

Worked example from a real session on this machine:

```
input          2,126,561
cached input   2,045,696   (already inside input — do not add)
output            23,166
-------------------------------------
total          2,149,727   = input + output   (equals official total_tokens)
wrong formula  4,195,423   = input + cached + output
```

Display formatting (one decimal place):

| range | shown |
|---|---|
| `< 1K` | `843 tok` |
| `1K … 999,999` | `12.4K tok` |
| `≥ 1M` | `5.7M tok` |

The full integer is always kept internally and shown in the expanded panel / debug log.

It is bound to the **conversation currently open in Codex Desktop**, not to all of `~/.codex`.
Switching conversation in Codex's sidebar switches the strip (see §4).

### C. Cache hit rate — current conversation, cumulative

```
cache_hit_rate = Σ cached_input_tokens / Σ input_tokens × 100%
```

Averaged over the **whole conversation**, not just the last turn. Displayed with no decimals
(`Cache 98%`; `97.88%` in the expanded panel). When `input_tokens == 0` it is `Cache --` —
never `NaN`, never `0%`.

---

## 2. Where the numbers come from

| metric | primary source | recovery / fallback source |
|---|---|---|
| TPS | rollout `event_msg/item_completed` (`started_at_ms`, `completed_at_ms`) + per-response official `output_tokens` | none — `--` if unavailable |
| Total | rollout cumulative usage (`input + output`) | `state_5.sqlite` → `threads.tokens_used` |
| Cache | rollout cumulative usage (`cached_input_tokens`, `input_tokens`) | none |
| **which conversation is active** | **`\\.\pipe\codex-ipc` broadcast `thread-stream-following-changed` → `conversationId`** | `state_5.sqlite` → `threads` ordered by `recency_at_ms` |
| TTFT / turn duration | rollout `event_msg/task_complete` (`time_to_first_token_ms`, `duration_ms`) | none |

Every source is opened **read-only**. Session files are never modified. No network access, no
telemetry, no proxying, no MITM, no DLL injection, no Electron internals.

### Usage carriers and de-duplication

Two official carriers carry the same cumulative counters, and which ones exist depends on the
Codex version. Both are supported:

| carrier | where | present on |
|---|---|---|
| `token_usage_record` | `payload.thread_token_usage` | Codex 0.159+ |
| `event_msg` / `token_count` | `payload.info.total_token_usage` | **all** versions; the **only** carrier on 0.151 and older |

Verified on this machine's corpus: 423 of 501 session files contain **no `token_usage_record` at
all**, so a monitor that reads only that one reports `0 tok` for most conversations.

**Both carriers are cumulative, so the newest reading replaces the previous one — nothing is ever
added.** That makes a duplicate reading harmless by construction, and it is also counted and
reported (`Dedup:` in the debug log) so a future Codex that starts emitting *deltas* shows up in
diagnostics instead of silently corrupting the totals.

Two measured traps, both fixed and pinned by the self-test:

1. **Per-response double counting.** On 0.159 every response is reported twice with byte-identical
   usage: once as `token_usage_record.usage` and once as `token_count.info.last_token_usage`.
   A dedup key based on the *carrier* therefore counted every response twice — measured exactly
   `2x`, which doubled TPS. The key is now derived from the **values** (`input:cached:output:total`),
   which are identical for the same response, so whichever carrier arrives first wins and the
   second is ignored.
2. **`turn_token_usage` is not a turn total on this version.** On Codex 26.928.3736.0
   (core 0.159.2) `turn_token_usage` is a byte-identical copy of `thread_token_usage` for every
   record — the whole conversation's counters. Trusting it as the per-turn numerator put the entire
   conversation's output into one turn. It is now only used when it genuinely differs from
   `thread_token_usage`.

### Reading strategy (performance)

* Session files reach 70–270 MB. The file is **never parsed whole**: a bounded **4 MB tail** is
  read first to recover cumulative usage and turn state, then only **new bytes are parsed** from
  that offset onward.
* If that 4 MB tail yields no cumulative usage, the window is **widened backwards (doubling) until
  usage is found or the file head is reached**, re-parsing each window from scratch so the state
  always reflects the entire window. The fast path stays O(4 MB); the extension is a safety net and
  the debug log flags it (`tail-window-extended:<n>MB`). It never happened once in a 501-file,
  1 GB corpus.
* With a new conversation the tail read recovers the totals immediately, so restart recovery is
  instant and history-independent.
* Baseline startup restores the active thread from the IPC broadcast (Codex sends it immediately
  on connect), so a monitor restart lands on the right conversation.
* No hot-loop scanning of `~/.codex`. Change notification is event-driven (`FileSystemWatcher`)
  with offset-based incremental parsing, so per-update cost is O(new bytes).
* Per-conversation state is cached as
  `thread_id → { input, cached, output, current_turn, tps_state }`.

### Failure behaviour

| situation | behaviour |
|---|---|
| Codex not started / `codex-ipc` absent | reconnects with exponential backoff (350 ms → 5 s), no busy-loop; falls back to `state_5.sqlite` and reports `active-thread-source = sqlite-fallback` |
| Codex restarts / Electron reloads | the pipe disappears and is re-created; the monitor reconnects on its own and flips back to `active-thread-source = ipc` |
| `initialize` fails or a frame is malformed | the frame is skipped, the listener reconnects; one bad message never kills the listener |
| app-server/file unavailable | Total and Cache still work from the session file; TPS shows `--` |
| rollout format changed | unknown records are ignored, counted, and listed under `Warnings:` in the debug log; the process never crashes |
| brand-new empty conversation | `⚡ -- tok/s · -- tok · Cache --`, picked up as soon as the file appears |
| no cached field present | `Cache --` |

---

## 3. Install and run

### Normal use — nothing to do

Once installed (see below), there is **no manual step, ever**. Windows logon starts
`CodexStatusbar.exe --background`, which waits quietly with a tray icon and no window; the moment
Codex Desktop appears it attaches by itself, and when Codex is closed the strip disappears while the
watcher keeps waiting for the next launch. No `start-monitor.bat`, no PowerShell, no "start the
monitor first", nothing to redo after a reboot. §10 covers exactly how that works, what is written
to the registry, and how to turn it off.

`start-monitor.bat` is kept, but only for the cases that need it: debugging, troubleshooting and a
one-off manual launch.

### From a release

Download `CodexStatusbar.exe` from
<https://github.com/dongyue-19/codex-statusbar/releases>, put it beside `start-monitor.bat`, then:

```
start-monitor.bat
```

The published build is self-contained, so no .NET runtime installation is needed. Verify the
download against `release\SHA256.txt` first:

```powershell
Get-FileHash .\CodexStatusbar.exe -Algorithm SHA256
```

### From source

Requires the **.NET 10 SDK** (`net10.0-windows`, Windows x64):

```powershell
pwsh -File build.ps1              # build + self-test + position probe + publish
pwsh -File build.ps1 -SkipTest    # build + publish only
```

`build.ps1` writes `dist\CodexStatusbar.exe`, which is what `start-monitor.bat` prefers; if `dist\`
is empty the launcher falls back to `src\CodexStatusbar\bin\Release\net10.0-windows\`. Building does
not modify anything outside the repository, and the overlay never writes outside
`%LOCALAPPDATA%\CodexStatusbar\`.

Requirements: Windows 10/11 x64, Codex Desktop running under the same user. Start the strip after
Codex Desktop; it will attach as soon as a Codex window is in the foreground.

### Command line

| flag | effect |
|---|---|
| *(none)* | overlay + tray icon |
| `--debug` | also write the diagnostics block (§5) to `%LOCALAPPDATA%\CodexStatusbar\debug.log` |
| `--background`, `--watch-codex` | the logon mode: tray only, no window, attaches when Codex appears — this is what the Run value launches |
| `--no-overlay` | headless; used for automated testing, produces the debug log only |
| `--thread <id>` | pin the monitor to one conversation id |
| `--sessions <path>` | override the sessions root (default `$CODEX_HOME\sessions` or `~\.codex\sessions`) |
| `--install-startup` | register "Start with Windows" in HKCU and remember the choice |
| `--uninstall-startup` | remove the registration and remember the choice |
| `--startup-status` | print the registry key, the resolved command and whether it still points at this exe |
| `--restart-wait` | internal: lets a restarting instance wait for the outgoing one to release the single-instance mutex |

### Start with Windows

**On by default.** The first launch of a build that has this feature registers
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `CodexStatusbar` =
`"<full path>\CodexStatusbar.exe" --background`. No administrator rights are involved at any point.
The tray's **Start with Windows** item toggles it later, and turning it off is permanent: no upgrade
re-enables it behind your back. §10 has the details.

---

## 4. Overlay behaviour

### Appearance — text only, no capsule

The strip draws **glyphs only**. There is no capsule fill and no border, so Codex's own chrome shows
through behind the numbers:

```
⚡ 243 tok/s · 5.7M tok · Cache 95%
```

It is docked inside Codex's composer toolbar, immediately left of the official **Context usage**
indicator and on the same line as the model selector, microphone and send button:

```
[ + ] [⚠ 完全访问]          ⚡ 192 tok/s · 2.4M tok · Cache 95%   ◔   deepseek-v4.1-flash 高 ⌄   🎙   ↑
```

And it is built to look like part of that toolbar rather than like a HUD:

| | strip | how it was chosen |
|---|---|---|
| font | **Segoe UI Regular, 13 DIP** | Codex's `--font-sans-default` resolves to Segoe UI on Windows, and `[data-codex-window-type=electron]` overrides `--text-sm` to `13px`. Cross-checked against the on-screen ink: `完全访问` is four full-width ideographs in a 78 px box, i.e. a 13 DIP em at 150 %. |
| colour (light) | `#1a1c1f` | Codex's `--app-color-text-foreground` — and the darkest pixel measured in Codex's own body text is rgb(26,28,31) |
| colour (dark) | `#dfdfdf` | Codex's `--color-text` in dark |
| line height | 18 DIP | `leading-[18px]`, confirmed by the permission label's 27 px UIA text box |
| shadow | **off** | docked on Codex's own background, the theme match is what carries readability |
| horizontal padding | 6 DIP each side | the window is sized from the measured text, so there is no fixed width to leave slack in |

The result, measured from the same screenshot as the control it sits beside: the strip's baseline is
**1 px** from the model selector's, and both reach the same darkest ink `rgb(26, 28, 31)`.

This is real per-pixel transparency, not a key colour: the window is a layered window
(`WS_EX_LAYERED` + `UpdateLayeredWindow`) painted from a 32-bit premultiplied ARGB surface, so
"background" pixels are genuinely alpha 0. Roughly 88 % of the strip's rectangle is fully
transparent and only the glyphs change the underlying pixels (measured — see
[`docs/evidence/round2-ui`](docs/evidence/round2-ui/README.md)).

**Why not `TransparencyKey`.** That technique draws the glyphs against a magic colour and punches it
out, so every anti-aliased edge keeps a tint of that colour — the classic halo. It is measurable:
the same line drawn that way leaves **1746 coloured pixels with a channel spread of 171**, where the
layered surface stays at **10** (and at 5 for a neutral glyph, which is the colour's own difference
plus un-premultiplication rounding).

Text is therefore rendered with GDI+ and **grayscale** anti-aliasing. ClearType's sub-pixel filter
assumes an opaque backdrop; on a transparent surface its per-channel blending is exactly what
produces coloured fringes.

**Readability.** Docked in the composer the theme match does the work and no shadow is drawn. If the
strip is ever moved somewhere it disagrees with its backdrop, **tray → `文字阴影`** (or
`"textShadow": true`) adds a 1 DIP shadow in the opposite polarity plus a fainter second pass.

### Theme

`theme = "Auto"` (the default) follows **Codex's own** appearance setting, read read-only from
`appearanceTheme` under `[desktop]` in `%USERPROFILE%\.codex\config.toml`, and falls back to the
Windows app theme only when Codex has not recorded one. This matters: Codex's setting is routinely
the opposite of the system one, and a Windows-only `auto` would paint white glyphs onto Codex's light
chrome. Force it with the tray **主题** menu or `theme = "Dark"` / `"Light"` in the settings file; the
menu shows what `Auto` resolved to.

### Position — docked to the Context control, never a percentage

The default `positionMode` is **`ComposerContextLeft`**: the strip is docked inside the composer
toolbar, its right edge a fixed number of DIP left of the official Context-usage indicator and its
vertical centre on the toolbar row.

```
strip.right   = ContextControl.left − contextGapDip        (default 10 DIP, target band 8–12)
strip.centreY = toolbar row centre                          (the model selector's own box centre)
```

Both terms are live UI Automation rectangles. **No absolute coordinate is stored** — so changing the
window size, moving Codex, maximising it, moving to another monitor or changing the DPI all keep the
strip 10 DIP from the Context control without anything being recomputed from an observation.

The reference is found in a ladder, and the debug log always says which rung was used:

| level | source | how it is found |
|---|---|---|
| 1 | `uia-context` | the `Image` child of the composer card whose name starts `上下文用量` / `Context usage`, or whose class contains `codex-description`, or which is simply the composer's only `Image` child. **This is what runs in practice.** |
| 2 | `uia-model` | a `Button` exposing the **ExpandCollapse** pattern, to the right of the Context indicator; its left edge stands in for the Context control's, minus the measured 20 DIP the Context indicator occupies |
| 3 | `uia-composer` | the composer card itself, with the toolbar row derived from its bottom-right corner |
| 4 | `window-fallback` | nothing but the Codex window rectangle, plus the same measured bottom-right DIP offsets |

The composer card is identified structurally — it is the ControlView parent of the `ProseMirror`
contenteditable — and the model selector by its pattern rather than its name, because its
`AutomationId` is a Radix-generated `radix-_r_NN_` that changes between renders and its `Name`
contains the model name.

**Responsive.** The strip is sized from its measured text and shortened only when Codex is genuinely
too narrow, against the real room between the composer's own left-hand controls and the Context
indicator:

| level | text | width at 13 DIP |
|---|---|---|
| 0 | `⚡ 192 tok/s · 2.4M tok · Cache 95%` | 195.9 DIP |
| 1 | `⚡ 192 t/s · 2.4M · 95%` | 121.3 DIP |
| 2 | `192 t/s · 2.4M · 95%` | 112.6 DIP |
| 3 | `2.4M · 95%` | 63.1 DIP |

At every normal width the **full** wording is shown; the short forms appear only when the full one
would collide with Codex's buttons. When even level 3 does not fit, the strip **hides** rather than
printing text over them. The debug log reports the level, every variant's measured width, and the
budget, so a cramped window is distinguishable from a broken selector at a glance.

### Manual positioning

Docking is the default, but the previous anchor-and-offset model is still there and is what
**Adjust Position** switches to. The tray **`Position`** menu offers:

| item | behaviour |
|---|---|
| `Dock left of Context` | the default above |
| `Manual position` | nine anchors of the Codex window + a DIP offset |
| `Fixed on Screen` | absolute screen coordinates, ignoring the Codex window |

The manual mode works exactly as before:

```
Anchor = TopRight      OffsetX = -24 DIP      OffsetY = +16 DIP
```

which means *the strip's top-right corner sits 24 DIP left of the window's right edge and 16 DIP
below its top edge* — and stays there when the window is resized, maximised, restored or moved.

| anchor | | |
|---|---|---|
| `TopLeft` `TopCenter` `TopRight` | `LeftCenter` `Center` `RightCenter` | `BottomLeft` `BottomCenter` `BottomRight` |

* **The anchor is decided once**, when you release the drag, by which of the nine regions (thirds on
  each axis) the strip ended up in. It is never re-inferred afterwards.
* **The offset is written once**, at the same moment, and only ever read afterwards. Every move /
  resize / maximise / restore evaluates `windowAnchorPoint + offset`. Nothing recomputes an offset
  from an observed position, so repeated resizing cannot accumulate drift — verified over 25 resize
  cycles with the saved attachment left byte-identical.
* **Display clamping is temporary.** If the strip would leave the screen it is pulled back into view
  (keeping at least 12 DIP visible on a screen smaller than the strip), but the clamp is never
  written back, so shrinking and re-growing the window returns it to the saved position.
* **DIP, not pixels**: the same offset measures 24 DIP at 100 %, 125 %, 150 % and 200 % scaling. When
  Codex moves to a monitor with a different scale factor the strip's pixel position changes by exactly
  the scaling of the strip and the margin — and by the same amount whatever the window's width, which
  is precisely what a percentage-of-window model gets wrong.
* **Entering this mode from the docked one** seeds the attachment from wherever the strip currently
  is, so the first frame of the drag does not jump to a stale anchor.

### Positioning the strip

While docked the strip needs no adjustment at all. To move it by hand:

* **Tray icon → `Adjust Position`**, or press **`Ctrl+Alt+Shift+P`** from anywhere.
* The strip becomes draggable, takes focus, and shows a faint outline, a drag hint and a resize grip.
  Drag it (the bottom-right grip also scales it from 60 % to 130 %).
* **`Lock Position`** (tray) or `Enter` saves it; `Esc` or `取消调整` abandons the change. The hotkey
  toggles: press it again to lock.
* **`Reset Position`** returns to the default; **`Anchor`** pins it to a chosen corner outright.

### Everything else

* **Hides** when Codex is minimised, and when Codex is not the foreground window.
* **Locked means completely click-through.** `WS_EX_TRANSPARENT` plus `WS_EX_NOACTIVATE` are both set
  while locked, and `WM_NCHITTEST` answers `HTTRANSPARENT` unconditionally — so *every* pixel of the
  strip, the glyphs included, lets the mouse reach Codex. Alpha-based hit testing alone was not enough:
  it passed clicks through the transparent pixels but swallowed the ones that landed on the numbers.
  Verified by sampling 72 points across the strip in each of eight window geometries; none resolve to
  the overlay. Both flags are dropped by `BeginEditMode` and restored by `EndEditMode`, and only in
  **Adjust Position** does the strip take the mouse at all.
* **The detail panel moved to the tray.** Since the locked strip no longer receives clicks, the panel
  is opened from **tray → `详情面板 · 展开 / 收起`**. Same panel, same toggle.
* **Never steals focus**: `WS_EX_TOOLWINDOW` so it does not appear in Alt+Tab, and `WM_MOUSEACTIVATE`
  is answered with `MA_NOACTIVATE`.
* **Tray icon**: position, theme, transparency, text shadow, which fields are visible, detail panel,
  hide, quit.

### Settings file

`%LOCALAPPDATA%\CodexStatusbar\settings.json` — written on first run and after every change:

```json
{
  "settingsVersion": 3,
  "transparentBackground": true,
  "positionMode": "ComposerContextLeft",
  "contextGapDip": 10,
  "textShadow": false,
  "anchor": "TopCenter",
  "offsetX": 0,
  "offsetY": 10,
  "theme": "Auto"
}
```

It is plain text and safe to edit by hand; restart the monitor to pick up changes. `anchor` /
`offsetX` / `offsetY` are only consulted in `Manual position`, and are preserved when docking so
switching back restores exactly the old position. A version-1 file still loads. A version-2 file
whose mode was the old default (`FollowCodex`) is upgraded to docking once; an explicit
`FixedScreen` is preserved.

### Conversation switching

The strip binds to the conversation Codex Desktop reports as currently being followed, so
switching conversations in the sidebar switches all three numbers, e.g.

```
before switch:  ⚡ 231 tok/s  ·  5.7M tok  ·  Cache 98%
after  switch:  ⚡ -- tok/s   ·  821K tok  ·  Cache 94%
```

TPS goes to `--` on switch because the newly selected conversation has no in-flight turn — the
old conversation's TPS is never shown against the new conversation's tokens.

---

## 5. Debug mode (`--debug`)

Appends a block like this to `%LOCALAPPDATA%\CodexStatusbar\debug.log` whenever state changes
(this is a real capture, lightly trimmed):

```
Codex Desktop PID:   48564
Codex version:       26.928.3736.0
Current thread:      01a0f62b-580a-7173-9131-174ca788f0ea
Title:               [按智能、性能和价格比较 AI 模型 | Artificial Analysis](plugin://...) 帮我…
Active thread source:ipc
Turn:                01a0f62b-9c99-7b62-ad5b-e84f0a8551c2
Streaming:           false
TPS state:           completed
Input:               2362387
Cached:              2233984
Output:              14895
Total:               2377282
Cache hit:           94.56%
Merged model elapsed ms:75223
Turn output tokens:  14895
Model intervals:     [1790836454570,1790836455617] [1790836477938,1790836485678] [1790836492999,1790836493803] …
First item start:    2026-10-01T06:34:14.570Z
Last item end:       2026-10-01T06:37:48.162Z
Current realtime TPS:191.6
Last completed TPS:  191.6
TTFT ms:             6131
Carrier:             token_usage_record
Dedup:               46 usage record(s) ignored as duplicate
Rollout path:        C:\Users\<user>\.codex\sessions\2026\10\01\rollout-2026-10-01T14-33-50-01a0f62b-…jsonl
Rollout offset:      1512645
Baseline source:     rollout-tail
Warnings:            none
```

Field notes:

* **`Active thread source`** is `ipc` (Codex's own broadcast selected the conversation),
  `sqlite-fallback` (the recency-ordered database did), `rollout` (a newly created rollout file
  did) or `none`. It flips back to `ipc` automatically once the pipe is available again.
* **`Total`** is printed as `Input + Output`, i.e. literally the documented formula, not the
  carrier's own `total_tokens` field.
* **`Merged model elapsed ms`** is the interval *union*, and **`Model intervals`** lists the
  windows it was merged from — so a wrong TPS can be checked by hand.
* **`Carrier`** is `token_usage_record`, `token_count`, `token_usage_record+token_count` or `none`.
* **`Dedup`** names duplicates that were deliberately ignored, so a future Codex that starts
  emitting deltas is visible here instead of silently corrupting the totals.
* **`Warnings`** collects unknown record/event/item types, malformed JSON and an extended tail
  window. This is the intended tool for diagnosing a future Codex update: unknowns surface here
  instead of the numbers silently going wrong.

`--debug` also appends a separate `[position]` section whenever the geometry changes, so the
placement can be checked against the window it is following. Docked, it prints the reference element's
identity *and* the two geometry errors — because "the selector broke" and "the layout moved" need
completely different fixes, and one glance at the log tells them apart:

```
[position]
Position mode:       ComposerContextLeft
Anchor:              (none — docked to the Context control)
Offset:              (none — no absolute coordinate is stored)
Reference source:    uia-context
Reference element:
   Name:             上下文用量：13%
   AutomationId:     (none)
   ControlType:      Image
   ClassName:        icon-xs inline-flex items-center justify-center align-middle text-codex-description
Reference rect:      (1708, 1396) 25x25
Row rect:            (1738, 1385) 254x42
Composer rect:       (995, 1292) 1105x147
Left cluster rect:   (995, 1385) 190x42
Reference left edge px:1708
Row centre Y px:     1406
Gap:                 10 DIP = 15 px (target 8–12 DIP)
Responsive level:    0 (full strip)
Variant widths DIP:  195.9 / 121.3 / 112.6 / 63.1  (full -> narrowest, at 100% scale)
Width budget DIP:    334.7
Right-edge error px: 15 (reference.left - strip.right; excess over the gap = 0)
Vertical centre error px:0 (|row centre - strip centre|; target <= 2)
Left limit px:       1191
Monitor DPI:         144
Theme:               Light (from Auto -> Codex config)
Background:          transparent (text only)
Text shadow:         off
Codex rect:          (308, 244) 2048x1224
Requested strip rect:--
Actual strip rect:   (1381, 1390) 312x33
Clamped:             no
```

`Reference source` is one of `uia-context` (normal), `uia-model`, `uia-composer` or
`window-fallback` — so a Codex update that changes its accessibility tree is visible here instead of
showing up as a strip that mysteriously moved. In the manual mode the same section reports the anchor
and offset instead, where `Requested` is `anchor + offset` and `Actual` is where the window really is;
they differ only when `Clamped: yes`, and even then the saved offset is untouched. The section
deliberately has no colon in its header so tools that parse the metric block are unaffected, and it is
terminated by a blank line.

`--debug` also appends a `[lifecycle]` block whenever the state changes, and a timestamped event for
every transition, so a launch can be read back in order:

```
[22:36:55] watcher started · mode background · primary instance
[22:36:55] startup registration: enabled (first run; on by default, the tray can turn it off)
[22:38:03] waiting: no Codex detected
[22:38:47] Codex process detected PID=55088 package=OpenAI.Codex version=26.928.3736.0
[22:38:47] attaching: Codex process detected PID=55088 package=OpenAI.Codex version=26.928.3736.0 window=yes
[22:38:47] main window ready
[22:38:47] subsystems started: ipc reader, rollout reader, dock tracker=on
[22:38:48] attach attempt 1: ipc=ready session=ready uia=waiting
[22:38:48] active: attached after 1 attempt(s): ipc=ready session=ready uia=waiting

[lifecycle]
Mode:                background
Single instance:     primary
Startup registration:enabled
Startup command:     "C:\...\CodexStatusbar.exe" --background
Watcher state:       ACTIVE
Codex detected:      true (PID 55088)
Codex PID:           55088
Package:             OpenAI.Codex
Codex version:       26.928.3736.0
Codex HWND:          190E18
Attach attempt:      1
IPC:                 connected
Session:             ready
UIA:                 waiting
Overlay:             visible
Detection rule:      accepted: ChatGPT, family OpenAI.Codex_2p2nqsd0c76g0
Detections:          2 (last 10802 ms ago)
Watcher error:       none
```

`Watcher state` is the lifecycle state from §10, `Detection rule` is the identity decision that was
actually applied, and `Overlay: visible / hidden` is the strip's real state — read together they
answer "is it attached, and is it on screen" without a screenshot.

There are also several diagnostic probes that need no window:

```
CodexStatusbar.exe --ipc-probe 8      # every frame Codex sends on \\.\pipe\codex-ipc + resolved thread
CodexStatusbar.exe --self-test fixtures
CodexStatusbar.exe --position-probe out.json    # the anchor / offset model
CodexStatusbar.exe --render-probe outdir        # real layered surfaces at 96/120/144 DPI, both themes
CodexStatusbar.exe --hotkey-probe out.json      # Ctrl+Alt+Shift+P (fails by design if the strip is running)
```

---

## 6. Verifying the numbers yourself

```
CodexStatusbar.exe --self-test fixtures            # 246 checks over the production code
python tools\verify_metrics.py          # real conversations, raw vs derived
python tools\make_fixtures.py           # deterministic fixtures + assertions
python tools\verify_tail_recovery.py    # bounded-tail recovery == full parse
python tools\compare_debug_vs_raw.py    # what the monitor PRINTED vs the raw rollout
node tools\groundtruth_tps.mjs          # derived TPS vs streaming-delta ground truth
node tools\probe_codex_ipc.mjs          # dump the Codex IPC broadcast vocabulary
```

Round-2 UI probes (no Codex needed for the first three):

```
CodexStatusbar.exe --position-probe out.json      # 48 checks: anchors, resize stability, DPI, persistence
CodexStatusbar.exe --render-probe   outDir        # the real surface to PNG + the halo measurement
CodexStatusbar.exe --show-probe     outDir        # the real window, screenshotted from the screen
CodexStatusbar.exe --form-probe     out.json fixtures\form-probe-request.json
CodexStatusbar.exe --hotkey-probe   out.json
pwsh -NoProfile -File tools\verify_overlay_live.ps1   # drives the real Codex window end to end
```

`verify_overlay_live.ps1` brings Codex to the foreground, screenshots the strip, then shrinks, grows,
maximises, restores and moves the Codex window and checks the strip's *real* rectangle against
`anchor + offset` each time, restoring your window's geometry and focus when it finishes. Last run:
**22 checks, 0 failures**.

* **`--self-test`** drives the real parser (`RolloutStreamState`), the real formatter, the real
  position calculator and the real settings serialiser. It pins, among others: the conversation total
  being `input + output` and never `input + cached + output`; `Cache --` when input is 0; the
  `843 / 12.4K / 5.7M` boundaries; overlapping windows unioning to `10000` ms rather than `11000`;
  a 5000 ms `CommandExecution` not changing the result; both carriers; five double-counting
  scenarios; the nine anchors and the 3×3 region rule; 25 resize cycles leaving the saved offset
  untouched; the display clamp never being written back; and the `CodexThemeSource` TOML rules
  including against the real `config.toml` on this machine. **246 checks, currently all passing.**
* **`make_fixtures.py`** writes `fixtures\fixture-modern.jsonl` and `fixtures\fixture-legacy.jsonl`
  and asserts exact expected values. The TPS fixture is built so the answer is exact: Reasoning
  4000 ms + AgentMessage 3000 ms = 7000 ms of model output with a **5000 ms `CommandExecution`
  that must be excluded**; 1400 official output tokens over those 7000 ms **must** give exactly
  `200.0 tok/s` (including the tool time would wrongly give `116.7`). The legacy fixture contains
  **only** `event_msg/token_count`, which is what Codex ≤ 0.151 writes.
* **`verify_tail_recovery.py`** proves that reading only the last 4 MB recovers cumulative
  input/cached/output/total **identical to a full parse** (verified across all 501 session files,
  1 GB, zero mismatches).
* **`compare_debug_vs_raw.py`** is the acceptance check: it reads what the monitor *printed* and
  independently re-derives the metrics from the rollout, then compares them field by field.
* **`groundtruth_tps.mjs`** is the TPS accuracy study described in §1A.
* **`--render-probe`** renders the *same* surface the live window blits
  (`TokenStripForm.RenderSurfaceBitmap`) at 96/120/144 DPI in both themes, composites it over dark,
  light and noisy backdrops, and measures the halo of the rejected key-colour approach side by side.
* **`--show-probe`** shows the real layered window over a controlled backdrop and screenshots it, so
  the capture shows what the desktop compositor produced rather than what we hoped it would.

`docs\INVESTIGATION.md` records the full Phase-1 evidence: the environment, the real event
vocabulary of this Codex build, the arithmetic checks, the IPC capture, the ground-truth table and
every architecture option that was considered and rejected. `docs\evidence\round2-ui\` holds the
round-2 UI evidence (screenshots, probe output, the live `[position]` log) with a README explaining
each file — see §9 for the handful of files that are deliberately not published. `docs\app-server-schema\`
holds the protocol schema generated from the installed Codex itself, so the event vocabulary can be
re-checked against any future version without guessing; it is not committed either, but one command
regenerates it from *your* Codex build (see §9).

---

## 7. Known issues and limitations

1. **TPS updates at model-response boundaries, not per token.** Because Codex persists no
   streaming deltas and its app-server cannot be joined externally, there is no sub-second
   signal. During a tool-using turn the number advances every few seconds. During a *single long
   answer with no tool calls* it stays at the last measured value (shown as `~243 tok/s`) and
   only updates when that response completes. This is a limitation of what Codex exposes, not a
   smoothed-over guess — the alternative would be to invent a number, which this project refuses
   to do.
2. **The `codex-ipc` pipe is an internal, undocumented Codex interface.** It is the only way to
   learn which conversation is displayed, and OpenAI may change it. If it breaks, the strip falls
   back to `recency_at_ms` ordering and stops following switches to *idle* conversations; totals
   and cache stay correct.
3. **Multi-window is not reliably supported, and is not hacked around.** Single-window behaviour is
   correct; with two Codex windows open the following applies, from the protocol itself:
   * The IPC broadcast *does* identify the window — it carries `sourceClientId` (one per window) and
     `targetClientIds` — and the monitor keys its map by `sourceClientId + hostId`, so it tracks
     which window follows which conversation.
   * But it follows the **most recently changed** window, because that is the only ordering the
     broadcast provides. It cannot know which Codex window has OS focus.
   * Meanwhile the overlay attaches to the **foreground** Codex window's geometry. So with two
     windows the strip may sit against one window while showing the other window's conversation.
   This was verified as far as it can be without synthesising input into the user's app: a second
   window could not be opened programmatically (`SetForegroundWindow` is blocked by Windows, so the
   keystroke could not be delivered safely), and only one Codex IPC client was ever observed. No
   fragile workaround was added.
4. **Which thread is "current" is only as good as Codex's own report.** If Codex reports a
   followed conversation for a window that is not the one you are reading, the strip follows Codex,
   not the pixels.
5. **Very short turns have noisy TPS.** The model-time signal has roughly millisecond resolution, so
   a response whose whole model window is only tens of milliseconds cannot yield a meaningful rate.
   The value is still shown (it is official data), and the debug log prints the window so you can
   judge. See the accuracy table in §1A.
6. **Exe is unsigned**, so SmartScreen will warn on first run.
7. Windows-only. No sub-agent rollouts are merged into a parent conversation's totals (each
   conversation is reported on its own), and `originator == "Codex Desktop"` is required on the
   first line, so sub-agent rollouts that replay a parent session are not mistaken for it.
8. The `desktop` section of the user's `config.toml` can suppress the context-window usage display;
   that is unrelated to this strip, which reads events rather than UI.
9. **Contrast depends on the theme matching the backdrop.** With no capsule behind the text, a strip
   whose theme disagrees with what is under it is harder to read — light text over a light header, for
   instance. The shadow reduces this but cannot remove it; the honest fix is the **主题** menu (or
   `"theme"` in the settings file). `Auto` follows Codex's own setting, which is what usually makes
   them agree. The worst combination captured is in
   [`locked-transparent-light-144dpi-over-busy.png`](docs/evidence/round2-ui/locked-transparent-light-144dpi-over-busy.png).
10. **The colour of the `⚡` comes from font fallback.** Segoe UI has no U+26A1, so GDI+ substitutes
    another family. It renders correctly on this machine; if a future Windows drops that fallback the
    glyph would show as a missing-glyph box, and the fix is a font-family list for the leading glyph.
11. **`Ctrl+Alt+Shift+P` may be taken** by another application. Registration failure is silent and the
    tray menu still works; check `--hotkey-probe` if the hotkey does nothing. Note that `--hotkey-probe`
    reports failure by design while the strip is running, because the strip owns the combination.
12. **The eye-candy measurements are from one machine.** The halo comparison, the DPI table, the font
    comparison and the resize numbers were produced here (Windows 11, 150 % scaling, Codex
    26.928.3736.0, `--text-sm: 13px`). Re-run the probes to reproduce them on another setup.
13. **Docking depends on Codex's accessibility tree, which Windows/Chromium only build on demand.**
    Querying UI Automation is what makes the tree exist; the strip therefore asks for it only while
    docked, and stops entirely in the manual modes. Measured cost with the tracker running: 31 ms of
    CPU over 45 s, i.e. 0.069 % of one core — indistinguishable from the previous round. If Codex ever
    ships with accessibility disabled or renames the composer's DOM, the ladder degrades to
    `uia-composer` and then to `window-fallback`; `Reference source` in the debug log says which.
14. **Fallback levels 3 and 4 use measured DIP offsets, not live geometry.** They were taken from one
    real composer (150 %, light theme, sidebar expanded) and are bottom-right anchored, so they are a
    graceful degradation rather than an equivalent. Levels 1 and 2 need no such constants.
15. **Referencing the UI Automation API costs ~9 MB of exe.** The managed UIA assemblies live in the
    Windows Desktop "WPF" profile, so the build references that profile and then prunes the WPF
    application layer (PresentationFramework and the themes/printing/ribbon assemblies, ~40 MB
    uncompressed) from the publish. What remains — PresentationCore, WindowsBase, System.Xaml — is
    UIAutomationClient's own transitive closure. The prune is guarded: if a future SDK renames those
    files the build fails instead of silently shipping 20 MB more.
16. **The vertical reference is the toolbar row, not the Context icon.** The Context indicator's own
    box sits ~2.5 px below the row centre because of its CSS `vertical-align: middle`, so aligning the
    text baseline to the icon would put it below the model selector's. `Row rect` in the log is the
    model selector's box, and the strip centres on that; `|context icon centre − strip centre|` is
    therefore about 2.5 px while `|row centre − strip centre|` is 0.5 px.
17. **At some narrow widths the strip disappears.** That is the responsive policy working: at ~1100 px
    window width the composer's own controls leave a 61.3 DIP budget and the narrowest variant is
    63.1 DIP, so there is nothing to draw. The log prints both numbers.

---

## 8. Credits and license

Built as a modification of **codex-token-overlay** by *soleillevant0125*
(<https://github.com/soleillevant0125/codex-token-overlay>, MIT). Reused from it: the Win32
Codex-window locator and classifier, the overlay window with `WS_EX_TOOLWINDOW/NOACTIVATE`, the
window-follow loop, manual attachment/snapping, theme handling and the settings/tray shell.
Its MIT licence is kept in `LICENSE-codex-token-overlay`.

Design ideas also taken from **codex-monitor-hud** by *LH-03*
(<https://github.com/LH-03/codex-monitor-hud>, MIT): read-only access to
`state_5.sqlite`'s `threads` table, the `originator == "Codex Desktop"` root-session filter,
and the bounded-tail/incremental JSONL reading approach.

TPS methodology follows **codex-model-benchmarks** by *zakmandhro*
(<https://github.com/zakmandhro/codex-model-benchmarks>, MIT): TPS is computed from official
token counts over the model's output window, never from characters.

This project is MIT licensed (`LICENSE`). It is not developed, endorsed, or supported by OpenAI.
Codex's session-file and IPC formats are internal implementation details and may change.

---

## 9. What is not in this repository

Three things are deliberately absent, and every one of them is reproducible locally.

**1. The built binary.** `CodexStatusbar.exe` is a 61 MB self-contained single file attached to the
[GitHub Release](https://github.com/dongyue-19/codex-statusbar/releases) instead of being committed,
so the repository stays source-sized. `release\` keeps the launcher, the notes and the SHA-256.
`pwsh -File build.ps1` produces the same exe from source.

**2. Evidence captured from a live session.** The probe outputs, the synthetic-backdrop renders and
the 8× type crops *are* committed — they are what the measurements in this file and in
`docs\INVESTIGATION.md` cite. What is withheld is anything that photographed a real conversation:
the full-window and 900 px screenshots, `onscreen-over-codex*.png`, `live-over-real-codex.png`, the
raw IPC/app-server captures (`*.jsonl`) and the two `debug-live-position.log` files. Those carry
real conversation titles, real message text and local file paths; a screenshot cannot be
un-published, and none of it is needed to reproduce a single number here. Each evidence README
lists exactly which files are missing and the command that regenerates them.

**3. The protocol schema extracted from Codex.** `docs\app-server-schema\` (3.5 MB, several hundred
files) is generated from OpenAI's own binary rather than written by this project, so redistributing
it is a licensing question that does not need to be taken on. Regenerate it against your own Codex
build in one command:

```
codex app-server generate-json-schema --experimental --out docs\app-server-schema
```

Everything else — the whole of `src\`, `tools\`, `fixtures\`, `docs\INVESTIGATION.md` and the
measurement evidence — is in the tree and is what the numbers in this README were produced with.

The attributions in §8 are also kept as a standalone notice in `THIRD-PARTY-NOTICES.md`, which is
the file that carries the licence conditions; `LICENSE` is left as uninterrupted MIT text so GitHub
identifies the project correctly.

---

## 10. Lifecycle — why nothing has to be started by hand

The published program is one process with two jobs: a **watcher** that follows the Codex Desktop
process, and the **overlay**, which only exists while there is something to dock to.

```
Windows logon
   │   HKCU\...\Run  →  "<path>\CodexStatusbar.exe" --background
   ▼
WAITING_FOR_CODEX ── Codex detected ──▶ ATTACHING ── IPC/session or Context reference ──▶ ACTIVE
   ▲                                                                                      │
   └──────────────── DETACHING ◀── Codex exited ──────────────────────────────────────────┘
```

| state | what is running | tray shows |
|---|---|---|
| `WAITING_FOR_CODEX` | the process watcher and the tray icon. No IPC reconnect loop, no rollout scan, no `state_5.sqlite` query, no UI Automation, no Codex theme polling | `状态条：Waiting` |
| `ATTACHING` | the subsystems are started and their readiness probed: retries at 0 / 250 / 500 ms / 1 s / 2 s, then every 2 s | `状态条：Connecting` |
| `ACTIVE` | everything — IPC conversation routing, the rollout reader, and (in dock mode) the composer tracker | `状态条：Active` |
| `DETACHING` | teardown — overlay hidden, IPC reader stopped and its conversation forgotten, rollout reader disposed, UIA tracker off | `状态条：Detaching` |

**Codex exiting never ends the process.** The watcher goes back to `WAITING_FOR_CODEX` and waits for
the next launch, so opening Codex a second time brings the strip back on its own. Everything
Codex-specific is dropped on detach — PID, HWND, accessibility element, connected pipe, selected
conversation — so a fresh Codex process is attached exactly like the first one, never inheriting a
stale window or a stale thread id.

### Which process counts as Codex Desktop

Codex Desktop is Electron and its executable is named **`ChatGPT.exe`** — the same name the real
ChatGPT desktop app uses. So the process name is only a cheap pre-filter and the decision is made on
MSIX **package identity**:

| signal | rule |
|---|---|
| `GetPackageFamilyName(process)` | must start with **`OpenAI.Codex_`** — the authoritative test |
| `GetPackageFullName(process)` | carries the build, e.g. `OpenAI.Codex_26.928.3736.0_x64__2p2nqsd0c76g0` |
| install path (fallback only) | used when no package identity can be read at all: it must contain `\WindowsApps\OpenAI.Codex_` |

Measured on this machine: every Codex process — the Electron browser process and all eight of its
renderers — reports `OpenAI.Codex_2p2nqsd0c76g0`; the unpackaged `codex.exe` CLI reports
`APPMODEL_ERROR_NO_PACKAGE (15700)` and is rejected; a real ChatGPT Desktop would report
`OpenAI.ChatGPT-Desktop_…` and is rejected as well. The decision that was actually applied is logged
verbatim as `Detection rule:` (see §5), so a wrong attach is diagnosable instead of mysterious.

### Detection cost — why the polling here is not a busy loop

The unprivileged event-driven options were each ruled out: `Win32_ProcessStartTrace` /
`ManagementEventWatcher` require administrator rights, which this project refuses to ask for, and
there is no per-package process notification that works without them. So while Codex is *absent* the
watcher polls — in two tiers, because measuring this machine showed the obvious single-tier version
is not cheap at all:

| call | what it returns | measured here |
|---|---|---|
| `EnumProcesses` | every PID, no names | **0.10 ms** |
| `CreateToolhelp32Snapshot` | names as well | **9.62 ms** (375 processes) |

Taking the expensive one every two seconds is 4.8 ms/s — 0.5 % of a core — on a machine where Codex
is closed, and it buys nothing. So a poll reads the cheap one, and a full identity check happens only
for PIDs that **appeared** since the previous poll: a process set that gained nothing cannot contain a
Codex that just started. Looking up one new PID costs a handle open plus one `GetPackageFamilyName`
(~0.1 ms) and needs no name at all, because the package family name is the authoritative signal. The
window enumeration is only reached once something *is* accepted.

* **While Codex is absent**: ~0.1 ms every 2 s. Measured **0.03–0.10 % of one core**.
* **While Codex is present** the polling stops entirely: the watcher blocks on the process handle
  instead, so the attached state does no polling at all.
* The state that lasts longest is also the cheapest: the UI timer drops from 350 ms to **1 s** and
  does nothing but read one field and compare two tray labels.

`Detections:` in the debug block is the heartbeat for all of this. A growing age while
`WAITING_FOR_CODEX` means the watcher has stalled; a growing age while `ACTIVE` is normal, because
there is nothing to poll for.

### Start with Windows

```
Key:      HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
Value:    CodexStatusbar
Command:  "<full path>\CodexStatusbar.exe" --background
Admin:    no — HKCU only, never HKLM, never a service, never a scheduled task
```

* The path is always quoted, so an install directory containing spaces cannot be mis-parsed.
* **On by default, once.** The first launch of a build that has this feature registers it. After
  that the setting is the user's: unchecking it deletes the value and no later launch or upgrade
  re-creates it. `settings.json` keeps both `startWithWindows` (the intent) and `startupConfigured`
  (whether the one-time default has been applied), because "never configured" and "turned off" are
  otherwise indistinguishable.
* **The path self-repairs.** If the value points at a directory the exe has since moved out of, the
  next launch rewrites it (§22 in the original spec).
* The executable is a **GUI-subsystem** binary, so the logon launch shows no console window — only
  the tray icon, and the strip once Codex appears.

Three commands cover it without the tray, for scripts and troubleshooting:

```
CodexStatusbar.exe --startup-status        # key, value name, command, current exe, whether they match
CodexStatusbar.exe --install-startup       # register, and remember the choice
CodexStatusbar.exe --uninstall-startup     # unregister, and remember the choice
```

### Tray menu

| item | effect |
|---|---|
| `Status · 状态` | `Codex：运行中 · PID … · OpenAI.Codex` / `Codex：未运行`, and `状态条：Waiting / Connecting / Active / Detaching` |
| `Start with Windows · 开机自动启动` | toggles the HKCU value; the checkmark shows the registry's real state, and a write failure reverts it |
| `Start / Attach now · 立即检测并附着` | re-detects Codex immediately and restarts the attach ladder — for a Codex that was started while the watcher was asleep |
| `Restart Statusbar · 重启状态条` | relaunches the exe and exits; the new instance waits briefly for the single-instance mutex |
| `Position`, `主题`, `详情面板`, … | unchanged (§4) |
| `退出` | exits the overlay, the watcher, the tray and the process — **without** unregistering "Start with Windows", so the next logon starts it again |

The tray tooltip reads `Codex Statusbar — Waiting for Codex` while waiting and
`Codex Statusbar — Active` when attached (then switches to the conversation id and token count once
the metrics arrive). No toast notifications are ever shown.

### Single instance

A named mutex, `Local\CodexStatusbar.SingleInstance`, taken before anything else starts. A logon
launch, a double-click and `start-monitor.bat` can all happen in any order and only one of them
becomes the primary: the others exit quietly with code 0, so there is never a second overlay, a
second tray icon or a second IPC consumer. Deliberate restarts pass `--restart-wait`, which lets the
new process wait up to 15 s for the outgoing one to release the name. Probes and `--self-test` run
before the mutex and are unaffected.

### Verified

`tools\verify_lifecycle_live.ps1` drives the real Codex Desktop through close/open cycles and checks
both sides: the overlay's own `[lifecycle]` block *and* an independent Win32 window enumeration, so
"attached" has to mean the strip is really on screen. It reports how many checks passed and writes
`lifecycle-verification.json`.