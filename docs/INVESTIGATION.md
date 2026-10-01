# Phase 1 — environment investigation (evidence log)

Recorded 2026-09-30 / 2026-10-01 on this machine. Everything below was observed directly, not
taken from documentation.

## 1. Environment

| item | value |
|---|---|
| OS | Windows 11 Home (中文), build 10.0.26300 |
| Shell | PowerShell 7.6.6 |
| .NET SDK | 10.0.400 (`C:\Program Files\dotnet\sdk`) |
| Node | v24.15.0 |
| Python | 3.12.10 |
| Codex Desktop package | MSIX `OpenAI.Codex 26.928.2636.0 x64`, installed under `C:\Program Files\WindowsApps\OpenAI.Codex_26.928.2636.0_x64__2p2nqsd0c76g0\app\` |
| Codex Desktop executable | `ChatGPT.exe` (Electron; the process name is **not** `Codex.exe`) |
| Electron/Chromium | 154.0.8037.57, device scale factor 1.5 |
| Codex core / CLI | `codex-cli 0.159.2` at `C:\Users\<user>\AppData\Local\OpenAI\Codex\bin\c6fe824d725f02d7\codex.exe` |
| second packaged CLI | `C:\Users\<user>\.codex\plugins\.plugin-appserver\codex.exe` (also 0.159.2, "complete local package") |
| Codex home | `C:\Users\<user>\.codex` |
| session files | `~/.codex/sessions/YYYY/MM/DD/rollout-<iso-ts>-<threadId>.jsonl`, 501 files, largest 13 MB here (another machine-side session reached 70 MB) |

### Process layout

```
ChatGPT.exe (PID 63152, Electron main, window class Chrome_WidgetWin_1, title "ChatGPT")
└─ codex.exe … app-server --analytics-default-enabled …          ← stdio only, private
   ├─ node.exe  @oai/cua-repl
   └─ node.exe  D:\doubao-search-mcp\src\server.js               ← MCP servers
```

The Codex Desktop **app-server** is spawned by the Electron main process and talks over
**stdio**. `Get-NetTCPConnection` shows **no listening socket** for any Codex process, and
`~/.codex/app-server-control/` is empty — so `app-server-control.sock` does not exist on this
install and the running app-server cannot be joined from outside.

Named pipes that do exist and are connectable:

| pipe | what it is |
|---|---|
| `\\.\pipe\codex-ipc` | Codex Desktop's **local broadcast channel** — see §4 |
| `\\.\pipe\codex-browser-use-*` | per-session browser-use transport |
| `\\.\pipe\codex-computer-use-*` | computer-use transport |
| `\\.\pipe\OpenAI.CodexSandbox.OpenAI.Codex_2p2nqsd0c76g0` | Windows sandbox service |

## 2. What actually exists in the rollout JSONL

Across the 80 newest session files (≈12 k top-level records):

**Top-level `type`:** `event_msg` (11,634) · `response_item` (11,579) ·
`token_usage_record` (2,657) · `turn_context` (247) · `world_state` (144) ·
`session_meta` (80) · `compacted` (15) · `inter_agent_communication_metadata` (6)

**`event_msg.payload.type`:** `item_completed` (7,835) · `token_count` (3,059) ·
`thread_settings_applied` (266) · `task_started` (237) · `task_complete` (229) ·
`turn_aborted` (8) · `agent_message` (6)

**`response_item.payload.type`:** `reasoning` · `message` · `function_call` ·
`function_call_output` · `custom_tool_call` · `custom_tool_call_output` · `web_search_call`

### The decisive finding

**There are no streaming delta records.** No `item_updated`, no `agent_message_delta`, no
`item_started`. Items appear once, complete, with `started_at_ms` / `completed_at_ms`. So a
file-watching monitor gets per-model-response granularity, never per-token.

### Verified record shapes

```jsonc
// token_usage_record — the authoritative cumulative numbers
{"timestamp":"2026-09-30T15:17:19.260Z","ordinal":205,"type":"token_usage_record",
 "payload":{"thread_id":"…","turn_id":"…","session_id":"…","root_turn_id":"…",
   "response_id":"resp_…",
   "usage":{"input_tokens":120471,"cached_input_tokens":120192,"cache_write_input_tokens":0,
            "output_tokens":915,"reasoning_output_tokens":164,"total_tokens":121386},
   "turn_token_usage":{…},"thread_token_usage":{…}}}   // thread_* = cumulative for the thread

