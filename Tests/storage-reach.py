"""
In-game check: storage and drying racks that stand wholly on an expansion's tiles count as usable for employees.

Vanilla uses an object only when NavMeshUtility.GetReachableAccessPoint finds an access point that lies inside the
property bounds (Property.DoBoundsContainPoint) and that the employee's NavMesh path reaches. An object touching the
old room has an access point inside the old bounds; one in the middle of the wing has all of them on the expansion,
so the expansion's bounds box has to cover the whole wing. Uses Based's Sweatshop wing as the expansion.

Needs Debug builds of MapExpansions (`mxreach`) and Based (`baseddiag phase`) in a running test slot.

    python _wt-MapExpansions-storagereach/Tests/storage-reach.py [--slot mptest] [--after-reload]
"""
import argparse
import importlib.util
import os
import re
import sys
import time

_spec = importlib.util.spec_from_file_location("employee_reach", os.path.join(os.path.dirname(os.path.abspath(__file__)), "employee-reach.py"))
_reach = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_reach)
Run, pick, CODE = _reach.Run, _reach.pick, _reach.CODE

ITEMS = ("smallstoragerack", "dryingrack", "smallstoragerack")


def report(run):
    """Returns (item lines on expansion tiles with their access point lines, tile summary line)."""
    log = run.cmd("mxreach %s" % CODE, wait=3.0)
    items, current = [], None
    for l in log:
        if "mxreach:   " in l and " at (" in l and "vanilla reach" in l:
            current = {"line": l.strip()[-260:], "points": [], "wing": "on expansion tiles" in l, "reach": "vanilla reach yes" in l}
            items.append(current)
        elif "mxreach:     point" in l and current is not None:
            current["points"].append(l.strip()[-260:])
        elif "expansion tiles:" in l:
            current = None
    tiles = next((l.strip()[-300:] for l in log if re.search(r"\d+ expansion tiles: \d+ outside", l)), "")
    return items, tiles


def check_report(run, label):
    items, tiles = report(run)
    print("      " + (tiles or "no tile summary"), flush=True)
    m = re.search(r"(\d+) expansion tiles: (\d+) outside the bounds", tiles or "")
    run.check("%s: every expansion tile lies inside the property bounds" % label, bool(m) and int(m.group(2)) == 0, tiles)
    wing = [i for i in items if i["wing"]]
    run.check("%s: the placed objects are on expansion tiles" % label, len(wing) >= len(ITEMS), "%d of %d" % (len(wing), len(items)))
    for i in wing:
        print("      " + i["line"], flush=True)
        for p in i["points"]:
            print("        " + p, flush=True)
        name = re.search(r"mxreach:   (\S+) at", i["line"])
        run.check("%s: %s on the wing is usable (vanilla reach)" % (label, name.group(1) if name else "object"), i["reach"],
                  next((p for p in i["points"] if "OUT" in p or "path NO" in p), i["line"]))


def wing_racks(lines):
    """Sum of input + drying + output over the drying racks on expansion tiles."""
    total = 0
    for l in lines:
        m = re.search(r"drying rack at .* on expansion tiles: input (\d+), drying (\d+), output (\d+)", l)
        if m:
            total += sum(int(g) for g in m.groups())
    return total


def check_work(run, label):
    """The botanist's supplies are a rack on the wing; it has to carry weed from there onto a drying rack on the wing."""
    log = run.cmd("mxreach setup %s wing" % CODE, wait=2.0)
    setup = pick(log, "mxreach setup:")
    run.check("%s: supplies are a rack on the wing" % label, "on expansion tiles" in setup and "drying racks" in setup, setup)
    before = wing_racks(run.cmd("mxreach work %s" % CODE, wait=1.0))
    log = run.cmd("mxreach stock %s ogkush 10" % CODE, wait=1.0)
    stock = pick(log, "mxreach stock")
    run.check("%s: weed put into the wing supplies" % label, "on expansion tiles" in stock, stock)
    now, work = before, []
    deadline = time.time() + 240
    while time.time() < deadline:
        work = run.cmd("mxreach work %s" % CODE, wait=1.0)
        now = wing_racks(work)
        if now > before:
            break
        time.sleep(10)
    for l in work:
        if "mxreach work:" in l and ("active" in l or "drying rack" in l):
            print("      " + l.strip()[-220:], flush=True)
    run.check("%s: the botanist moved weed onto a drying rack on the wing" % label, now > before,
              "wing racks %d -> %d; %s" % (before, now, pick(work, "active")))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default="mptest")
    ap.add_argument("--save", type=int, default=5)
    ap.add_argument("--after-reload", action="store_true", help="also save, reload and check again")
    ap.add_argument("--work", action="store_true",
                    help="also let the botanist carry weed from a supplies rack on the wing onto a drying rack on the wing")
    args = ap.parse_args()
    run = Run(args.slot)

    run.cmd("settime 1000", wait=1.0)
    log = run.cmd("baseddiag phase complete", wait=6.0)
    run.check("wing complete", any("complete" in l for l in log), pick(log, "phase"))
    run.cmd("addemployee botanist %s" % CODE, wait=5.0)
    log = run.cmd("mxreach place %s locker old" % CODE, wait=2.0)
    run.check("built locker (old)", any("mxreach place: built locker" in l for l in log), pick(log, "mxreach place"))
    for item in ITEMS:
        log = run.cmd("mxreach place %s %s wing" % (CODE, item), wait=2.0)
        run.check("built %s (wing)" % item, any("mxreach place: built " + item in l for l in log), pick(log, "mxreach place"))
    log = run.cmd("mxreach setup %s" % CODE, wait=2.0)
    run.check("botanist set up", any("mxreach setup:" in l and "paid for today" in l for l in log), pick(log, "mxreach setup"))

    check_report(run, "fresh")
    if args.work:
        check_work(run, "fresh")

    if args.after_reload:
        mark = run.mark()
        run.g.call("save_now", {}, timeout_ms=60000)
        time.sleep(3)
        run.g.call("return_to_menu", {}, timeout_ms=60000)
        time.sleep(8)
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
            check_report(run, "after reload")
            if args.work:
                check_work(run, "after reload")
    return run.summary()


if __name__ == "__main__":
    sys.exit(main())
