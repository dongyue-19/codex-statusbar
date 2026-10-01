// Ground-truth probe: measure true delivered TPS from the real app-server event
// stream (item/agentMessage/delta) and record the exact notification vocabulary
// this Codex version emits. Read-only experiment: it starts its own app-server on
// stdio and runs one ephemeral thread, so it never touches the desktop session.
//
// Usage: node tools/probe_appserver.mjs [--prompt "..."] [--seconds N]

import { spawn } from "node:child_process";
import readline from "node:readline";
import path from "node:path";
import os from "node:os";
import fs from "node:fs";

const CODEX_BIN =
  process.env.CODEX_BIN ||
  path.join(os.homedir(), ".codex", "plugins", ".plugin-appserver", "codex.exe");

const argv = process.argv.slice(2);
const arg = (name, dflt) => {
  const i = argv.indexOf(name);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : dflt;
};
const PROMPT = arg(
  "--prompt",
  "Write a single continuous technical essay of about 900 words explaining how HTTP request pipelining differs from HTTP/2 multiplexing, including a short history of each. Do not call any tools. Output prose only."
);
const CWD = arg("--cwd", process.cwd());
const MODEL = arg("--model", "deepseek-v4.1-flash");
const LOG = arg("--log", path.join(CWD, "docs", "probe-appserver-events.jsonl"));

const logFh = fs.createWriteStream(LOG, { flags: "a" });
const note = (o) => logFh.write(JSON.stringify(o) + "\n");

const child = spawn(CODEX_BIN, ["app-server"], {
  stdio: ["pipe", "pipe", "inherit"],
  env: process.env,
});

let nextId = 1;
const pending = new Map();
const send = (msg) => child.stdin.write(JSON.stringify(msg) + "\n");
const request = (method, params) =>
  new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject, method });
    send({ id, method, params });
  });

const t0 = Date.now();
const stamp = () => Date.now() - t0;

// ---- TPS state -------------------------------------------------------------
const run = {
  deltas: 0,
  chars: 0,
  firstDeltaAt: null,
  lastDeltaAt: null,
  reasonDeltas: 0,
  reasonChars: 0,
  usage: null,
  tokenUsage: null,
  completedAt: null,
};

const vocab = new Map();

const rl = readline.createInterface({ input: child.stdout });
rl.on("line", (line) => {
  let m;
  try {
    m = JSON.parse(line);
  } catch {
    return;
  }
  note({ t: stamp(), dir: "recv", msg: m });

  if (m.method) {
    vocab.set(m.method, (vocab.get(m.method) || 0) + 1);
    const p = m.params || {};
    switch (m.method) {
      case "item/agentMessage/delta": {
        run.deltas++;
        run.chars += (p.delta || "").length;
        if (run.firstDeltaAt === null) run.firstDeltaAt = Date.now() - t0;
        run.lastDeltaAt = Date.now() - t0;
        break;
      }
      case "item/reasoning/textDelta":
      case "item/reasoning/summaryTextDelta": {
        run.reasonDeltas++;
        run.reasonChars += (p.delta || "").length;
        break;
      }
      case "thread/tokenUsage/updated": {
        run.tokenUsage = p.tokenUsage;
        break;
      }
      case "rawResponse/completed": {
        run.usage = p.usage || p;
        break;
      }
      case "turn/completed": {
        run.completedAt = Date.now() - t0;
        run.turn = p.turn;
        finish();
        break;
      }
      case "error":
        console.error("[app-server error]", JSON.stringify(p));
        break;
      default:
        break;
    }
    return;
  }
  if (m.id !== undefined) {
    const slot = pending.get(m.id);
    if (slot) {
      pending.delete(m.id);
      if (m.error) slot.reject(new Error(`${slot.method}: ${JSON.stringify(m.error)}`));
      else slot.resolve(m.result);
    }
  }
});

function finish() {
  const ms = (a, b) => (a !== null && b !== null ? b - a : null);
  const streamMs = ms(run.firstDeltaAt, run.lastDeltaAt);
  const u = run.usage || (run.tokenUsage && (run.tokenUsage.last || run.tokenUsage)) || {};
  const pick = (...names) => {
    for (const n of names) if (u && u[n] != null) return u[n];
    return null;
  };
  const outTok = pick("outputTokens", "output_tokens");
  const reasTok = pick("reasoningOutputTokens", "reasoning_output_tokens");
  const inTok = pick("inputTokens", "input_tokens");
  const cacheTok = pick("cachedInputTokens", "cached_input_tokens");
  const visible = outTok != null && reasTok != null ? Math.max(0, outTok - reasTok) : null;

  const report = {
    codexBin: CODEX_BIN,
    model: MODEL,
    ttftMs: run.firstDeltaAt,
    lastDeltaMs: run.lastDeltaAt,
    totalMs: run.completedAt,
    streamMs,
    deltaCount: run.deltas,
    deltaChars: run.chars,
    reasoningDeltaCount: run.reasonDeltas,
    reasoningChars: run.reasonChars,
    usageKeys: u && typeof u === "object" ? Object.keys(u) : null,
    inputTokens: inTok,
    cachedInputTokens: cacheTok,
    outputTokens: outTok,
    reasoningOutputTokens: reasTok,
    visibleTokens: visible,
    // True delivered TPS, exactly as codex-model-benchmarks defines it:
    streamTpsVisible:
      visible != null && streamMs ? +(visible / (streamMs / 1000)).toFixed(1) : null,
    streamTpsOutput:
      outTok != null && streamMs ? +(outTok / (streamMs / 1000)).toFixed(1) : null,
    turnDurationMs: run.turn && run.turn.durationMs,
    turnStatus: run.turn && run.turn.status,
    notificationVocabulary: Object.fromEntries([...vocab.entries()].sort()),
    tokenUsageShape: run.tokenUsage ? Object.keys(run.tokenUsage) : null,
    tokenUsage: run.tokenUsage,
  };
  console.log("\n===== GROUND TRUTH REPORT =====");
  console.log(JSON.stringify(report, null, 2));
  note({ t: stamp(), dir: "report", report });
  logFh.end();
  child.stdin.end();
  setTimeout(() => {
    child.kill();
    process.exit(0);
  }, 300);
}

async function main() {
  await request("initialize", {
    clientInfo: { name: "codex-statusbar-probe", title: "Codex Statusbar Probe", version: "0.1.0" },
    capabilities: { experimentalApi: true, requestAttestation: false },
  });
  send({ method: "initialized" });

  const started = await request("thread/start", {
    model: MODEL,
    cwd: CWD,
    approvalPolicy: "never",
    sandbox: "read-only",
    ephemeral: true,
    historyMode: "paginated",
    experimentalRawEvents: true,
    allowProviderModelFallback: false,
    baseInstructions:
      "You are a benchmarking target. Produce prose only. Never call tools, never read or write files.",
  });
  const threadId = (started && started.thread && started.thread.id) || started?.threadId;
  console.log("[probe] thread/start ->", threadId);
  note({ t: stamp(), dir: "info", threadId });

  await request("turn/start", {
    threadId,
    input: [{ type: "text", text: PROMPT, text_elements: [] }],
    effort: "low",
  });
  console.log("[probe] turn/start sent; streaming...");

  // Safety net: never hang forever.
  setTimeout(() => {
    if (run.completedAt === null) {
      console.error("[probe] timeout, finishing with partial data");
      run.completedAt = Date.now() - t0;
      finish();
    }
  }, 180000);
}

main().catch((e) => {
  console.error("[probe] FATAL:", e.message);
  logFh.end();
  child.kill();
  process.exit(1);
});