// item_completed — the model-output timing window
{"type":"event_msg","payload":{"type":"item_completed","thread_id":"…","turn_id":"…",
  "item":{"type":"AgentMessage","id":"msg_…","content":[{"type":"Text","text":"…"}],"phase":"commentary"},
  "started_at_ms":1790780897069,"completed_at_ms":1790780897069}}

// task_complete — official TTFT + turn wall clock
{"type":"event_msg","payload":{"type":"task_complete","turn_id":"…",
  "duration_ms":225756,"time_to_first_token_ms":4341,"last_agent_message":"…"}}

// token_count — same numbers, alternative carrier
{"type":"event_msg","payload":{"type":"token_count","info":{
  "total_token_usage":{…},"last_token_usage":{…},"model_context_window":950000},
  "rate_limits":{…}}}
```

## 3. Arithmetic verified against raw data

Example conversation `01a0f2db-ca6a-7dc0-9d70-418385f344fe` (source: `tools/verify_metrics.py`):

```
thread_token_usage.input_tokens            = 2,126,561
thread_token_usage.cached_input_tokens     = 2,045,696
thread_token_usage.output_tokens           = 23,166
thread_token_usage.total_tokens            = 2,149,727

input + output                             = 2,149,727   == official total_tokens  ✔
input + cached + output                    = 4,195,423   ✘ wrong (cache double-counted)
cached / input                              = 96.1974 %  -> display 96%
```

`state_5.sqlite` → `threads.tokens_used` for the same thread = `2,149,727`. The database column
and the rollout agree exactly. (`threads.tokens_used` lags — it updates at turn end — so it is
used only for restart recovery and as a fallback.)

## 4. Active-conversation detection — verified

`~/.codex/state_5.sqlite` has **no** "currently selected thread" column. `threads` carries
`recency_at_ms` (view ordering), which is a good approximation but cannot distinguish
"currently displayed" from "recently viewed". The UI's own persistence
(`.codex-global-state.json`) has sidebar/read/pin state but no selected-thread key either.

The reliable signal is the Codex Desktop IPC pipe. Live capture (`tools/probe_codex_ipc.mjs`):

```
[ipc] connected to \\.\pipe\codex-ipc
[ipc] sent initialize: {"type":"request","requestId":"…","sourceClientId":"codex-statusbar-probe",
                        "version":0,"method":"initialize","params":{"clientType":"…"}}
[ipc] << response  initialize  params={}
[ipc] << broadcast thread-stream-following-changed
        params={"conversationId":"01a0f2db-ca6a-7dc0-9d70-418385f344fe","hostId":"local","following":true}
