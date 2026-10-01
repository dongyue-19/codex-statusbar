// Probe Codex Desktop's local IPC endpoint (Windows named pipe \\.\pipe\codex-ipc).
//
// Codex Desktop exposes a length-prefixed JSON broadcast channel. codex-token-overlay
// (MIT) uses it to learn which conversation the user currently has open, via the
// `thread-stream-following-changed` broadcast. This probe verifies that protocol on
// this machine and records the full message vocabulary, and — importantly — checks
// whether app-server notifications (item/agentMessage/delta, thread/tokenUsage/updated)
// are broadcast over the same channel. That would make true live TPS available without
// touching the Codex process at all.
//
// Read-only: it only listens and identifies itself as a separate client type.
//
// Usage: node tools/probe_codex_ipc.mjs [--seconds 20]

import net from "node:net";
import path from "node:path";
import fs from "node:fs";

const argv = process.argv.slice(2);
const arg = (n, d) => {
  const i = argv.indexOf(n);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : d;
};
const SECONDS = Number(arg("--seconds", "20"));
const PIPE = arg("--pipe", "\\\\.\\pipe\\codex-ipc");
const LOG = arg("--log", path.join(process.cwd(), "docs", "probe-codex-ipc.jsonl"));

const logFh = fs.createWriteStream(LOG, { flags: "a" });
const t0 = Date.now();
const note = (o) => logFh.write(JSON.stringify(o) + "\n");

const methodCounts = new Map();
const typeCounts = new Map();
const clientTypes = new Set();

const sock = net.connect(PIPE);
let buf = Buffer.alloc(0);
let ready = false;

sock.on("connect", () => {
  console.log("[ipc] connected to", PIPE);
  ready = true;
  const requestId = crypto.randomUUID();
  const init = {
    type: "request",
    requestId,
    sourceClientId: "codex-statusbar-probe",
    version: 0,
    method: "initialize",
    params: { clientType: "codex-statusbar-probe" },
  };
  writeFrame(init);
  console.log("[ipc] sent initialize:", JSON.stringify(init));
});

sock.on("error", (e) => {
  console.error("[ipc] ERROR", e.code, e.message);
  note({ t: Date.now() - t0, dir: "error", code: e.code, message: e.message });
});

sock.on("close", () => {
  console.log("[ipc] closed");
  report();
});

function writeFrame(obj) {
  const json = Buffer.from(JSON.stringify(obj), "utf8");
  const head = Buffer.alloc(4);
  head.writeUInt32LE(json.length, 0);
  sock.write(Buffer.concat([head, json]));
}

sock.on("data", (chunk) => {
  buf = Buffer.concat([buf, chunk]);
  for (;;) {
    if (buf.length < 4) return;
    const len = buf.readUInt32LE(0);
    if (len > 8 * 1024 * 1024) {
      note({ t: Date.now() - t0, dir: "error", message: `implausible frame length ${len}` });
      buf = Buffer.alloc(0);
      return;
    }
    if (buf.length < 4 + len) return;
    const body = buf.subarray(4, 4 + len).toString("utf8");
    buf = buf.subarray(4 + len);
    handleFrame(body);
  }
});

function handleFrame(body) {
  let m;
  try {
    m = JSON.parse(body);
  } catch {
    note({ t: Date.now() - t0, dir: "recv-raw", body: body.slice(0, 2000) });
    return;
  }
  note({ t: Date.now() - t0, dir: "recv", msg: m });
  const type = m.type ?? "(none)";
  typeCounts.set(type, (typeCounts.get(type) || 0) + 1);
  if (m.sourceClientId) clientTypes.add(`${m.sourceClientId}@v${m.version}`);
  const method = m.method ?? "(none)";
  methodCounts.set(`${type}:${method}`, (methodCounts.get(`${type}:${method}`) || 0) + 1);
  if (m.method) {
    const keys = Object.keys(m.params || {});
    console.log(
      `[ipc] << ${type} ${m.method}  params=${JSON.stringify(m.params || {}).slice(0, 300)}`
    );
    note({ t: Date.now() - t0, dir: "method-keys", method: m.method, keys });
  }
}

function report() {
  const r = {
    pipe: PIPE,
    listenedSeconds: SECONDS,
    typeCounts: Object.fromEntries([...typeCounts.entries()].sort()),
    methodCounts: Object.fromEntries([...methodCounts.entries()].sort()),
    clientTypes: [...clientTypes].sort(),
  };
  console.log("\n===== IPC PROBE REPORT =====");
  console.log(JSON.stringify(r, null, 2));
  note({ t: Date.now() - t0, dir: "report", report: r });
  logFh.end();
  process.exit(0);
}

setTimeout(() => {
  if (ready) sock.end();
  else report();
}, SECONDS * 1000);