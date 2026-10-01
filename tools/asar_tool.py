"""Read-only inspection of the Codex Desktop `app.asar` archive.

Parses the asar index (a small JSON header at the front of the file) and either
lists or extracts individual members, so that the UI's own CSS can be read
without scanning the whole 540 MB archive. Nothing is written back into the
package; extracted members go to a scratch directory.

asar layout:
    uint32le  4                     header pickle size (always 4)
    uint32le  headerSize + 4        size of the index block that follows
    uint32le  jsonSize              length of the JSON index string
    bytes     jsonSize              JSON index
    padding to 4-byte alignment, then the concatenated file contents
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

DEFAULT_ASAR = Path(
    r"C:\Program Files\WindowsApps\OpenAI.Codex_26.928.3736.0_x64__2p2nqsd0c76g0"
    r"\app\resources\app.asar"
)


def read_index(path: Path):
    """Return (index, data_start).

    The index is a pickle-framed JSON blob; its recorded length includes the
    4-byte length prefix, so the trailing padding is trimmed with raw_decode
    rather than trusted byte-for-byte.
    """
    with path.open("rb") as fh:
        head = fh.read(16)
        if len(head) < 16:
            raise SystemExit("not an asar archive: too short")
        _pickle, header_size, _pickle2, json_size = struct.unpack("<IIII", head)
        blob = fh.read(json_size)
    text = blob.decode("utf-8", errors="replace")
    index, consumed = json.JSONDecoder().raw_decode(text)
    print(f"index: header_size={header_size} json_size={json_size} consumed={consumed}")
    return index, 8 + header_size


def walk(node, prefix=""):
    for name, entry in node.get("files", {}).items():
        p = f"{prefix}/{name}" if prefix else name
        if "files" in entry:
            yield from walk(entry, p)
        else:
            yield p, entry


def extract(asar: Path, data_start: int, offset: int, size: int) -> bytes:
    with asar.open("rb") as fh:
        fh.seek(data_start + offset)
        return fh.read(size)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--asar", default=str(DEFAULT_ASAR))
    ap.add_argument("--list", metavar="REGEX", help="list member paths matching a regex")
    ap.add_argument("--extract", metavar="REGEX", help="extract matching members")
    ap.add_argument("--out", default="asar-out", help="extraction directory")
    ap.add_argument("--max-bytes", type=int, default=8 * 1024 * 1024)
    args = ap.parse_args()

    asar = Path(args.asar)
    index, data_start = read_index(asar)
    members = list(walk(index))
    print(f"asar     : {asar}")
    print(f"members  : {len(members)}")
    print(f"dataStart: {data_start}")

    import re

    if args.list:
        rx = re.compile(args.list)
        hits = [(p, e) for p, e in members if rx.search(p)]
        print(f"matching {args.list}: {len(hits)}")
        for p, e in hits:
            print(f"  {e.get('size', 0):>12,}  {p}")
        return 0

    if args.extract:
        rx = re.compile(args.extract)
        out = Path(args.out)
        hits = [(p, e) for p, e in members if rx.search(p)]
        print(f"matching {args.extract}: {len(hits)}")
        for p, e in hits:
            size = int(e.get("size", 0))
            if size > args.max_bytes:
                print(f"  SKIP (too big) {size:>12,}  {p}")
                continue
            dest = out / p.replace("/", "_")
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(extract(asar, data_start, int(e["offset"]), size))
            print(f"  -> {size:>12,}  {dest}")
        return 0

    ap.print_help()
    return 0


if __name__ == "__main__":
    sys.exit(main())