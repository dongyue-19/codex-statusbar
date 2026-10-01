# CodexStatusbar — 1.0.0-rc2

Release candidate. **Feature-frozen**: no further functional change to the metrics, the token/cache
arithmetic, IPC conversation binding, the UIA `ComposerContextLeft` positioning, the responsive
strategy, typography/baseline/theme, the click-through behaviour, or the lifecycle.

A read-only, always-visible 3-metric status strip for the **official OpenAI Codex Desktop client on
Windows**. It is a separate companion process: it does not patch, replace, inject into or hook the
Codex binary. It reads data Codex already writes, and places one small overlay window next to
Codex's own composer toolbar using public Win32 APIs.

```
⚡ 158 tok/s · 2.6M tok · Cache 90%
```

---

## What is new in rc2 — it now starts and stops itself

**Normal use requires nothing at all.** Windows logon starts the watcher; Codex opening attaches it;
Codex closing hides the strip; the watcher keeps waiting for the next launch. No `start-monitor.bat`,
no PowerShell, nothing to redo after a reboot.

| state | what runs | tray |
|---|---|---|
| `WAITING_FOR_CODEX` | process watcher + tray only — no IPC loop, no rollout scan, no `state_5.sqlite`, no UI Automation | `状态条：Waiting` |
| `ATTACHING` | subsystems started, readiness probed on a 0 / 250 / 500 ms / 1 s / 2 s ladder | `状态条：Connecting` |
| `ACTIVE` | IPC routing, rollout reader, composer tracker | `状态条：Active` |
| `DETACHING` | teardown; every Codex-specific handle dropped | `状态条：Detaching` |

Codex exiting never ends the process, and a new Codex process is attached exactly like the first one
— no stale PID, HWND, accessibility element or conversation.

### Which process counts as Codex Desktop

Codex Desktop is Electron and its executable is named `ChatGPT.exe` — the same name the real ChatGPT
desktop app uses. The name is therefore only a pre-filter; the decision is made on MSIX **package
identity**:

```
GetPackageFamilyName(process) must start with "OpenAI.Codex_"      ← the authoritative test
GetPackageFullName(process)  carries the build, e.g. OpenAI.Codex_26.928.3736.0_x64__2p2nqsd0c76g0
install path                 fallback only, when no identity can be read: must contain
                             \WindowsApps\OpenAI.Codex_
```

Measured here: every Codex process (the browser process and all eight renderers) reports
`OpenAI.Codex_2p2nqsd0c76g0`; the unpackaged `codex.exe` CLI reports
`APPMODEL_ERROR_NO_PACKAGE (15700)` and is rejected; a real ChatGPT Desktop would report
`OpenAI.ChatGPT-Desktop_…` and is rejected too. The decision actually applied is printed as
`Detection rule:` in the debug log.

### Start with Windows

```
Key:      HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
Value:    CodexStatusbar
Command:  "<full path>\CodexStatusbar.exe" --background
Admin:    no — HKCU only, never HKLM, never a service, never a scheduled task
```

On by default, **once**: the first launch of a build with this feature registers it, and after that
the setting is yours — unchecking the tray item deletes the value and no later launch or upgrade
re-creates it. If the exe later moves, the next launch rewrites the path. The binary is
GUI-subsystem, so the logon launch shows no console window.

---

## 1. The three metrics — exact definitions

### TPS

```
TPS = 最近一次模型生成的 model throughput，
      基于官方 output token usage 和模型活动区间推导。
```

`1000 × Σ(official output_tokens of the last turn's model responses) ÷ |union of that turn's
Reasoning and AgentMessage item windows|`.

- The numerator is always the official `output_tokens` from Codex — never a character count, never
  `chars / 4`.
- The denominator is the time the model actually spent emitting output: tool execution, user think
  time, queue time and whole-turn wall clock are excluded by construction.
- It is an **interval union**, not a sum: `[0s,5s]` + `[4s,10s]` is 10 s, not 11 s.
- Shown as `~192 tok/s` while held across a tool call, `-- tok/s` when unmeasurable. Never estimated,
  never `0`.

### Total Token

```
Total Token = 当前 conversation 累计 input + output，
              cached input 不重复计算。
```

`total = Σ input_tokens + Σ output_tokens` — **never** `input + cached + output`, which would count
the cached prefix twice. Cumulative for the whole conversation, de-duplicated across both usage
carriers Codex writes (`token_count` and `token_usage_record`), and it follows conversation switches.

### Cache

```
Cache = 当前 conversation 累计 cached_input / input。
```

`Σ cached_input_tokens ÷ Σ input_tokens × 100%`. Rendered as `--` — never `0%`, never `NaN` — when
`input == 0` or the cached field is absent.

---

## 2. Default UI

```
⚡ 158 tok/s · 2.6M tok · Cache 90%
```

One line, always visible, no click. Defaults:

| default | value |
|---|---|
| **Dock** | inside Codex's composer toolbar, immediately left of the Context usage indicator, on the same row as the model selector, microphone and send button |
| **Auto theme** | Codex's own `--app-color-text-foreground`: light `#1a1c1f`, dark `#dfdfdf` |
| **Full click-through** | every pixel, including the glyph pixels, is transparent to the mouse; the strip never takes focus and never appears in Alt+Tab |
| **Responsive variants** | four measured widths; it steps down to a shorter form rather than overlapping the controls to its left |
| **Start with Windows** | on (see above) |

