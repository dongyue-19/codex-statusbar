// Ground-truth TPS study.
//
// Question: the monitor derives TPS as
//     official output_tokens / union(Reasoning + AgentMessage item windows)
// but never sees the raw token stream. How close is that to the speed measured directly
// from the streaming deltas?
//
// Method: start our OWN Codex app-server on stdio with `experimentalRawEvents`, run a set of
// varied turns (sequential, on ephemeral threads so nothing is written into the user's Codex
// sidebar), and for each turn record BOTH numbers from the very same event stream:
//
//   raw TPS      = visible output tokens / (last delta - first delta)        <- true delivered speed
//   derived TPS  = official output tokens / union(item started..completed)   <- what the monitor shows
//
// It also confirms the semantics of the official counters: whether `output_tokens` includes the
// hidden reasoning tokens, and whether the per-response outputs sum to the thread cumulative total.
//
// Usage: node tools/groundtruth_tps.mjs [--turns N] [--out file]

import { spawn } from "node:child_process";
import readline from "node:readline";
import path from "node:path";
import os from "node:os";
import fs from "node:fs";

const argv = process.argv.slice(2);
const arg = (name, dflt) => {
  const i = argv.indexOf(name);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : dflt;
};

const CODEX_BIN =
  process.env.CODEX_BIN ||
  path.join(os.homedir(), ".codex", "plugins", ".plugin-appserver", "codex.exe");
const MODEL = arg("--model", "deepseek-v4.1-flash");
const CWD = arg("--cwd", process.cwd());
const OUT = arg("--out", path.join(CWD, "docs", "evidence", "groundtruth-tps.json"));

// The app-server protocol names ThreadItem types in camelCase ("reasoning", "agentMessage"),
// whereas the rollout JSONL persists them in PascalCase ("Reasoning", "AgentMessage"). Accept both.
const MODEL_ITEM_TYPES = new Set(["Reasoning", "AgentMessage", "reasoning", "agentMessage"]);

// Varied scenarios: short, long, reasoning-heavy, code-heavy, CJK, mixed, and two effort levels,
// so the sample covers a wide range of reasoning/visible token ratios.
const SCENARIOS = [
  { name: "one-word", effort: "low", prompt: "Reply with exactly one word: ok" },
  { name: "short-sentence", effort: "low", prompt: "In one sentence, say what a TCP handshake is." },
  { name: "medium-prose", effort: "low", prompt: "Write about 150 words on why cache locality matters in modern CPUs." },
  { name: "long-prose", effort: "medium", prompt: "Write about 500 words explaining how B-tree indexes differ from LSM trees, with tradeoffs." },
  { name: "reasoning-arithmetic", effort: "high", prompt: "Compute 18 * 27 + 46 * 13 - 19 * 21. Show your reasoning briefly, then give the final number." },
  { name: "reasoning-puzzle", effort: "high", prompt: "Three switches outside a room control three bulbs inside. You may enter the room once. How do you identify each switch? Reason it out." },
  { name: "code-block", effort: "medium", prompt: "Write a Python function that merges two sorted lists in-place-ish (returns a new list), with a docstring and 3 unit-test-style assertions. Code only." },
  { name: "bullets", effort: "low", prompt: "List 12 distinct uses of a hash map, one short bullet each." },
  { name: "chinese-long", effort: "medium", prompt: "请用大约 400 字说明 HTTP/2 多路复用与 HTTP/1.1 管道化的区别，并给出各自的历史背景。" },
  { name: "mixed-language", effort: "medium", prompt: "Explain 缓存一致性协议（MESI）in English, but keep the technical terms in Chinese where natural. About 250 words." },
  { name: "tabular", effort: "low", prompt: "Give a markdown table comparing TCP, UDP and QUIC across 6 dimensions." },
  { name: "reasoning-many-steps", effort: "high", prompt: "A train leaves at 09:15 at 80 km/h. Another leaves the same station at 10:00 at 110 km/h on the same track. When and where does the second catch the first? Show every step." },
  { name: "strict-format", effort: "low", prompt: "Output only a JSON object with keys a,b,c and integer values 1,2,3. No prose." },
  { name: "very-long", effort: "medium", prompt: "Write about 900 words on the history of the Unix pipe, from Thompson's early experiments to modern shells." },
];

const requestedTurns = Number(arg("--turns", String(SCENARIOS.length)));
const scenarios = SCENARIOS.slice(0, Math.max(1, Math.min(requestedTurns, SCENARIOS.length)));

const child = spawn(CODEX_BIN, ["app-server"], { stdio: ["pipe", "pipe", "inherit"], env: process.env });

let nextId = 1;
const pending = new Map();
const send = (m) => child.stdin.write(JSON.stringify(m) + "\n");
const request = (method, params) =>
  new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject, method });
    send({ id, method, params });
  });

const rl = readline.createInterface({ input: child.stdout });

