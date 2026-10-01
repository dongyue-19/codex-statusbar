#!/usr/bin/env python3
"""
Independent verification of the three primary metrics against raw Codex data.

This script deliberately shares NO code with the C# monitor. It re-derives the three
numbers straight from the rollout JSONL so the monitor's output can be checked by hand.

Definitions implemented here (and nothing else):
    conversation_total_tokens = SUM(input_tokens) + SUM(output_tokens)
    cache_hit_rate            = SUM(cached_input_tokens) / SUM(input_tokens) * 100
    turn_tps                  = official output_tokens / model_output_ms * 1000

`model_output_ms` counts ONLY Reasoning + AgentMessage item windows.
CommandExecution / FileChange / McpToolCall / WebSearch / UserMessage are tool, IO or
user time and are excluded by construction.

Usage:
    python tools/verify_metrics.py [thread-id ...]
Without arguments it verifies the most recently updated desktop threads.
"""
import json
import os
import sqlite3
import sys

MODEL_ITEM_TYPES = {"Reasoning", "AgentMessage"}
NON_MODEL_ITEM_TYPES = {
    "UserMessage", "CommandExecution", "FileChange", "McpToolCall",
    "WebSearch", "ImageView", "Compact", "Sleep",
}


def codex_home() -> str:
    return os.environ.get("CODEX_HOME") or os.path.join(os.path.expanduser("~"), ".codex")


def find_rollout(home: str, thread_id: str) -> str | None:
    root = os.path.join(home, "sessions")
    hit = None
    for dp, _dn, fn in os.walk(root):
        for f in fn:
            if f.endswith(".jsonl") and thread_id in f:
                p = os.path.join(dp, f)
                if hit is None or os.path.getmtime(p) > os.path.getmtime(hit):
                    hit = p
    return hit


def recent_desktop_threads(home: str, limit: int) -> list[str]:
    """Most recently viewed threads, read-only, from state_5.sqlite."""
    p = os.path.join(home, "state_5.sqlite")
    if not os.path.exists(p):
        return []
    con = sqlite3.connect(f"file:{p}?mode=ro", uri=True)
    try:
        rows = con.execute(
            "SELECT id, originator, cli_version, tokens_used, recency_at_ms, title "
            "FROM threads WHERE archived = 0 AND originator = 'Codex Desktop' "
            "ORDER BY recency_at_ms DESC LIMIT ?", (limit,)
        ).fetchall()
    finally:
        con.close()
    for r in rows:
        print(f"  candidate {r[0]}  cli={r[2]}  db_tokens_used={r[3]:,}  title={(r[5] or '')[:40]}")
    return [r[0] for r in rows]