```

* Framing: **4-byte little-endian length prefix + UTF-8 JSON**.
* Codex pushes the currently followed conversation **immediately on connect**, then again on
  every conversation switch.
* The reported `conversationId` **matched** the thread at the top of `threads ORDER BY
  recency_at_ms DESC` — the two independent signals agree.
* No app-server notifications appeared on this channel while Codex was idle; it is the
  *client-facing* channel, so it is used here for conversation identity, not for token events.

## 5. Ground-truth TPS measurement

`tools/probe_appserver.mjs` spawns its **own** `codex app-server` on stdio (never touching the
desktop's), runs one ephemeral thread with `experimentalRawEvents: true`, and times the real
delta stream:

| measurement | value |
|---|---|
| `item/agentMessage/delta` events | 480 |
| delta characters | 5,768 |
| TTFT (start → first agent delta) | 25,897 ms |
| first→last delta window | 12,856 ms |
| official `outputTokens` / `reasoningOutputTokens` | 2,782 / 1,610 |
| **true delivered TPS (visible)** | **91.2 tok/s** |
| **true delivered TPS (all output tokens / delta window)** | **216.4 tok/s** |
| `turn.durationMs` | 35,975 ms |

Notification vocabulary observed on the live stream (this version): `thread/started`,
`thread/status/changed`, `thread/settings/updated`, `turn/started`, `turn/completed`,
`item/started`, `item/completed`, `item/agentMessage/delta`, `item/reasoning/textDelta`,
`thread/tokenUsage/updated`, `rawResponse/completed`, `rawResponseItem/completed`,
`account/updated`, `account/rateLimits/updated`, `configWarning`, `warning`,
`mcpServer/startupStatus/updated`, `remoteControl/status/changed`.

`rawResponse/completed` carries `usage` in camelCase:
`{totalTokens, inputTokens, cachedInputTokens, cacheWriteInputTokens, outputTokens,
reasoningOutputTokens}`. `thread/tokenUsage/updated` carries
`tokenUsage: {total, last, modelContextWindow}` in the same shape.

**Conclusion used by this project:** 216.4 tok/s is what a live delta measurement yields; the
rollout-derived formula developed for the monitor yields **214.9 tok/s** on the best comparable
turn of the same model — a 0.7% agreement. That is why the monitor's TPS is
`official output_tokens / (Reasoning + AgentMessage item ms)` and is labelled exact rather than
estimated.

## 6. Options considered and rejected

| option | verdict |
|---|---|
| Attach to the desktop's app-server | **impossible** — stdio-bound to Electron, no socket/pipe exposed |
| MITM the provider HTTPS traffic | rejected: explicitly out of scope, would see all prompt content |
| Inject a DLL / hook Electron / read the renderer DOM | rejected: fragile across updates, invasive, violates the brief |
| Read streaming text via UI Automation | rejected: fragile, heavy CPU, effectively a DOM hack |
| Poll `~/.codex` trees on a timer | rejected: 500 session files, one at 70 MB |
| **Read-only `codex-ipc` for conversation identity + bounded-tail rollout parse for tokens** | **chosen** — non-invasive, survives Codex updates, O(new bytes) per update |
| `codex app-server generate-json-schema` to re-check the vocabulary after an update | adopted as a maintenance tool (`docs/app-server-schema/`) |

## 7. Repository evaluation summary

| | codex-monitor-hud (LH-03) | codex-token-overlay (soleillevant0125) |
|---|---|---|
| stars / commits / last commit | 23 / 35 / 2026-09-23 | 4 / 9 / 2026-08-13 |
| stack | C# .NET 10 WPF (+ legacy PowerShell) | C# .NET 10 WinForms (+ macOS Swift) |
| follows the Codex window | no — free-floating screen-anchored HUD | **yes** — Win32 poll, no hooks |
| knows the current conversation | no — "recently written" heuristics | **yes** — `codex-ipc` broadcast |
| has TPS | no | no |
| license | MIT | MIT |

`codex-token-overlay` was chosen as the base because it already solves the two hardest
non-invasive problems (window attachment and active-conversation identity) and already
computes total / cache-hit from the right official fields. It was then extended with the TPS
engine, the three-metric strip, the conversation-switch reset semantics, restart recovery and
debug mode. `codex-monitor-hud` contributed the `state_5.sqlite` read-only fallback and the
`originator == "Codex Desktop"` root-session filter.

## 8. Implementation-phase findings (all found by cross-checking against raw data)

These were discovered *after* the first working build, by comparing the monitor's own output with
the raw rollout — which is exactly why `tools/compare_debug_vs_raw.py` exists.

### 8.1 `turn_token_usage` is not a turn total on this build

On Codex **26.928.3736.0** (core 0.159.2) `turn_token_usage` is a **byte-identical copy of
`thread_token_usage`** for every one of the 23 records examined — i.e. the whole conversation's
counters, not the turn's. Using it as the per-turn numerator put the entire conversation's output
into a single turn. It is now only trusted when it genuinely differs from `thread_token_usage`.

### 8.2 Every response is reported twice, with identical values

For one response, `token_usage_record.usage` and `token_count.info.last_token_usage` are
identical field by field (verified across all 23 pairs in a live session and again in the 14-turn
study). A de-duplication key based on the *carrier* therefore counted every response **exactly
twice** (measured: numerator 29790 vs the true 14895), which doubled the reported TPS. The key is
now derived from the values (`input:cached:output:total`), so whichever carrier arrives first wins.

### 8.3 Item type casing differs between the two surfaces

The app-server protocol names `ThreadItem` types in **camelCase** (`reasoning`, `agentMessage`)
while the rollout JSONL persists them in **PascalCase** (`Reasoning`, `AgentMessage`). A tool that
reads one surface and filters on the other's casing silently collects no model windows at all —
this is what made the first ground-truth run report an empty denominator for all 14 turns.

### 8.4 Ground-truth TPS accuracy (14 turns, `tools/groundtruth_tps.mjs`)

| turn | output tok | reasoning tok | model window | derived | delta ground truth | error |
|---|---|---|---|---|---|---|
| short-sentence | 211 | 168 | 1998 ms | 105.6 | 105.7 | 0.1 % |
| medium-prose | 337 | 139 | 3017 ms | 111.7 | 111.8 | 0.1 % |
| long-prose | 1604 | 913 | 16822 ms | 95.4 | 95.4 | 0.1 % |
| reasoning-arithmetic | 111 | 49 | 515 ms | 215.5 | 215.1 | 0.2 % |
| reasoning-puzzle | 805 | 597 | 7967 ms | 101.0 | 101.1 | 0.0 % |
| code-block | 907 | 538 | 5085 ms | 178.4 | 178.4 | 0.0 % |
| bullets | 637 | 307 | 5530 ms | 115.2 | 115.2 | 0.0 % |
| chinese-long | 1007 | 523 | 9789 ms | 102.9 | 102.9 | 0.0 % |
| mixed-language | 905 | 526 | 8015 ms | 112.9 | 112.9 | 0.0 % |
| tabular | 1238 | 691 | 9526 ms | 130.0 | 131.2 | 0.9 % |
| reasoning-many-steps | 690 | 186 | 4347 ms | 158.7 | 158.7 | 0.0 % |
| very-long | 5070 | 3779 | 53353 ms | 95.0 | 95.0 | 0.0 % |
| strict-format | 57 | 43 | 18 ms | 3166.7 | 3800 | 16.7 % |

**median 0.04 %, P95 16.7 %, max 16.7 %**; 11 of 13 within 0.2 %. The lone outlier is an 18 ms
window on a 57-token answer, where the timing resolution itself is the limit — not the formula.
`reasoning_output_tokens <= output_tokens` held in all 14 turns, confirming that
`output_tokens` **includes** the hidden reasoning tokens.

An independent whole-conversation check on the user's live session agreed exactly:
merged model window `75223 ms` (monitor) vs `75223 ms` (re-derived), and TPS `191.6`
(EMA-smoothed) vs `198.0` (raw), with input/cached/output/total matching to the unit.

### 8.5 Live state-machine trace (real generation, 110 s capture)

```
#   state      streaming  TPS    mergedMs  turnOut
0   waiting    true       --     0         0        <- new turn, no model output yet
1   waiting    true       --     0         0
2   waiting    true       --     1316      0        <- model window accumulating
3   completed  false      106.1  12136     1288     <- 1288 / 12.136 s = 106.1 tok/s
```

This confirms requirement D (`-- tok/s` during a new turn with no output), the union growing
during generation, and the exact final value on completion.

### 8.6 IPC behaviour observed across a Codex update

Codex Desktop was updated mid-investigation (package `26.928.2636.0` → `26.928.3736.0`). The
`codex-ipc` pipe and the `thread-stream-following-changed` broadcast **survived unchanged**
(re-verified live after the update, resolving the correct `conversationId`). During the restart
window the monitor reported `active-thread-source = sqlite-fallback` with still-correct totals,
then flipped back to `ipc` on its own — which is exactly the reconnect behaviour, observed rather
than assumed.

### 8.7 CLI sessions are not Desktop sessions

A `codex exec` rollout carries a different first-line `originator`, so the `originator == "Codex
Desktop"` filter skips it. That is correct for the strip (it should not jump to a CLI run), but it
silently defeated the first `--thread` live-trace attempt. An **explicit `--thread` pin is now
trusted regardless of originator**; automatic selection still requires a Desktop root session.

