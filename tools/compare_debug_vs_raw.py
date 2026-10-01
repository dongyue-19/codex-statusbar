#!/usr/bin/env python3
r"""
Cross-check the monitor's own debug output against an independent re-derivation
from the raw rollout file.

This is the acceptance check: it does not trust the monitor and it does not trust the
monitor's parser. It reads what the monitor *printed* and what the rollout *says*, and
compares them field by field.

Usage:
    python tools/compare_debug_vs_raw.py                 # uses LOCALAPPDATA\CodexStatusbar\debug.log
    python tools/compare_debug_vs_raw.py --log <path>
"""
import argparse
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from verify_metrics import analyze, find_rollout, codex_home  # noqa: E402


def parse_debug_log(path):
    """Return the LAST diagnostics block in the log as a dict."""
    if not os.path.exists(path):
        print(f"!! debug log not found: {path}")
        return None
    text = open(path, encoding="utf-8", errors="replace").read()
    # blocks start with "Current thread:"
    starts = [m.start() for m in re.finditer(r"^Current thread:", text, re.M)]
    if not starts:
        print(f"!! no diagnostics block found in {path}")
        return None
    block = text[starts[-1]:]
    out = {}
    for line in block.splitlines():
        if ":" not in line:
            continue
        k, _, v = line.partition(":")
        out[k.strip()] = v.strip()
    return out


def as_int(d, key):
    v = d.get(key, "")
    m = re.search(r"-?\d+", v.replace(",", ""))
    return int(m.group()) if m else None