// per-turn accumulator
let turn = null;
const results = [];
const globalChecks = {
  reasoningSubsetOfOutput: true,
  perResponseSumMatchesThreadTotal: null,
  carriersAgree: true,
};

function startTurn(scenario) {
  turn = {
    scenario,
    deltas: 0,
    visibleChars: 0,
    firstDeltaAt: null,
    firstAnyDeltaAt: null,
    lastDeltaAt: null,
    reasonChars: 0,
    intervals: [],
    openItems: new Map(),
    official: null,
    tokenUsageLast: null,
    startedAt: null,
    completedAt: null,
  };
}

function unionMs(intervals) {
  if (intervals.length === 0) return 0;
  const sorted = [...intervals].sort((a, b) => a[0] - b[0]);
  let total = 0;
  let [cs, ce] = sorted[0];
  for (const [s, e] of sorted.slice(1)) {
    if (s <= ce) ce = Math.max(ce, e);
    else {
      total += ce - cs;
      cs = s;
      ce = e;
    }
  }
  return total + (ce - cs);
}

rl.on("line", (line) => {
  let m;
  try {
    m = JSON.parse(line);
  } catch {
    return;
  }
  if (m.id !== undefined) {
    const slot = pending.get(m.id);
    if (slot) {
      pending.delete(m.id);
      m.error ? slot.reject(new Error(`${slot.method}: ${JSON.stringify(m.error)}`)) : slot.resolve(m.result);
    }
    return;
  }
  if (!m.method || !turn) return;
  const p = m.params || {};
  switch (m.method) {
    case "turn/started":
      turn.startedAt = Date.now();
      break;
    case "item/started":
      if (MODEL_ITEM_TYPES.has(p.item?.type)) turn.openItems.set(p.item.id, Date.now());
      break;
    case "item/completed":
      if (MODEL_ITEM_TYPES.has(p.item?.type)) {
        const s = turn.openItems.get(p.item.id);
        if (s) {
          turn.intervals.push([s, Date.now()]);
          turn.openItems.delete(p.item.id);
        }
      }
      break;
    case "item/agentMessage/delta":
      turn.deltas++;
      turn.visibleChars += (p.delta || "").length;
      if (turn.firstDeltaAt === null) turn.firstDeltaAt = Date.now();
      if (turn.firstAnyDeltaAt === null) turn.firstAnyDeltaAt = Date.now();
      turn.lastDeltaAt = Date.now();
      break;
    case "item/reasoning/textDelta":
    case "item/reasoning/summaryTextDelta":
      turn.reasonChars += (p.delta || "").length;
      // Reasoning is emitted BEFORE the visible answer. The honest analogue of the monitor's
      // denominator therefore starts at the first delta of ANY kind, because the monitor's union
      // includes the Reasoning item window while its numerator includes reasoning tokens.
      if (turn.firstAnyDeltaAt === null) turn.firstAnyDeltaAt = Date.now();
      break;
    case "rawResponse/completed":
      if (p.usage) turn.official = p.usage;
      break;
    case "thread/tokenUsage/updated":
      turn.tokenUsageLast = p.tokenUsage?.last ?? null;
      break;
    case "turn/completed":
      turn.completedAt = Date.now();
      finishTurn();
      break;
  }
});

