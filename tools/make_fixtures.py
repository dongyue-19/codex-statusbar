#!/usr/bin/env python3
"""
Generate deterministic rollout JSONL fixtures with hand-computable expected values,
then assert those expectations. These fixtures define the monitor's required behaviour
and are the reference for the C# self-test (ProbeRunner).

Run:  python tools/make_fixtures.py
Writes: fixtures/fixture-modern.jsonl, fixtures/fixture-legacy.jsonl
Exits non-zero if any expectation fails.

Design of the TPS fixture (all numbers chosen so the answer is exact)
--------------------------------------------------------------------
Turn A model output windows (Reasoning + AgentMessage only):
    Reasoning        1000 ->  5000 ms   = 4000 ms
    CommandExecution 5000 -> 10000 ms   = 5000 ms   <-- tool time, MUST be excluded
    AgentMessage    10000 -> 13000 ms   = 3000 ms
    --------------------------------------------------
    model_output_ms = 4000 + 3000 = 7000 ms
    official output_tokens (turn A, from usage) = 1400
    => turn A TPS = 1400 / 7000 * 1000 = 200.0 tok/s   (exactly)

Turn B:
    Reasoning       20000 -> 21000 ms   = 1000 ms
    AgentMessage    21000 -> 23000 ms   = 2000 ms
    model_output_ms = 3000 ms ; output_tokens = 300
    => turn B TPS = 100.0 tok/s   (this becomes "last turn avg TPS")

Cumulative after turn B: input 220000, cached 204000, output 1700
    total = input + output = 221700          (official total_tokens == 221700)
    cache = 204000 / 220000 * 100 = 92.7272...%   -> display 93%
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
FIX = os.path.join(ROOT, "fixtures")

MODERN_ID = "11111111-1111-7111-8111-111111111111"
LEGACY_ID = "22222222-2222-7222-8222-222222222222"
TURN_A = "aaaa0000-0000-7000-8000-000000000001"
TURN_B = "bbbb0000-0000-7000-8000-000000000002"

# ---- expected values, asserted below -----------------------------------------
EXPECT_MODERN = {
    "total": 221_700,               # input 220000 + output 1700
    "input": 220_000,
    "cached": 204_000,
    "output": 1_700,
    "uncached": 16_000,             # input - cached
    "cache_percent": 92.72727272727273,
    "cache_display": 93,
    "display_total": "221.7K tok",  # 221700 -> one decimal + K
    "turn_a_tps": 200.0,
    "turn_b_tps": 100.0,
    "last_turn_tps": 100.0,
    "turn_a_model_ms": 7000,        # 4000 + 3000, tool 5000 excluded
    "turn_b_model_ms": 3000,
    "excluded_tool_ms": 5000,
}
EXPECT_LEGACY = {
    "total": 5_699_084,             # 5,646,305 + 52,779
    "input": 5_646_305,
    "cached": 5_526_656,
    "output": 52_779,
    "uncached": 119_649,            # 5,646,305 - 5,526,656
    "cache_percent": 5_526_656 / 5_646_305 * 100,   # 97.8775...%
    "cache_display": 98,
    "display_total": "5.7M tok",
    "wrong_formula": 11_225_740,    # input + cached + output (must never be shown)
}


def item(turn, itype, start, end, extra=None):
    it = {"type": itype, "id": f"{itype.lower()}-{start}"}
    if itype == "AgentMessage":
        it["content"] = [{"type": "Text", "text": "x"}]
        it["phase"] = "commentary"
    if extra:
        it.update(extra)
    return {
        "type": "event_msg",
        "payload": {
            "type": "item_completed",
            "thread_id": MODERN_ID,
            "turn_id": turn,
            "item": it,
            "started_at_ms": start,
            "completed_at_ms": end,
        },
    }


def task_started(turn, ts):
    return {"type": "event_msg", "payload": {"type": "task_started", "turn_id": turn, "started_at": ts}}


def task_complete(turn, duration_ms, ttft_ms):
    return {
        "type": "event_msg",
        "payload": {
            "type": "task_complete",
            "turn_id": turn,
            "duration_ms": duration_ms,
            "time_to_first_token_ms": ttft_ms,
            "last_agent_message": "done",
        },
    }


def usage(input_t, cached, output, reasoning):
    return {
        "input_tokens": input_t,
        "cached_input_tokens": cached,
        "cache_write_input_tokens": 0,
        "output_tokens": output,
        "reasoning_output_tokens": reasoning,
        "total_tokens": input_t + output,
    }


def build_modern():
    lines = [
        {
            "timestamp": "2026-09-30T15:00:00.000Z",
            "ordinal": 0,
            "type": "session_meta",
            "payload": {
                "id": MODERN_ID,
                "session_id": MODERN_ID,
                "timestamp": "2026-09-30T15:00:00.000Z",
                "cwd": "C:\\fixture",
                "originator": "Codex Desktop",
                "cli_version": "0.159.2",
                "source": "vscode",
                "thread_source": "user",
            },
        }
    ]
    o = 1

    # ---- turn A ----
    lines.append(task_started(TURN_A, 1000))
    lines.append(item(TURN_A, "UserMessage", 1000, 1000))
    lines.append(item(TURN_A, "Reasoning", 1000, 5000))
    lines.append(item(TURN_A, "CommandExecution", 5000, 10000))   # 5000 ms tool time
    lines.append(item(TURN_A, "AgentMessage", 10000, 13000))
    ua = usage(100_000, 90_000, 1_400, 200)
    lines.append({
        "type": "token_usage_record",
        "payload": {
            "thread_id": MODERN_ID, "turn_id": TURN_A, "session_id": MODERN_ID,
            "response_id": "resp_A", "usage": ua,
            "turn_token_usage": ua, "thread_token_usage": ua,
        },
    })
    lines.append(task_complete(TURN_A, 20_000, 1_200))

    # ---- turn B ----
    lines.append(task_started(TURN_B, 20_000))
    lines.append(item(TURN_B, "Reasoning", 20_000, 21_000))
    lines.append(item(TURN_B, "AgentMessage", 21_000, 23_000))
    ub = usage(120_000, 114_000, 300, 50)
    thread_b = usage(220_000, 204_000, 1_700, 250)
    turn_b = usage(220_000, 204_000, 1_700, 250)
    lines.append({
        "type": "token_usage_record",
        "payload": {
            "thread_id": MODERN_ID, "turn_id": TURN_B, "session_id": MODERN_ID,
            "response_id": "resp_B", "usage": ub,
            "turn_token_usage": turn_b, "thread_token_usage": thread_b,
        },
    })
    lines.append(task_complete(TURN_B, 12_000, 900))
    for i, ln in enumerate(lines):
        ln.setdefault("ordinal", i)
        ln.setdefault("timestamp", f"2026-09-30T15:00:{i:02d}.000Z")
    return lines


def build_legacy():
    """Only event_msg/token_count — no token_usage_record at all (Codex <=0.151)."""
    tot = {
        "input_tokens": 5_646_305,
        "cached_input_tokens": 5_526_656,
        "cache_write_input_tokens": 0,
        "output_tokens": 52_779,
        "reasoning_output_tokens": 20_000,
        "total_tokens": 5_699_084,
    }
    lines = [
        {
            "timestamp": "2026-09-30T15:00:00.000Z", "ordinal": 0, "type": "session_meta",
            "payload": {
                "id": LEGACY_ID, "session_id": LEGACY_ID,
                "timestamp": "2026-09-30T15:00:00.000Z", "cwd": "C:\\fixture",
                "originator": "Codex Desktop", "cli_version": "0.151.0-alpha.7.2",
                "source": "vscode", "thread_source": "user",
            },
        },
        {
            "timestamp": "2026-09-30T15:00:05.000Z", "ordinal": 1, "type": "event_msg",
            "payload": {
                "type": "token_count",
                "info": {
                    "total_token_usage": tot,
                    "last_token_usage": tot,
                    "model_context_window": 950_000,
                },
                "rate_limits": None,
            },
        },
    ]
    return lines


def write(name, lines):
    os.makedirs(FIX, exist_ok=True)
    p = os.path.join(FIX, name)
    with open(p, "w", encoding="utf-8", newline="\n") as fh:
        for ln in lines:
            fh.write(json.dumps(ln, ensure_ascii=False) + "\n")
    return p


# ---------------------------------------------------------------- verification
def read(path):
    with open(path, encoding="utf-8") as fh:
        return [json.loads(l) for l in fh if l.strip()]


def derive(records):
    """Independent re-derivation, deliberately not sharing logic with the C# code."""
    st = {"input": None, "cached": None, "output": None, "total": None,
          "per_turn_out": {}, "per_turn_ms": {}, "tpss": {}, "excluded_ms": 0,
          "turn_order": []}
    for rec in records:
        t = rec.get("type")
        pl = rec.get("payload") or {}
        if t == "token_usage_record" and pl.get("thread_token_usage"):
            tu = pl["thread_token_usage"]
            st.update(input=tu["input_tokens"], cached=tu["cached_input_tokens"],
                      output=tu["output_tokens"], total=tu["total_tokens"])
            tid = pl.get("turn_id")
            st["per_turn_out"][tid] = st["per_turn_out"].get(tid, 0) + (pl.get("usage") or {}).get("output_tokens", 0)
        elif t == "event_msg":
            pt = pl.get("type")
            if pt == "token_count":
                tu = (pl.get("info") or {}).get("total_token_usage") or {}
                st.update(input=tu.get("input_tokens"), cached=tu.get("cached_input_tokens"),
                          output=tu.get("output_tokens"), total=tu.get("total_tokens"))
            elif pt == "item_completed":
                tid = pl.get("turn_id")
                it = (pl.get("item") or {}).get("type")
                s, c = pl.get("started_at_ms"), pl.get("completed_at_ms")
                if s is None or c is None:
                    continue
                d = c - s
                if it in ("Reasoning", "AgentMessage"):
                    st["per_turn_ms"][tid] = st["per_turn_ms"].get(tid, 0) + d
                    if tid not in st["turn_order"]:
                        st["turn_order"].append(tid)
                else:
                    st["excluded_ms"] += d
    for tid in st["turn_order"]:
        ms = st["per_turn_ms"].get(tid, 0)
        out = st["per_turn_out"].get(tid, 0)
        st["tpss"][tid] = (out / ms * 1000) if ms > 0 else None
    return st


