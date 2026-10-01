"""Measure rendered text metrics from a screenshot, for typography matching.

Given a PNG and a rectangle, this reports the background colour, the ink mask,
the ink bounding box, per-row ink profile (which reveals cap height, x-height
and descender depth) and the colour of the glyph cores. It can also write a
nearest-neighbour magnified crop so a human can see what was measured.

Nothing here renders anything: it only measures pixels that already exist on
screen, so results are evidence about the real Codex UI rather than a guess.
"""
from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

from PIL import Image


def dominant_border_colour(img: Image.Image, box):
    """Background colour: mode of the 1px frame around the rectangle."""
    x, y, w, h = box
    px = img.load()
    samples = []
    for i in range(x, x + w):
        samples.append(px[i, y])
        samples.append(px[i, y + h - 1])
    for j in range(y, y + h):
        samples.append(px[x, j])
        samples.append(px[x + w - 1, j])
    return Counter(samples).most_common(1)[0][0]


def analyse(img: Image.Image, box, threshold=18):
    x, y, w, h = box
    px = img.load()
    bg = dominant_border_colour(img, box)

    rows = []
    cols = [0] * w
    inks = []
    min_col = None
    max_col = None
    for j in range(y, y + h):
        n = 0
        for i in range(x, x + w):
            p = px[i, j]
            if p[3] == 0:
                continue
            d = max(abs(p[0] - bg[0]), abs(p[1] - bg[1]), abs(p[2] - bg[2]))
            if d > threshold:
                n += 1
                cols[i - x] += 1
                inks.append(p[:3])
                min_col = i if min_col is None else min(min_col, i)
                max_col = i if max_col is None else max(max_col, i)
        rows.append(n)

    top = next((j for j, n in enumerate(rows) if n > 0), None)
    bottom = next((j for j, n in reversed(list(enumerate(rows))) if n > 0), None)

    # inked rows profile, as a compact string
    profile = "".join("#" if n > 6 else ("+" if n > 0 else ".") for n in rows)

    result = {
        "box": list(box),
        "background": bg[:3],
        "ink_pixels": len(inks),
        "ink_top_row": top,
        "ink_bottom_row": bottom,
        "ink_height_px": (bottom - top + 1) if top is not None else 0,
        "ink_left_col": (min_col - x) if min_col is not None else None,
        "ink_right_col": (max_col - x) if max_col is not None else None,
        "ink_width_px": (max_col - min_col + 1) if min_col is not None else 0,
        "row_profile": profile,
        "row_counts": rows,
        "col_profile": "".join("#" if n > 3 else ("+" if n > 0 else ".") for n in cols),
        "col_counts": cols,
    }
    if inks:
        lum = sorted(inks, key=lambda c: 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2])
        core = lum[: max(1, len(lum) // 5)]
        median = sorted(core, key=lambda c: 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2])[len(core) // 2]
        result["ink_core_median"] = list(median)
        result["ink_core_darkest"] = list(lum[0])
        result["ink_core_lightest_of_core"] = list(lum[len(core) - 1])
        # how far the ink is from the background, per channel
        result["contrast"] = [abs(median[k] - bg[k]) for k in range(3)]
    return result


def magnify(img: Image.Image, box, scale, out: Path):
    x, y, w, h = box
    crop = img.crop((x, y, x + w, y + h))
    crop = crop.resize((w * scale, h * scale), Image.NEAREST)
    crop.save(out)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--image", required=True)
    ap.add_argument("--origin", default="0,0", help="screen coords of the image's top-left")
    ap.add_argument("--box", action="append", required=True, metavar="NAME=x,y,w,h",
                    help="box in SCREEN coordinates")
    ap.add_argument("--threshold", type=int, default=18)
    ap.add_argument("--zoom", type=int, default=0, help="write a magnified crop at this scale")
    ap.add_argument("--outdir", default=".")
    ap.add_argument("--json")
    args = ap.parse_args()

    img = Image.open(args.image).convert("RGBA")
    ox, oy = (int(v) for v in args.origin.split(","))

    results = {}
    for spec in args.box:
        name, coords = spec.split("=", 1)
        sx, sy, w, h = (int(v) for v in coords.split(","))
        local = (sx - ox, sy - oy, w, h)
        if local[0] < 0 or local[1] < 0 or local[0] + w > img.width or local[1] + h > img.height:
            print(f"{name}: box {local} outside image {img.size} -- skipped")
            continue
        r = analyse(img, local, args.threshold)
        r["screen_box"] = [sx, sy, w, h]
        results[name] = r
        print(f"-- {name} --")
        print(f"   screen box      {sx},{sy} {w}x{h}   local {local}")
        print(f"   background      rgb{tuple(r['background'])}")
        print(f"   ink bbox rows   {r['ink_top_row']}..{r['ink_bottom_row']}  (height {r['ink_height_px']} px)")
        print(f"   ink bbox cols   {r['ink_left_col']}..{r['ink_right_col']}  (width {r['ink_width_px']} px)")
        print(f"   ink pixels      {r['ink_pixels']}")
        if "ink_core_median" in r:
            print(f"   ink core        median rgb{tuple(r['ink_core_median'])}  darkest rgb{tuple(r['ink_core_darkest'])}")
            print(f"   contrast delta  {r['contrast']}")
        print(f"   row profile     {r['row_profile']}")
        if r["col_profile"] and len(r["col_profile"]) <= 400:
            print(f"   col profile     {r['col_profile']}")
        if args.zoom:
            out = Path(args.outdir) / f"{name}-zoom{args.zoom}x.png"
            magnify(img, local, args.zoom, out)
            print(f"   wrote           {out}")
        print()

    if args.json:
        Path(args.json).write_text(json.dumps(results, indent=2), encoding="utf-8")
        print(f"wrote {args.json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())