### 8.8 Two more selection bugs found the same way

* `update while hidden`: `SelectPreferredRootSession` early-returned when the thread already
  matched, without updating the source label, so if the very first poll beat the asynchronous IPC
  connect (the common case) the debug log claimed `sqlite-fallback` forever.
* `--sessions` only overrode the sessions root, not the state database, so a test home leaked the
  real profile's most-recent conversation. The database path now follows the sessions root when a
  sibling `state_5.sqlite` exists.

---

# Phase 2 — overlay UI and positioning (evidence log)

Scope: transparency, readability, drag-to-position, anchor + offset, resize stability. The metric
data layer (TPS / token / cache) was **not** touched; to prove that, `compare_debug_vs_raw.py` and
the 81 metric assertions from Phase 1 still pass unchanged (the self-test grew to 150 by *adding* a
position/theme suite).

## 9. Choosing the transparency technique

`TransparencyKey` is the obvious approach and it is the wrong one. It draws the glyphs against a
magic colour and punches that colour out, so every anti-aliased edge keeps a tint of it. Measured on
this machine with the same string, one line, both ways:

| technique | halo pixels | max channel spread |
|---|---|---|
| `TransparencyKey` + GDI `TextRenderer` + `MakeTransparent` | **1746** | **171** |
| `WS_EX_LAYERED` + 32bpp premultiplied ARGB + `UpdateLayeredWindow` | — | **10** (5 with the bolt removed) |

