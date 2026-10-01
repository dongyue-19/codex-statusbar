import json
import sys

path = sys.argv[1]
d = json.load(open(path, encoding="utf-8"))
cases = d["Cases"]
clipped = [c for c in cases if c.get("TextClipped")]
print("cases:", len(cases), " text-clipped:", len(clipped))
for c in cases:
    if c["Mode"] == "locked" and c["Colours"] == "text only":
        slack = c["Width"] - 1 - c["TextInkRight"]
        print("  {:34} w={:<4} ink={}..{} right slack={}".format(
            c["Name"], c["Width"], c["TextInkLeft"], c["TextInkRight"], slack))
for c in clipped:
    print("  CLIPPED:", c["Name"])