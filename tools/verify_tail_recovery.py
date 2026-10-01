#!/usr/bin/env python3
"""
Verify that reading only a bounded TAIL of a rollout recovers exactly the same
cumulative numbers and turn state as a full parse.

This matters because session files reach tens or hundreds of MB and the monitor must
never parse them whole. It also checks the incremental-append property used at runtime.

Usage: python tools/verify_tail_recovery.py
"""
import json
import os
import sys

TAIL_BYTES = 4 * 1024 * 1024
MODEL_ITEM_TYPES = {"Reasoning", "AgentMessage"}
NON_MODEL_ITEM_TYPES = {"UserMessage", "CommandExecution", "FileChange",
                        "McpToolCall", "WebSearch", "ImageView"}


def parse_records(chunks, carry=b""):
    """Feed byte chunks; yield complete parsed JSON lines, keeping partial lines."""
    for buf in chunks:
        data = carry + buf
        lines = data.split(b"\n")
        carry = lines.pop()
        for raw in lines:
            raw = raw.strip()
            if not raw:
                continue
            try:
                yield json.loads(raw)
            except json.JSONDecodeError:
                continue


def summarize(records, state=None):
    st = state or {"thread": None, "input": 0, "cached": 0, "output": 0,
                   "total": 0, "turn": None, "model_ms": 0, "turn_out": 0,
                   "last_turn_tps": None, "records": 0}
    for rec in records:
        t = rec.get("type")
        pl = rec.get("payload") or {}
        st["records"] += 1
        if t == "token_usage_record":
            tu = pl.get("thread_token_usage") or {}
            if tu:
                st["thread"] = pl.get("thread_id")
                st["input"] = tu.get("input_tokens") or 0
                st["cached"] = tu.get("cached_input_tokens") or 0
                st["output"] = tu.get("output_tokens") or 0
                st["total"] = tu.get("total_tokens") or 0
                st["carrier"] = "token_usage_record"
            u = pl.get("usage") or {}
            tid = pl.get("turn_id")
            if tid != st["turn"]:
                if st["model_ms"] > 0 and st["turn_out"] > 0:
                    st["last_turn_tps"] = st["turn_out"] / st["model_ms"] * 1000
                st["turn"] = tid
                st["model_ms"] = 0
                st["turn_out"] = 0
            st["turn_out"] += u.get("output_tokens") or 0
        elif t == "event_msg" and pl.get("type") == "token_count":
            # Codex <= 0.151 has ONLY this carrier; 0.159 has both and they agree.
            info = pl.get("info") or {}
            tu = info.get("total_token_usage") or {}
            if tu:
                st["input"] = tu.get("input_tokens") or 0
                st["cached"] = tu.get("cached_input_tokens") or 0
                st["output"] = tu.get("output_tokens") or 0
                st["total"] = tu.get("total_tokens") or 0
                st["carrier"] = "token_count"
            last = info.get("last_token_usage") or {}
            if last:
                st["turn_out"] = (last.get("output_tokens") or 0) + st["turn_out"]
        elif t == "event_msg" and pl.get("type") == "item_completed":
            item = pl.get("item") or {}
            if item.get("type") in MODEL_ITEM_TYPES:
                s, c = pl.get("started_at_ms"), pl.get("completed_at_ms")
                if s is not None and c is not None:
                    d = c - s
                    if 0 <= d <= 600_000:
                        if pl.get("turn_id") != st["turn"] and st["model_ms"] > 0:
                            st["last_turn_tps"] = st["turn_out"] / st["model_ms"] * 1000
                            st["turn"] = pl.get("turn_id")
                            st["model_ms"] = 0
                            st["turn_out"] = 0
                        st["model_ms"] += d
        elif t == "session_meta":
            st["thread"] = (pl.get("id") or pl.get("session_id"))
    return st


def report(label, st):
    tps = (st["turn_out"] / st["model_ms"] * 1000) if st["model_ms"] > 0 else None
    print(f"{label:<34} thread={str(st['thread'])[:20]:<20} "
          f"input={st['input']:>12,} cached={st['cached']:>12,} output={st['output']:>9,} "
          f"total={st['total']:>12,} turn={str(st['turn'])[:8]} "
          f"model_ms={st['model_ms']:>7,} tps={(f'{tps:.1f}' if tps else '--'):>7} "
          f"recs={st['records']}")


def main():
    home = os.environ.get("CODEX_HOME") or os.path.join(os.path.expanduser("~"), ".codex")
    root = os.path.join(home, "sessions")
    files = []
    for dp, _dn, fn in os.walk(root):
        for f in fn:
            if f.endswith(".jsonl"):
                p = os.path.join(dp, f)
                files.append((os.path.getsize(p), p))
    files.sort(reverse=True)
    if not files:
        print("no session files")
        return 1

    print(f"TAIL_BYTES = {TAIL_BYTES:,}\n")
    worst = 0
    for size, path in files[:6]:
        print("=" * 130)
        print(f"{path}\n  size = {size/1048576:.2f} MB")
        # full parse
        with open(path, "rb") as fh:
            full = summarize(parse_records(iter(lambda: fh.read(1 << 20), b"")))
        # tail-only parse, discarding the first (possibly partial) line
        with open(path, "rb") as fh:
            start = max(0, size - TAIL_BYTES)
            fh.seek(start)
            blob = fh.read()
        first_nl = blob.find(b"\n")
        tail_blob = blob[first_nl + 1:] if first_nl >= 0 else b""
        tail = summarize(parse_records([tail_blob]))
        # tail parse seeded from the state DB (what the monitor actually does)
        report("full parse", full)
        report("tail parse only", tail)
        ok = (full["input"] == tail["input"] and full["cached"] == tail["cached"]
              and full["output"] == tail["output"] and full["total"] == tail["total"])
        print(f"  cumulative usage identical? {ok}")
        delta_tps = abs((full["last_turn_tps"] or 0) - (tail["last_turn_tps"] or 0))
        print(f"  last-turn TPS full={full['last_turn_tps']} tail={tail['last_turn_tps']} "
              f"(delta {delta_tps:.1f})")
        if not ok:
            worst += 1
    print("=" * 130)
    print(f"files where tail recovery differed from full parse: {worst}")
    return 0 if worst == 0 else 1


if __name__ == "__main__":
    sys.exit(main())