"Channel spread" is `max(R,G,B) - min(R,G,B)` on a partially covered pixel: a genuinely neutral glyph
edge keeps `R == G == B`, so spread measures *introduced colour*. The layered figure's 5 is the
palette's own `(245,245,247)` — already 2 apart — plus un-premultiplication rounding. Evidence:
`docs/evidence/round2-ui/rejected-transparencykey-halo-over-busy.png` shows the surviving fringe.

Text therefore had to move from `TextRenderer` (GDI/ClearType) to GDI+ `DrawString` with
`TextRenderingHint.AntiAlias`. ClearType blends each sub-pixel channel independently against what is
behind the glyph; behind a transparent surface there is nothing, so the fringe *is* the technique.
Grayscale AA carries a single coverage value per pixel, which is exactly what an alpha surface can
represent.

Two consequences were then checked rather than assumed:

* **`⚡` still renders.** Segoe UI has no U+26A1 and GDI+ font fallback supplies it (shot with the
  bolt in place: `locked-transparent-dark-144dpi.png`). Recorded as a limitation anyway, since it
  depends on a fallback family.
* **DPI.** Fonts are specified in points, and GDI+ resolves points against the `Graphics` DPI, so the
  offscreen bitmap must carry the host DPI via `SetResolution` — without it, 150 % scaling silently
  renders 100 %-sized text. The `--render-probe` captures at 96/120/144 DPI to catch exactly that.

## 10. The position model, and why not percentages

`Σ` percentage-of-window positioning drifts under resize by construction. The model adopted is
**anchor + DIP offset**, where the anchor is one of nine points and the offset is

```
stripAnchorPoint - windowAnchorPoint        (in DIP)
```

captured **once**, when the drag is released, and thereafter only read. Every move / resize /
maximise / restore evaluates `windowAnchorPoint + offset`. Nothing ever recomputes an offset from an
observed position, which is the only way cumulative drift can be structurally impossible rather than
merely unobserved.