def as_float(d, key):
    v = d.get(key, "")
    if v in ("--", "", "None"):
        return None
    m = re.search(r"-?\d+(?:\.\d+)?", v)
    return float(m.group()) if m else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", default=os.path.join(
        os.environ.get("LOCALAPPDATA", ""), "CodexStatusbar", "debug.log"))
    args = ap.parse_args()

    d = parse_debug_log(args.log)
    if not d:
        return 1

    print("=== what the monitor printed ===")
    for k, v in d.items():
        print(f"    {k:<22} {v}")

    thread = d.get("Current thread", "").strip()
    home = codex_home()
    path = find_rollout(home, thread) if thread else None
    if not path:
        print(f"\n!! could not locate the rollout file for thread {thread!r}")
        return 1

    print(f"\n=== independent re-derivation from ===\n    {path}\n")
    # analyze() prints a full report; capture the key numbers by re-deriving here
    recs = [json.loads(l) for l in open(path, encoding="utf-8", errors="replace") if l.strip()]
    inp = cached = out = total = None
    for r in recs:
        pl = r.get("payload") or {}
        if r.get("type") == "token_usage_record" and pl.get("thread_token_usage"):
            tu = pl["thread_token_usage"]
            inp, cached, out, total = (tu.get("input_tokens"), tu.get("cached_input_tokens"),
                                       tu.get("output_tokens"), tu.get("total_tokens"))
        elif r.get("type") == "event_msg" and pl.get("type") == "token_count":
            tu = (pl.get("info") or {}).get("total_token_usage") or {}
            if tu:
                inp, cached, out, total = (tu.get("input_tokens"), tu.get("cached_input_tokens"),
                                           tu.get("output_tokens"), tu.get("total_tokens"))
    if inp is None:
        print("!! no usage record found in the rollout")
        return 1

    checks = [
        ("Input", as_int(d, "Input"), inp),
        ("Cached", as_int(d, "Cached"), cached),
        ("Output", as_int(d, "Output"), out),
        ("Total", as_int(d, "Total"), inp + out),
    ]
    shown_cache = as_float(d, "Cache hit")
    want_cache = (cached / inp * 100) if inp else None

    print(f"{'field':<28} {'monitor':>14} {'raw-derived':>14}   verdict")
    bad = 0
    for name, got, want in checks:
        ok = got == want
        bad += not ok
        print(f"{name:<28} {str(got):>14} {str(want):>14}   {'OK' if ok else 'MISMATCH'}")
    if want_cache is None:
        ok = d.get("Cache hit", "").strip().startswith("--")
        print(f"{'Cache hit':<28} {str(shown_cache):>14} {'-- (input==0)':>14}   {'OK' if ok else 'MISMATCH'}")
        bad += not ok
    else:
        ok = shown_cache is not None and abs(shown_cache - want_cache) < 0.011
        bad += not ok
        print(f"{'Cache hit %':<28} {str(shown_cache):>14} {round(want_cache, 4):>14}   {'OK' if ok else 'MISMATCH'}")
    if total != inp + out:
        print("!! warning: official total_tokens != input+output in this file")
        bad += 1

    # TPS cross-check. The monitor's denominator is the UNION of the turn's model-output windows, so
    # an independent check must union as well, and the monitor's displayed value is EMA-smoothed
    # (alpha 0.35) so it legitimately sits slightly below the final raw sample.
    per_turn_intervals = {}
    per_turn_out = {}
    seen_samples = set()
    for r in recs:
        pl = r.get("payload") or {}
        if r.get("type") == "event_msg" and pl.get("type") == "item_completed":
            it = (pl.get("item") or {}).get("type")
            s, c = pl.get("started_at_ms"), pl.get("completed_at_ms")
            if it in ("Reasoning", "AgentMessage") and s is not None and c is not None:
                d_ms = c - s
                if 0 <= d_ms <= 600_000:
                    tid = pl.get("turn_id")
                    per_turn_intervals.setdefault(tid, []).append((s, s + d_ms))
        elif r.get("type") == "token_usage_record":
            pl2 = r.get("payload") or {}
            usage = pl2.get("usage") or {}
            key = (usage.get("input_tokens"), usage.get("cached_input_tokens"),
                   usage.get("output_tokens"), usage.get("total_tokens"))
            if key not in seen_samples:
                seen_samples.add(key)
                tid = pl2.get("turn_id")
                per_turn_out[tid] = per_turn_out.get(tid, 0) + (usage.get("output_tokens") or 0)
        elif r.get("type") == "event_msg" and pl.get("type") == "token_count":
            last = (pl.get("info") or {}).get("last_token_usage") or {}
            key = (last.get("input_tokens"), last.get("cached_input_tokens"),
                   last.get("output_tokens"), last.get("total_tokens"))
            if last.get("output_tokens") and key not in seen_samples:
                seen_samples.add(key)

    def union(ivals):
        if not ivals:
            return 0
        ivals = sorted(ivals)
        total = 0
        cs, ce = ivals[0]
        for s, e in ivals[1:]:
            if s <= ce:
                ce = max(ce, e)
            else:
                total += ce - cs
                cs, ce = s, e
        return total + (ce - cs)

    final_turn = None
    for r in recs:
        pl = r.get("payload") or {}
        if r.get("type") == "token_usage_record" and pl.get("turn_id"):
            final_turn = pl.get("turn_id")
    raw_ms = union(per_turn_intervals.get(final_turn, []))
    raw_out = per_turn_out.get(final_turn, 0)
    raw_tps = (raw_out / raw_ms * 1000) if raw_ms > 0 and raw_out > 0 else None
    mon_tps = as_float(d, "Last completed TPS")
    mon_merged = as_int(d, "Merged model elapsed ms")
    print()
    if mon_merged is not None and raw_ms:
        same = mon_merged == raw_ms
        bad += not same
        print(f"{'merged model ms (union)':<28} {str(mon_merged):>14} {str(raw_ms):>14}   "
              f"{'OK' if same else 'MISMATCH'}")
    if raw_tps is None:
        print(f"{'Last completed TPS':<28} {str(mon_tps):>14} {'not derivable':>14}   "
              f"{'OK (both absent)' if mon_tps is None else 'CHECK'}")
    else:
        # EMA smoothing may legitimately sit ~15% below the last raw sample; it must never exceed it.
        ok = mon_tps is not None and raw_tps * 0.70 <= mon_tps <= raw_tps * 1.001
        bad += not ok
        print(f"{'Last completed TPS':<28} {str(mon_tps):>14} {round(raw_tps, 1):>14}   "
              f"{'OK (EMA-smoothed)' if ok else 'MISMATCH'}")

    print()
    if bad:
        print(f"RESULT: {bad} mismatch(es) — investigate before trusting the strip")
        return 1
    print("RESULT: monitor output agrees with the raw rollout data")
    return 0


if __name__ == "__main__":
    sys.exit(main())