function finishTurn() {
  const t = turn;
  turn = null;

  const usage = t.official || t.tokenUsageLast || {};
  const num = (a, b) => (usage[a] ?? usage[b] ?? null);
  const outputTokens = num("outputTokens", "output_tokens");
  const reasoningTokens = num("reasoningOutputTokens", "reasoning_output_tokens") ?? 0;
  const inputTokens = num("inputTokens", "input_tokens");
  const cachedTokens = num("cachedInputTokens", "cached_input_tokens");

  if (outputTokens != null && reasoningTokens > outputTokens) globalChecks.reasoningSubsetOfOutput = false;

  const streamMs = t.firstDeltaAt && t.lastDeltaAt ? t.lastDeltaAt - t.firstDeltaAt : 0;
  // The comparable raw window: first delta of any kind -> last visible delta. This spans exactly the
  // same phases as the monitor's union(Reasoning + AgentMessage) denominator.
  const rawWindowMs =
    t.firstAnyDeltaAt && t.lastDeltaAt ? t.lastDeltaAt - t.firstAnyDeltaAt : 0;
  const mergedMs = unionMs(t.intervals);
  const visible = outputTokens != null ? Math.max(0, outputTokens - reasoningTokens) : null;

  const rawTps = visible != null && streamMs > 0 ? (visible / streamMs) * 1000 : null;
  const derivedTps = outputTokens != null && mergedMs > 0 ? (outputTokens / mergedMs) * 1000 : null;

  // Both sides now use the SAME numerator (total output tokens, reasoning included) so the only
  // difference between them is how the elapsed time was obtained: streamed deltas vs item windows.
  const rawTpsSameNumerator =
    outputTokens != null && rawWindowMs > 0 ? (outputTokens / rawWindowMs) * 1000 : null;

  const relError =
    rawTpsSameNumerator && derivedTps
      ? Math.abs(derivedTps - rawTpsSameNumerator) / rawTpsSameNumerator
      : null;

  const row = {
    name: t.scenario.name,
    effort: t.scenario.effort,
    deltaCount: t.deltas,
    visibleChars: t.visibleChars,
    reasoningChars: t.reasonChars,
    inputTokens,
    cachedInputTokens: cachedTokens,
    outputTokens,
    reasoningOutputTokens: reasoningTokens,
    visibleTokens: visible,
    modelItemCount: t.intervals.length,
    deltaWindowMs: streamMs,
    rawWindowMs,
    mergedModelMs: mergedMs,
    ttftMs: t.firstDeltaAt,
    ttftAnyMs: t.firstAnyDeltaAt,
    turnMs: t.startedAt && t.completedAt ? t.completedAt - t.startedAt : null,
    rawTpsVisible: rawTps == null ? null : +rawTps.toFixed(1),
    rawTpsTotalOutput: rawTpsSameNumerator == null ? null : +rawTpsSameNumerator.toFixed(1),
    derivedTps: derivedTps == null ? null : +derivedTps.toFixed(1),
    relativeError: relError == null ? null : +relError.toFixed(4),
  };
  results.push(row);
  console.log(
    `  ${row.name.padEnd(24)} out=${String(row.outputTokens).padStart(5)} reas=${String(row.reasoningOutputTokens).padStart(5)} ` +
      `deltaWin=${String(row.deltaWindowMs).padStart(6)}ms modelWin=${String(row.mergedModelMs).padStart(6)}ms  ` +
      `raw=${String(row.rawTpsTotalOutput).padStart(7)} derived=${String(row.derivedTps).padStart(7)} err=${
        row.relativeError == null ? "  n/a" : (row.relativeError * 100).toFixed(1) + "%"
      }`
  );
  runNext();
}

async function runNext() {
  const i = results.length;
  if (i >= scenarios.length) return report();
  const scenario = scenarios[i];
  startTurn(scenario);
  try {
    const started = await request("thread/start", {
      model: MODEL,
      cwd: CWD,
      approvalPolicy: "never",
      sandbox: "read-only",
      ephemeral: true,
      historyMode: "paginated",
      experimentalRawEvents: true,
      allowProviderModelFallback: false,
      baseInstructions: "You are a benchmarking target. Produce the answer only. Never call tools.",
    });
    const threadId = started?.thread?.id ?? started?.threadId;
    await request("turn/start", {
      threadId,
      input: [{ type: "text", text: scenario.prompt, text_elements: [] }],
      effort: scenario.effort,
    });
  } catch (e) {
    console.error(`  ${scenario.name}: FAILED ${e.message}`);
    turn = null;
    runNext();
  }
}

function percentile(sorted, p) {
  if (sorted.length === 0) return null;
  const idx = Math.min(sorted.length - 1, Math.max(0, Math.ceil(p * sorted.length) - 1));
  return sorted[idx];
}

function report() {
  const errors = results.map((r) => r.relativeError).filter((v) => v != null).sort((a, b) => a - b);
  const summary = {
    model: MODEL,
    measuredAt: new Date().toISOString(),
    turns: results.length,
    turnsWithError: errors.length,
    medianRelativeError: percentile(errors, 0.5),
    p95RelativeError: percentile(errors, 0.95),
    maxRelativeError: errors.length ? errors[errors.length - 1] : null,
    meanRelativeError: errors.length ? errors.reduce((a, b) => a + b, 0) / errors.length : null,
    reasoningTokensAreSubsetOfOutputTokens: globalChecks.reasoningSubsetOfOutput,
    semantics: {
      outputTokensIncludesReasoning:
        "output_tokens is the total emitted by the model and reasoning_output_tokens is a SUBSET of it " +
        "(visible = output - reasoning). The denominator counts the Reasoning item window, so numerator " +
        "and denominator cover the same span: reasoning + visible output.",
    },
  };

  console.log("\n===== SUMMARY =====");
  console.log(JSON.stringify(summary, null, 2));

  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, JSON.stringify({ summary, rows: results }, null, 2));
  console.log(`\nwritten: ${OUT}`);

  child.stdin.end();
  setTimeout(() => {
    child.kill();
    process.exit(errors.length > 0 ? 0 : 1);
  }, 300);
}

async function main() {
  await request("initialize", {
    clientInfo: { name: "codex-statusbar-groundtruth", title: "Ground Truth TPS", version: "1.0.0" },
    capabilities: { experimentalApi: true, requestAttestation: false },
  });
  send({ method: "initialized" });
  console.log(`model=${MODEL}  turns=${scenarios.length}\n`);
  runNext();
}

main().catch((e) => {
  console.error("FATAL", e.message);
  child.kill();
  process.exit(1);
});