The anchor is chosen at the same moment, from the 3×3 region (thirds on each axis) the strip was
dropped in, and never re-inferred — so a resize cannot silently re-anchor the strip.

Display clamping is separated from the saved position: a strip that would leave the screen is pulled
back into view (keeping ≥ 12 DIP visible when the screen is smaller than the strip), and that
adjusted position is never written back. Without that separation, shrinking the window and growing it
again would lose the user's position permanently.

Measured (`--position-probe`, 48 checks, all passing): a drop in the top-right region captures
`TopRight, (-24, +16) DIP`; three window sizes resolve within 0 px of `anchor + offset`; 25 resize
cycles leave the attachment byte-identical and the original window size reproduces the original
position exactly; the clamp leaves the attachment untouched; and the same offset measures 24 DIP at
96/120/144/192 DPI. Crossing 100 % → 150 % moves the strip by exactly the scaling of the strip plus
the margin (−177 px), and by the *same* −177 px whether the window is 1400 or 2600 wide — the
percentage model would give different answers for those two.

## 11. Theme: Windows and Codex disagree

The Windows app theme is `AppsUseLightTheme = 0` (dark) on this machine while Codex Desktop is set to
`light`, so a Windows-only `auto` painted white glyphs onto Codex's light chrome — visible in the
first live capture of this round. Codex's own setting is `appearanceTheme` in
`%USERPROFILE%\.codex\config.toml`.

Two TOML details cost a debugging cycle each:

* the key lives under `[desktop]`, not the root table, and *after* a dozen other sections — so
  "stop reading at the first `[`" misses it entirely;
* keys belong to the table header that precedes them, so the parser has to track the current table
  and accept only the root and `[desktop]` scopes (not `[desktop.appearance…]`, which is a
  sub-theme).

The file also contains credentials, so only that one key is parsed; nothing else is read, retained or
logged. `auto` now resolves through Codex first and falls back to Windows, and the tray menu shows
what it resolved to. Both cases are pinned by self-tests, including one that parses the real
`config.toml`.

## 12. The bug the offscreen probes could not catch

Every offscreen check passed — the ARGB surface was byte-correct at three DPIs in both themes, and
`GetWindowRect` agreed with the position the application reported — while **nothing was drawn on
screen at all**. The failure mode was precise: `UpdateLayeredWindow` had never been called, and a
layered window with no surface renders nothing *and* is invisible to `WindowFromPoint`.

Two lessons, both now enforced in code:

* a layered window's content must be blitted from the state changes themselves
  (`ApplyLayout` / `SetPresentation` / `ApplyTheme` / after `Show()`), not left to `WM_PAINT`;
* "the window exists and is visible and has the right rectangle" is not evidence that it is painting
  or that it can be clicked. The form probe now measures `WindowFromPoint` **inside** the strip
  (must return the strip) and **outside** its region but inside its rectangle (must return the window
  underneath) — that is what proves click-through.

A third, smaller one: the theme binding applies the palette during `ApplicationContext`
construction, before the tray menu exists, so the theme callback dereferenced null menu items and the
process died with `NullReferenceException` on startup. The self-test could not catch that; running the
real binary did, immediately.

## 13. Live verification of the running application

`tools\verify_overlay_live.ps1` drives the real Codex window with `AttachThreadInput` +
`SetForegroundWindow`, then shrinks, grows, maximises, restores and moves it, reading the geometry the
application itself reports. Two things were needed to make the measurements trustworthy:

* the script must set **per-monitor DPI awareness before reading any geometry**, otherwise
  `GetWindowRect` and `CopyFromScreen` work in virtualised 96-DPI units and every comparison is off
  by the scale factor — which is exactly how the first version produced a screenshot of the wrong
  region;
* it must identify the Codex window by DWM *extended frame bounds* (what the application compares
  against), not `GetWindowRect` (which includes the invisible resize border).

Result — 22 checks, 0 failures:

