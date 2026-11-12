"""
In-game check: a property extension with GridOrigin builds a grid of its own, items placed on it stand at the new
floor's height, employees reach them, and they load again on that grid after save and reload.

Uses Based's Storage Unit loft (extension 'storage-loft-floor', state 'a-complete' of 'based_storage_upper'), whose
grid lies 3.15 m above the unit's own. Needs Debug builds of MapExpansions (`mxreach`) and Based (`baseddiag phase`)
in a running test slot and a save where the player owns the Storage Unit.

    python _wt-MapExpansions-extgrid/Tests/extension-grid.py [--slot mptest] [--after-reload]
"""
import argparse
import importlib.util
import os
import re
import subprocess
import sys
import time

_spec = importlib.util.spec_from_file_location("employee_reach", os.path.join(os.path.dirname(os.path.abspath(__file__)), "employee-reach.py"))
_reach = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_reach)
Run, pick = _reach.Run, _reach.pick

CODE = "storageunit"
PHASE = "ray.storageunit-loft:complete"
LOFT_Y = 3.25
ITEMS = ("smallstoragerack", "dryingrack")


def grid_line(lines):
    return next((l.strip()[-260:] for l in lines if re.search(r"property extension: '.*storage-loft-floor' built grid \d+ of '%s'" % CODE, l)), "")


def grid_index(line):
    m = re.search(r"built grid (\d+) of", line)
    return int(m.group(1)) if m else None


def loft_items(run):
    """(item lines on expansion tiles whose y lies at the loft floor, tile summary line)."""
    log = run.cmd("mxreach %s" % CODE, wait=3.0)
    items = []
    for l in log:
        m = re.search(r"mxreach:   (\S+) at \((-?[\d.]+), (-?[\d.]+), (-?[\d.]+)\) on expansion tiles: vanilla reach (yes|NO)", l)
        if m and abs(float(m.group(3)) - LOFT_Y) < 0.3:
            items.append({"id": m.group(1), "reach": m.group(5) == "yes", "line": l.strip()[-220:]})
    tiles = next((l.strip()[-300:] for l in log if re.search(r"\d+ expansion tiles: \d+ outside", l)), "")
    return items, tiles


def check_loft(run, label):
    items, tiles = loft_items(run)
    print("      " + (tiles or "no tile summary"), flush=True)
    m = re.search(r"(\d+) expansion tiles: (\d+) outside the bounds, (\d+) without a path", tiles or "")
    run.check("%s: every expansion tile lies inside the property bounds" % label, bool(m) and int(m.group(2)) == 0, tiles)
    ids = sorted(i["id"] for i in items)
    run.check("%s: the placed items stand on the loft grid at y %.2f" % (label, LOFT_Y), ids == sorted(ITEMS), ids)
    for i in items:
        run.check("%s: %s on the loft is usable (vanilla reach)" % (label, i["id"]), i["reach"], i["line"])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default="mptest")
    ap.add_argument("--save", type=int, default=5)
    ap.add_argument("--after-reload", action="store_true", help="also save, reload and check again")
    args = ap.parse_args()
    run = Run(args.slot)

    run.cmd("settime 1000", wait=1.0)
    mark = run.mark()
    log = run.cmd("baseddiag site=%s phase %s" % (CODE, PHASE), wait=10.0)
    run.check("loft complete", any("construction moved to" in l for l in log), pick(log, "baseddiag"))
    line = grid_line(run.lines(mark))
    index = grid_index(line)
    if not run.check("the extension built a grid of its own", index is not None, line or "no 'built grid' line"):
        return run.summary()
    print("      " + line, flush=True)

    run.cmd("addemployee botanist %s" % CODE, wait=5.0)
    for item in ITEMS:
        log = run.cmd("mxreach place %s %s wing 0 %d" % (CODE, item, index), wait=2.0)
        built = pick(log, "mxreach place")
        run.check("built %s on grid %d" % (item, index), "mxreach place: built " + item in built and "of grid %d" % index in built, built)
    check_loft(run, "fresh")

    if args.after_reload:
        run.g.call("save_now", {}, timeout_ms=60000)
        time.sleep(3)
        run.g.call("return_to_menu", {}, timeout_ms=60000)
        time.sleep(8)
        mark = run.mark()
        for attempt in range(6):
            reply = run.g.call("load_save", {"slot": args.save}, timeout_ms=60000)
            result = (reply or {}).get("result") or {}
            if reply and reply.get("ok") and result.get("loading"):
                break
            print("      reload: load_save attempt %d not accepted: %s" % (attempt + 1, reply), flush=True)
            time.sleep(5)
        ready, last = run.g.wait_ready(timeout=1200, interval=10, say=lambda s: None)
        if run.check("reload ready", ready, "" if ready else str(last)):
            time.sleep(5)
            line = grid_line(run.lines(mark))
            run.check("after reload: the loft grid is built again with the same index", grid_index(line) == index, line or "no 'built grid' line")
            check_loft(run, "after reload")
    return run.summary()


if __name__ == "__main__":
    sys.exit(main())
