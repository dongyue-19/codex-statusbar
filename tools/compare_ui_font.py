"""Compare candidate Windows UI fonts against type measured from a real screenshot.

The target numbers come from `measure_native_type.py` run on a capture of the
Codex composer toolbar. This script renders the same string with FreeType at
the same pixel size in each candidate font/weight and reports the ink extents,
so the choice of family and size is decided by matching pixels rather than by
assuming that "Segoe UI" means one particular file.

FreeType's antialiasing differs from Chromium's, but advance widths, ink width
and ink height are font metrics and agree closely enough to discriminate
between candidates separated by one pixel of em size.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

FONTS = {
    "SegoeUI-Regular": r"C:\Windows\Fonts\segoeui.ttf",
    "SegoeUI-Semibold": r"C:\Windows\Fonts\segoeuib.ttf",
    "SegoeUI-Light": r"C:\Windows\Fonts\segoeuil.ttf",
    "SegUIVar": r"C:\Windows\Fonts\SegUIVar.ttf",
}


def ink_extents(img: Image.Image, threshold=18):
    px = img.load()
    w, h = img.size
    bg = px[w - 1, h - 1][:3]
    min_x = min_y = None
    max_x = max_y = None
    count = 0
    darkest = None
    for y in range(h):
        for x in range(w):
            p = px[x, y]
            if max(abs(p[k] - bg[k]) for k in range(3)) > threshold:
                count += 1
                min_x = x if min_x is None else min(min_x, x)
                max_x = x if max_x is None else max(max_x, x)
                min_y = y if min_y is None else min(min_y, y)
                max_y = y if max_y is None else max(max_y, y)
                lum = 0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2]
                if darkest is None or lum < darkest[0]:
                    darkest = (lum, p[:3])
    if min_x is None:
        return None
    return {
        "ink_width": max_x - min_x + 1,
        "ink_height": max_y - min_y + 1,
        "ink_top": min_y,
        "ink_left": min_x,
        "ink_pixels": count,
        "darkest": list(darkest[1]),
    }


def render(font_path: str, px_size: float, text: str, axes=None, fg=(26, 28, 31)):
    size = int(round(px_size * 4))  # render 4x then no downsample: metrics are scale-free
    font = ImageFont.truetype(font_path, size)
    if axes:
        try:
            font.set_variation_by_axes(axes)
        except Exception as exc:  # pragma: no cover - depends on font
            print(f"    (axes {axes} unsupported: {exc})")
    img = Image.new("RGBA", (size * len(text) + 200, size * 3), (255, 255, 255, 255))
    d = ImageDraw.Draw(img)
    d.text((50, 50), text, font=font, fill=fg + (255,))
    ext = ink_extents(img)
    if ext:
        for k in ("ink_width", "ink_height", "ink_top", "ink_left"):
            ext[k] = ext[k] / 4.0
    return ext, font


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--text", default="deepseek-v4.1-flash")
    ap.add_argument("--dip-size", type=float, action="append", default=None,
                    help="font size in DIP; repeatable")
    ap.add_argument("--scale", type=float, default=1.5, help="display scaling")
    ap.add_argument("--target", help="JSON from measure_native_type.py")
    ap.add_argument("--target-key", help="which box in that JSON is the reference")
    ap.add_argument("--json")
    args = ap.parse_args()

    sizes = args.dip_size or [12.0, 13.0, 14.0]
    axes_by_font = {
        "SegUIVar": [(400,), (430,)],
        "SegoeUI-Regular": [None],
        "SegoeUI-Semibold": [None],
        "SegoeUI-Light": [None],
    }

    target = None
    if args.target and args.target_key:
        data = json.loads(Path(args.target).read_text(encoding="utf-8"))
        target = data[args.target_key]
        print(f"TARGET ({args.target_key}): ink {target['ink_width_px']}x{target['ink_height_px']} px, "
              f"rows {target['ink_top_row']}..{target['ink_bottom_row']}, "
              f"core rgb{tuple(target.get('ink_core_median', []))}, "
              f"darkest rgb{tuple(target.get('ink_core_darkest', []))}")
        print(f"        row profile {target['row_profile']}")
        print()

    results = []
    for name, path in FONTS.items():
        if not Path(path).exists():
            print(f"{name}: missing {path}")
            continue
        for axes in axes_by_font.get(name, [None]):
            for dip in sizes:
                px = dip * args.scale
                ext, font = render(path, px, args.text, axes)
                if ext is None:
                    continue
                rec = {
                    "font": name, "axes": list(axes) if axes else None,
                    "dip": dip, "px": px, **ext,
                }
                if target:
                    rec["width_delta"] = round(ext["ink_width"] - target["ink_width_px"], 2)
                    rec["height_delta"] = round(ext["ink_height"] - target["ink_height_px"], 2)
                results.append(rec)

    hdr = f"{'font':<18}{'axes':<10}{'DIP':>6}{'px':>7}{'inkW':>9}{'inkH':>8}{'top':>7}{'darkest':>18}"
    if target:
        hdr += f"{'dW':>8}{'dH':>7}"
    print(hdr)
    print("-" * len(hdr))
    for r in sorted(results, key=lambda r: (abs(r.get("width_delta", 0)), r["dip"])):
        line = (f"{r['font']:<18}{str(r['axes'] or '-'):<10}{r['dip']:>6.1f}{r['px']:>7.2f}"
                f"{r['ink_width']:>9.2f}{r['ink_height']:>8.2f}{r['ink_top']:>7.2f}"
                f"{str(tuple(r['darkest'])):>18}")
        if target:
            line += f"{r['width_delta']:>8.2f}{r['height_delta']:>7.2f}"
        print(line)

    if args.json:
        Path(args.json).write_text(json.dumps({"target": target, "candidates": results}, indent=2), encoding="utf-8")
        print(f"\nwrote {args.json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())