```
before          Codex (321,153) 1920x1224  TopCenter (0,10)  requested (1034,168)  actual (1034,168)
after shrink    Codex (321,153)  738x900   TopCenter (0,10)  requested  (443,168)  actual  (443,168)
grown back      Codex (321,153) 1920x1224  TopCenter (0,10)  requested (1034,168)  actual (1034,168)
maximized       Codex (-11,-11) 2582x1550  TopCenter (0,10)  requested (1033,  4)  actual (1033,  4)
restored        Codex (321,153) 1920x1224  TopCenter (0,10)  requested (1034,168)  actual (1034,168)
moved +240,+135                            strip delta (240,135) == Codex delta (240,135)
```

The window styles were read from the live process: `ex = 0x08090088` =
`WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE`, `WM_MOUSEACTIVATE` answered `3`
(`MA_NOACTIVATE`) while locked and `1` while positioning, restored to `3` afterwards.

## 14. Cost

Measured while Codex was in the foreground, i.e. with the strip visible and re-laying out every
350 ms: **15.6 ms of CPU over 22 s = 0.071 % of one core**, independently corroborated by
`Get-Counter` at **0.077 %**. Idle with the strip hidden: below the sampling floor. Roughly 88 % of
the strip's rectangle is fully transparent; only ~13 % of its pixels differ from the backdrop.
---

# Phase 3 — docking the strip inside Codex's composer toolbar

## 15. What the accessibility tree actually exposes

Chromium only materialises its accessibility tree when a UIA client asks for it, so the first walk of
the Codex window came back nearly empty and the second was complete. That asymmetry is the whole
reason the investigation started by *looking* rather than by writing a selector.

`tools/probe_codex_uia.ps1` dumps the bottom band of the tree; `tools/probe_codex_composer.ps1`
narrows to the composer. The relevant rows, verbatim:

```
Depth ControlType AutomationId    X    Y    W    H Name
   11 Group                      995 1292 1105  147
   12 Edit                      1013 1313 1069   66 随心输入            (ProseMirror)
   12 Button  radix-_r_bg_      1057 1385  128   42 更改权限             (ExpandCollapse)
   12 Image                     1708 1396   25   25 上下文用量：13%      <-- the reference
   12 Button  radix-_r_125_     1738 1385  254   42 deepseek-v4.1-flash 高 (ExpandCollapse)
   12 Button                    1991 1385   43   42 听写
   12 Button                    2045 1385   43   42 发送
```

Three things settled the design:

1. **The Context indicator is a direct child of the composer card** (its `ControlView` index 3), and
   its class string carries the design-system token `text-codex-description`.
2. **Nothing has a usable AutomationId.** `radix-_r_bg_` / `radix-_r_125_` are Radix-generated and
   change between renders, so an id-based selector would be a time bomb. The stable signals are the
   *structure* (the composer is the ControlView parent of the `ProseMirror` editor), the *control
   type*, the *sibling order*, and — for the model selector — the **ExpandCollapse** pattern, which
   distinguishes "opens a menu" from ordinary buttons without depending on any name.
3. **The model name is unusable as a selector** for the reason the user predicted, and the Context
   indicator's label contains a number that changes. The label is only used as a tie-breaker among
   `Image` children; with one Image child the structure alone suffices.

## 16. Does the reference survive a relayout?

`tools/probe_uia_stability.ps1` holds an `AutomationElement` and also re-walks the tree after each
geometry change. Result:

| step | cached Context | fresh Context | cached Model | fresh Model |
|---|---|---|---|---|
| baseline 2048×1224 | 1708,1396 | 1708,1396 | 1738,1385 | 1738,1385 |
| resize 1100 | 987,1396 | 987,1396 | 1017,1385 | 1017,1385 |
| resize 900 | 786,1396 | 786,1396 | **stale** | 816,1385 |
| resize 1700 | 1534,1396 | 1534,1396 | **stale** | 1564,1385 |
| maximized | 1656,1456 | 1656,1456 | **stale** | 1686,1445 |
| moved +120,+60 | 1828,1456 | 1828,1456 | **stale** | 1858,1445 |