Text only — no capsule, no border — with real per-pixel transparency, so Codex's chrome shows
through. Responsive ladder: `⚡ 192 tok/s · 2.4M tok · Cache 95%` → `⚡ 192 t/s · 2.4M · 95%` →
`192 t/s · 2.4M · 95%` → `2.4M · 95%`.

**Click-through consequence:** the locked strip is fully mouse-transparent, so the detail panel is
opened from the tray (**`详情面板 · 展开 / 收起`**), not by clicking the numbers.

---

## 3. Settings

```
%LOCALAPPDATA%\CodexStatusbar\settings.json
```

Created on first run (`settingsVersion: 4`): appearance, theme, position mode, manual anchor/offset,
and `startWithWindows` / `startupConfigured`. Delete it to return to defaults. `--debug` writes to
`%LOCALAPPDATA%\CodexStatusbar\debug.log`.

---

## 4. Start

Double-click, or from a terminal:

```
start-monitor.bat
start-monitor.bat --debug
```

`start-monitor.bat` is kept for debugging, troubleshooting and a one-off manual launch — with
"Start with Windows" on, normal use never needs it. Start it **after** Codex Desktop when launching
by hand.

Requirements: Windows 10/11 x64 and Codex Desktop under the same user account; the build is
self-contained, so no .NET runtime is needed. No administrator rights, and nothing is written outside
`%LOCALAPPDATA%\CodexStatusbar\` and the single HKCU Run value.

---

## 5. Diagnostics kept in this build

| command | what it does |
|---|---|
| `--self-test [fixturesDir]` | drives the real parser, formatter, position calculator, settings serialiser, identity rule and lifecycle ladder; **246 checks**, exit 0 on success |
| `--debug` | writes the metric block, the `[position]` block, the `[lifecycle]` block and a timestamped event for every transition |
| `--background` / `--watch-codex` | the logon mode: tray only, no window |
| `--install-startup` / `--uninstall-startup` / `--startup-status` | registry registration, removal, and a status dump. No admin |
| `--no-overlay` | headless monitor, debug log only |
| `--ipc-probe <seconds>` | every frame Codex sends on `\\.\pipe\codex-ipc` and the resolved conversation |
| `--thread <id>` | pin the monitor to one conversation id |
| `--sessions <path>` | override the sessions root |
| `--position-probe out.json` | the anchor/offset model: 48 checks |
| `--render-probe <dir>` | real layered surfaces at 96/120/144 DPI, both themes, with the ink-extent check |
| `--show-probe <dir>` / `--form-probe` / `--hotkey-probe` | the real window, screenshotted / driven / hotkey registration |

Live acceptance scripts in `tools\`:

```
pwsh -File tools\verify_lifecycle_live.ps1      # close/open Codex, assert attach/detach each cycle
pwsh -File tools\verify_startup_and_resource.ps1 # registry, single instance, logon launch, idle CPU
pwsh -File tools\verify_composer_dock.ps1        # docking geometry against the live composer
```

`--debug` prints the `Reference source`, the responsive level, the variant widths and the width
budget for placement, and `Watcher state` / `Detection rule` / `Overlay` for the lifecycle — so a
Codex update that changes its accessibility tree or its package identity shows up as a log line
instead of a strip that mysteriously moved or vanished.

---

## 6. Turning it off

* **Stop it for this session:** tray → `退出`. The "Start with Windows" registration is left alone,
  so the next logon starts it again.
* **Stop it permanently:** tray → uncheck `Start with Windows`, or run
  `CodexStatusbar.exe --uninstall-startup`.
* **Remove everything:** exit it, delete `%LOCALAPPDATA%\CodexStatusbar\`, and delete the exe. The
  app is a portable single file; nothing else is installed.

---

## 7. Verify this exact build

```
CodexStatusbar.exe --self-test
```

Expected: `checks: 246   failures: 0` / `RESULT: PASS`, exit code 0.

```
Version:      1.0.0-rc2
EXE:          CodexStatusbar.exe
SHA-256:      see SHA256.txt next to this file
```

The executable is not code-signed, so SmartScreen may warn on first launch (*More info → Run
anyway*). That is expected for a release candidate.

---

## 8. Known limitations

- Fallback positioning levels (`uia-model`, `uia-composer`, `window-fallback`) use measured DIP
  constants — graceful degradation, not equivalent to the UIA Context reference.
- The `⚡` glyph depends on GDI+ font fallback.
- `Ctrl+Alt+Shift+P` may already be taken by another application; the tray menu still works.
- Process detection uses a 2 s poll while Codex is absent, because the unprivileged event-driven
  alternatives (`Win32_ProcessStartTrace`) require administrator rights. While Codex is present the
  watcher blocks on the process handle instead and polls nothing.
- If Codex is closed to the tray (its window destroyed while the process stays alive), the watcher
  correctly stays attached, but the strip is hidden because there is no foreground Codex window —
  that is the documented placement rule, not a lifecycle bug.
- The executable is unsigned.

---

## 9. License

MIT — see `LICENSE`. This project is a derivative of **codex-token-overlay** by *soleillevant0125*;
the attributions are kept in `THIRD-PARTY-NOTICES.md` and the upstream licence text in
`LICENSE-codex-token-overlay`. Not developed, endorsed or supported by OpenAI.