def analyze(path: str):
    """Walk the rollout in file order and print the raw + derived numbers."""
    print("=" * 96)
    print("ROLLOUT:", path)
    print(f"size: {os.path.getsize(path)/1048576:.2f} MB")
    print("=" * 96)

    # ---- official cumulative usage: trust the LAST token_usage_record ----------
    thread_usage = None          # last thread_token_usage seen
    per_response = []            # (response_id, output_tokens, reasoning, input, cached)
    turns = {}                   # turn_id -> dict
    order = []
    first_line_meta = None
    item_types_seen = {}

    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for lineno, line in enumerate(fh):
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            if lineno == 0:
                first_line_meta = rec
            rtype = rec.get("type")
            pl = rec.get("payload") or {}

            if rtype == "token_usage_record":
                u = pl.get("usage") or {}
                thread_usage = pl.get("thread_token_usage") or thread_usage
                per_response.append((
                    (pl.get("response_id") or "")[-8:],
                    u.get("input_tokens"), u.get("cached_input_tokens"),
                    u.get("output_tokens"), u.get("reasoning_output_tokens"),
                ))
                tid = pl.get("turn_id")
                t = turns.setdefault(tid, {"out": 0, "model_ms": 0, "items": []})
                t["out"] += u.get("output_tokens") or 0
                if tid not in order:
                    order.append(tid)
                continue

            if rtype == "event_msg" and pl.get("type") == "item_completed":
                tid = pl.get("turn_id")
                t = turns.setdefault(tid, {"out": 0, "model_ms": 0, "items": []})
                if tid not in order:
                    order.append(tid)
                item = pl.get("item") or {}
                itype = item.get("type")
                item_types_seen[itype] = item_types_seen.get(itype, 0) + 1
                s, c = pl.get("started_at_ms"), pl.get("completed_at_ms")
                dur = (c - s) if (s is not None and c is not None) else None
                if dur is not None and 0 <= dur <= 600_000 and itype in MODEL_ITEM_TYPES:
                    t["model_ms"] += dur
                    t["items"].append((itype, dur, s, c))
                t.setdefault("all_items", []).append((itype, dur))
                continue

            if rtype == "event_msg" and pl.get("type") == "task_complete":
                tid = pl.get("turn_id")
                t = turns.setdefault(tid, {"out": 0, "model_ms": 0, "items": []})
                t["duration_ms"] = pl.get("duration_ms")
                t["ttft_ms"] = pl.get("time_to_first_token_ms")
                t["status"] = "completed"
                continue

    # ---- report ---------------------------------------------------------------
    meta = (first_line_meta or {}).get("payload") or {}
    print(f"thread_id     : {meta.get('id')}")
    print(f"originator    : {meta.get('originator')}   source={meta.get('source')}  cli={meta.get('cli_version')}")
    print(f"cwd           : {meta.get('cwd')}")
    print(f"item types    : {item_types_seen}")
    print()

    if not thread_usage:
        print("!! no thread_token_usage found in this rollout")
        return
    inp = thread_usage.get("input_tokens") or 0
    cached = thread_usage.get("cached_input_tokens") or 0
    out = thread_usage.get("output_tokens") or 0
    reas = thread_usage.get("reasoning_output_tokens") or 0
    total = thread_usage.get("total_tokens") or 0

    print("--- RAW (official, cumulative for this conversation) ---")
    print(f"thread_token_usage.input_tokens            = {inp:,}")
    print(f"thread_token_usage.cached_input_tokens     = {cached:,}")
    print(f"thread_token_usage.output_tokens           = {out:,}")
    print(f"thread_token_usage.reasoning_output_tokens = {reas:,}")
    print(f"thread_token_usage.total_tokens            = {total:,}")
    print()
    print("--- DERIVED (what the status bar must show) ---")
    print(f"total = input + output                     = {inp:,} + {out:,} = {inp + out:,}")
    print(f"  .. equals official total_tokens?         = {inp + out == total}")
    print(f"  .. WRONG formula input+cached+output     = {inp + cached + out:,}  (must NOT be used)")
    print(f"uncached = input - cached                  = {inp - cached:,}")
    if inp > 0:
        print(f"cache_hit_rate = cached/input*100          = {cached / inp * 100:.4f}%  -> display {cached / inp * 100:.0f}%")
    else:
        print("cache_hit_rate                             = -- (input == 0, must not be NaN)")
    grand = inp + out
    shown = (f"{grand / 1_000_000:.1f}M" if grand >= 1_000_000 else
             (f"{grand / 1000:.1f}K" if grand >= 1000 else str(grand)))
    print(f"display total                              = {grand:,} -> {shown} tok")
    print()
    print(f"--- PER-RESPONSE official usage (last {min(8, len(per_response))}) ---")
    print(f"{'resp':>9} {'input':>10} {'cached':>10} {'output':>8} {'reasoning':>10}")
    for r in per_response[-8:]:
        print(f"{r[0]:>9} {r[1]:>10,} {r[2]:>10,} {r[3]:>8,} {r[4]:>10,}")
    print()
    print("--- PER-TURN TPS (official output tokens / model-output ms only) ---")
    print(f"{'turn':>10} {'output':>8} {'model_ms':>9} {'tps':>8} {'turn_ms':>9} {'ttft_ms':>8}  model items")
    for tid in order:
        t = turns[tid]
        ms = t["model_ms"]
        tps = (t["out"] / ms * 1000) if ms > 0 else None
        items = ", ".join(f"{a}:{b}" for a, b, _s, _c in t["items"][:6])
        print(f"{(tid or '')[-8:]:>10} {t['out']:>8,} {ms:>9,} "
              f"{(f'{tps:.1f}' if tps else '--'):>8} "
              f"{t.get('duration_ms', ''):>9} {t.get('ttft_ms', ''):>8}  {items}")
    print()
    non_model_ms = 0
    for t in turns.values():
        for itype, dur in t.get("all_items", []):
            if itype in NON_MODEL_ITEM_TYPES and dur:
                non_model_ms += dur
    print(f"excluded tool/IO/user time across all turns = {non_model_ms:,} ms "
          f"(this must never enter the TPS denominator)")


def main():
    home = codex_home()
    print("CODEX_HOME =", home)
    ids = sys.argv[1:]
    if not ids:
        print("\nrecent Codex Desktop threads in state_5.sqlite:")
        ids = recent_desktop_threads(home, 3)[:2]
    for tid in ids:
        p = find_rollout(home, tid)
        if not p:
            print(f"!! no rollout file for {tid}")
            continue
        analyze(p)


if __name__ == "__main__":
    main()