So: **the Context indicator's element survives everything** and can be held and re-read cheaply
(one cross-process property read per tick), while the model selector's node is replaced by React on
the first relayout and must be re-discovered. That is exactly why the Context indicator is level 1 and
the model selector only a fallback — and why the tracker holds one reference and re-walks on
staleness rather than walking every tick.

`model.Left − context.Left` was 30 physical px (20 DIP) in **every** row, which is what makes level 2
possible at all.

## 17. The typography, from Codex's own stylesheet

The composer's class strings leak the design system, and `app.asar` (540 MB) can be indexed without
scanning it — `tools/asar_tool.py` reads the archive's JSON header and extracts the few CSS bundles
that matter. The decisive tokens:

```css
--font-sans-default: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
[data-codex-window-type=electron] { --text-sm: 13px; --font-weight-medium: 500; }
--app-color-text-foreground: light #1a1c1f, dark var(--gray-fixed-150);
--color-text: var(--app-color-text-foreground);
```

* Only `"Segoe UI"` resolves on Windows, so the family is not a guess.
* `--text-sm` is globally `.875rem` (14 px) but **overridden to 13px for the Electron window type**,
  which this application is.
* `#1a1c1f` is `rgb(26,28,31)` — and the darkest pixel measured in Codex's own body text on screen is
  `rgb(26,28,31)`, exactly. The light-theme text colour is therefore not an approximation.

`--text-sm` was confirmed on screen twice: four full-width CJK glyphs (`完全访问`) occupy a 78 px box,
i.e. a 19.5 px em = 13 DIP at 150 %; and `tools/compare_ui_font.py` rendered the model label in each
candidate, with Segoe UI Regular at 13 DIP giving 170 px of ink against 172 px measured (the residual
2 px is Blink's per-glyph advance rounding).

## 18. Three bugs only the live window could find

1. **The strip rendered nothing at first.** A layered window's content exists only after
   `UpdateLayeredWindow`; the round-2 code drove it from the paint cycle. Fixed there, but this round
   repeated the lesson in a smaller way: the *first* docked launch drew "Cache 9…".
2. **Text clipped by 12 px.** The window is sized from the measured advance plus 6 DIP each side, but
   the text was drawn into a rectangle inset by the 10 DIP `HorizontalPadding` the metric layouts use.
   282 px of drawable area against 294 px of text. The render probe now measures the ink's horizontal
   extent and asserts it does not reach the edge, so the two numbers cannot drift apart again — it
   caught nothing before the fix and reports 6–11 px of slack after it.
3. **`⚡113`.** `StringFormat.GenericTypographic` — used everywhere else precisely so no padding is
   added around glyphs — reports a whitespace-only string as **zero** wide, so the space after the
   lightning glyph vanished. `StringFormatFlags.MeasureTrailingSpaces` on that one measurement fixes
   it.

## 19. The reference is 2.5 px off-centre, and that matters

The Context indicator's box is `(1708,1396) 25x25` inside a `(1738,1385) 254x42` toolbar row, i.e.
its centre is at y=1408.5 while the row's is 1406. The cause is its own
`vertical-align: middle`. Aligning the strip's text to the icon's centre would put the baseline 2.5 px
below the model selector's, so the vertical reference is the **row box** (the model selector's
rectangle) and the debug log reports both errors separately.

Measured on the finished strip and on the native label in the same screenshot: baseline **1 px** apart.

## 20. Cost of asking for the tree

UI Automation makes Codex build its accessibility tree, which is not free for Codex. Two mitigations
were chosen rather than assumed away: the tracker runs **only** in the docked mode (the manual modes
stop it), and all UIA work happens on one background thread that publishes an immutable snapshot, so
a slow or wedged Codex cannot stall the strip.

Measured: **31.2 ms of CPU over 45 s = 0.069 % of one core**, identical to the previous round — the
automation is not measurable in the overlay's own cost, and the strip's render is smaller than before
(312×33 versus 495×51).