def fmt(value):
    if value is None:
        return "-- tok"
    if value < 1000:
        return f"{value} tok"
    if value < 1_000_000:
        return f"{value / 1000:.1f}K tok"
    return f"{value / 1_000_000:.1f}M tok"


def check(label, got, want):
    ok = got == want if not isinstance(want, float) else abs(got - want) < 1e-9
    print(f"    {'OK  ' if ok else 'FAIL'} {label}: got={got!r} want={want!r}")
    return ok


def main():
    failures = 0
    p_modern = write("fixture-modern.jsonl", build_modern())
    p_legacy = write("fixture-legacy.jsonl", build_legacy())
    print(f"wrote {p_modern}\nwrote {p_legacy}\n")

    print("== fixture-modern (token_usage_record carrier) ==")
    st = derive(read(p_modern))
    e = EXPECT_MODERN
    failures += not check("input", st["input"], e["input"])
    failures += not check("cached", st["cached"], e["cached"])
    failures += not check("output", st["output"], e["output"])
    failures += not check("total == input+output", st["total"], e["total"])
    failures += not check("total != input+cached+output",
                          st["total"] == st["input"] + st["cached"] + st["output"], False)
    failures += not check("uncached", st["input"] - st["cached"], e["uncached"])
    failures += not check("cache percent", round(st["cached"] / st["input"] * 100, 10),
                          round(e["cache_percent"], 10))
    failures += not check("cache display", round(st["cached"] / st["input"] * 100), e["cache_display"])
    failures += not check("display total", fmt(st["total"]), e["display_total"])
    failures += not check("turn A model_ms", st["per_turn_ms"][TURN_A], e["turn_a_model_ms"])
    failures += not check("turn B model_ms", st["per_turn_ms"][TURN_B], e["turn_b_model_ms"])
    failures += not check("excluded tool ms", st["excluded_ms"], e["excluded_tool_ms"])
    failures += not check("turn A tps", round(st["tpss"][TURN_A], 6), e["turn_a_tps"])
    failures += not check("turn B tps", round(st["tpss"][TURN_B], 6), e["turn_b_tps"])
    failures += not check("last turn tps", round(st["tpss"][st["turn_order"][-1]], 6), e["last_turn_tps"])
    print(f"    note: a denominator that wrongly included the 5000 ms tool call would give "
          f"turn A tps = {1400 / 12000 * 1000:.1f}, not 200.0")

    print("\n== fixture-legacy (token_count carrier only, Codex 0.151) ==")
    st2 = derive(read(p_legacy))
    e2 = EXPECT_LEGACY
    failures += not check("input", st2["input"], e2["input"])
    failures += not check("cached", st2["cached"], e2["cached"])
    failures += not check("output", st2["output"], e2["output"])
    failures += not check("total == input+output", st2["total"], e2["total"])
    failures += not check("uncached", st2["input"] - st2["cached"], e2["uncached"])
    failures += not check("cache display", round(st2["cached"] / st2["input"] * 100), e2["cache_display"])
    failures += not check("display total", fmt(st2["total"]), e2["display_total"])
    failures += not check("input + cached + output (WRONG, must differ)",
                          st2["input"] + st2["cached"] + st2["output"], e2["wrong_formula"])
    failures += not check("no TPS measurable -> None", st2["tpss"], {})

    print("\n== formatting boundaries ==")
    failures += not check("843", fmt(843), "843 tok")
    failures += not check("999", fmt(999), "999 tok")
    failures += not check("1000", fmt(1000), "1.0K tok")
    failures += not check("12400", fmt(12_400), "12.4K tok")
    failures += not check("999999", fmt(999_999), "1000.0K tok")  # documented edge: rounds up
    failures += not check("1000000", fmt(1_000_000), "1.0M tok")
    failures += not check("5699084", fmt(5_699_084), "5.7M tok")
    failures += not check("unknown", fmt(None), "-- tok")

    print()
    if failures:
        print(f"RESULT: {failures} FAILURE(S)")
        return 1
    print("RESULT: all fixture expectations passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())