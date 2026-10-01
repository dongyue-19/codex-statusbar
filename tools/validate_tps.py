#!/usr/bin/env python3
"""
Validate the TPS formula against official rollout data.

Hypothesis under test
---------------------
The rollout JSONL has no streaming deltas, so live TPS cannot be sampled directly.
But every `item_completed` record carries `started_at_ms` / `completed_at_ms`, and
every `token_usage_record` carries the official per-response `usage`. If an item's
[started_at_ms, completed_at_ms] window really is its generation window, then:

    generation_ms(response) = sum(item durations for Reasoning + AgentMessage)
    TPS_exact(response)     = usage.output_tokens / generation_ms * 1000

should be self-consistent across responses and roughly stable per model.

This script prints, per response, the official token counts and the durations the
rollout reports, so the hypothesis can be confirmed or falsified on real data.
"""
import json
import os
import sys

GEN_TYPES = ("Reasoning", "AgentMessage")


def iter_records(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                yield json.loads(line)
            except json.JSONDecodeError:
                continue


def analyze(path, limit=40):
    print("=" * 100)
    print("FILE:", path)
    print("=" * 100)

    turn_id = None
    pending = []          # (item_type, dur_ms)
    turn_usage = None
    rows = []

    for rec in iter_records(path):
        rtype = rec.get("type")
        payload = rec.get("payload") or {}

        if rtype == "token_usage_record":
            usage = payload.get("usage") or {}
            out = usage.get("output_tokens")
            reas = usage.get("reasoning_output_tokens") or 0
            gen_ms = sum(d for (t, d) in pending if t in GEN_TYPES)
            msg_ms = sum(d for (t, d) in pending if t == "AgentMessage")
            tps_all = (out / gen_ms * 1000) if (out and gen_ms) else None
            tps_vis = ((out - reas) / msg_ms * 1000) if (out and msg_ms and out > reas) else None
            rows.append({
                "ts": rec.get("timestamp"),
                "resp": (payload.get("response_id") or "")[-8:],
                "in": usage.get("input_tokens"),
                "cached": usage.get("cached_input_tokens"),
                "out": out,
                "reas": reas,
                "items": list(pending),
                "gen_ms": gen_ms,
                "msg_ms": msg_ms,
                "tps_all": round(tps_all, 1) if tps_all else None,
                "tps_vis": round(tps_vis, 1) if tps_vis else None,
                "thread_total": (payload.get("thread_token_usage") or {}).get("total_tokens"),
            })
            pending = []
            continue

        if rtype == "event_msg" and payload.get("type") == "item_completed":
            tid = payload.get("turn_id")
            if tid != turn_id:
                turn_id = tid
                pending = []
            item = payload.get("item") or {}
            itype = item.get("type")
            s, c = payload.get("started_at_ms"), payload.get("completed_at_ms")
            dur = (c - s) if (s is not None and c is not None) else 0
            pending.append((itype, dur))
            continue

        if rtype == "event_msg" and payload.get("type") == "task_complete":
            turn_usage = payload
            print("\n--- task_complete: duration_ms=%s ttft_ms=%s" % (
                payload.get("duration_ms"), payload.get("time_to_first_token_ms")))
            continue

    hdr = "%-24s %-9s %8s %8s %7s %7s %8s %8s %8s" % (
        "timestamp", "resp", "in", "cached", "out", "reas", "gen_ms", "tps_all", "tps_vis")
    print(hdr)
    print("-" * len(hdr))
    for r in rows[:limit]:
        print("%-24s %-9s %8s %8s %7s %7s %8s %8s %8s" % (
            r["ts"], r["resp"], r["in"], r["cached"], r["out"], r["reas"],
            r["gen_ms"], r["tps_all"], r["tps_vis"]))
    print("\nitem type breakdown of the windows:")
    for r in rows[:6]:
        print("   ", r["resp"], r["items"])
    return rows


def main():
    home = os.path.expanduser("~")
    root = os.path.join(home, ".codex", "sessions")
    if len(sys.argv) > 1:
        paths = sys.argv[1:]
    else:
        cands = []
        for dp, _dn, fn in os.walk(root):
            for f in fn:
                if f.endswith(".jsonl"):
                    p = os.path.join(dp, f)
                    cands.append((os.path.getmtime(p), p))
        cands.sort(reverse=True)
        paths = [p for _m, p in cands[:2]]
    for p in paths:
        analyze(p)


if __name__ == "__main__":
    main()