"""
In-game check: a botanist works at a mushroom bed and a spawn station that stand on an expansion's tiles.

Vanilla gives an employee a task only when NavMeshUtility.GetReachableAccessPoint finds an access point inside the
property bounds that its NavMesh path reaches. Uses Based's finished Sweatshop wing as the expansion.

Needs Debug builds of MapExpansions (`mxreach`) and Based (`baseddiag phase`) in a running test slot and a save where
the player owns the Sweatshop. It completes the wing, hires a botanist and builds a locker, a shelf, a bed and a
spawn station in the save.

    python _wt-MapExpansions-botanist/Tests/employee-reach.py [--slot mptest]
"""
import argparse
import os
import re
import sys
import time


def _bridge():
    folder = os.path.dirname(os.path.abspath(__file__))
    for _ in range(6):
        candidate = os.path.join(folder, "Workspace", "tools", "bridge")
        if os.path.isfile(os.path.join(candidate, "s1.py")):
            return candidate
        folder = os.path.dirname(folder)
    raise SystemExit("[employee-reach] Workspace/tools/bridge/s1.py not found above " + os.path.dirname(os.path.abspath(__file__)))


sys.path.insert(0, _bridge())
from s1 import Game  # noqa: E402

CODE = "sweatshop"


class Run:
    def __init__(self, slot):
        for stream in (sys.stdout, sys.stderr):
            stream.reconfigure(encoding="utf-8", errors="replace")
        self.g = Game(slot)
        self.results = []

    def check(self, name, ok, detail=""):
        verdict = "PASS" if ok else "FAIL"
        self.results.append((name, verdict, detail))
        print("%s  %s%s" % (verdict, name, ("  - " + str(detail)) if detail else ""), flush=True)
        return ok

    def summary(self):
        failed = [r for r in self.results if r[1] != "PASS"]
        print("\n===== %d PASS, %d FAIL" % (len(self.results) - len(failed), len(failed)))
        for name, verdict, detail in failed:
            print("%s  %s  - %s" % (verdict, name, detail))
        return 1 if failed else 0

    def mark(self):
        path = self.g.log_path()
        return (path, os.path.getsize(path) if path else 0)

    def lines(self, mark):
        path, offset = mark
        now = self.g.log_path()
        if now != path:
            offset = 0
        if not now:
            return []
        with open(now, "rb") as handle:
            handle.seek(offset)
            return handle.read().decode("utf-8", errors="replace").splitlines()

    def cmd(self, command, wait=1.0, show=False):
        mark = self.mark()
        self.g.call("run_console_command", {"command": command, "capture": False})
        time.sleep(wait + 0.5)
        out = [l for l in self.lines(mark) if "[MapExpansions]" in l or "[Based]" in l or "Console" in l]
        if show:
            for l in out:
                if "[dev] mxreach" in l:
                    print("      " + l.strip()[-260:], flush=True)
        return out


def item_reach(lines, item, expansion):
    """'yes', 'NO' or None for the first object line of this item on (or off) the expansion."""
    for l in lines:
        m = re.search(r"mxreach:   %s at .*?(on expansion tiles)?: vanilla reach (yes|NO)" % re.escape(item), l)
        if m and bool(m.group(1)) == expansion:
            return m.group(2)
    return None


def pick(lines, text):
    return next((l.strip()[-220:] for l in lines if text in l), "")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default="mptest")
    args = ap.parse_args()
    run = Run(args.slot)

    log = run.cmd("baseddiag phase complete", wait=4.0)
    run.check("wing complete", any("construction moved to complete" in l or "already" in l for l in log), pick(log, "phase"))
    run.cmd("settime 1000", wait=1.0)
    run.cmd("addemployee botanist %s" % CODE, wait=5.0)
    for item, where in (("locker", "old"), ("smallstoragerack", "old"), ("mushroombed", "wing"), ("mushroomspawnstation", "wing")):
        log = run.cmd("mxreach place %s %s %s" % (CODE, item, where), wait=2.0)
        run.check("built %s (%s)" % (item, where), any("mxreach place: built " + item in l for l in log), pick(log, "mxreach place"))
    for item, qty in (("mushroomsubstrate", 10), ("grainbag", 5), ("sporesyringe", 5), ("shroomspawn", 5)):
        run.cmd("mxreach stock %s %s %d" % (CODE, item, qty), wait=0.5)
    log = run.cmd("mxreach setup %s" % CODE, wait=2.0)
    run.check("botanist set up", any("mxreach setup:" in l and "paid for today" in l for l in log), pick(log, "mxreach setup"))

    log = run.cmd("mxreach %s" % CODE, wait=2.0, show=True)
    run.check("the bed on the wing is reachable", item_reach(log, "mushroombed", True) == "yes", pick(log, "mushroombed at"))
    run.check("the spawn station on the wing is reachable", item_reach(log, "mushroomspawnstation", True) == "yes", pick(log, "mushroomspawnstation at"))
    run.check("the wing tiles are inside the bounds and reachable", any(" 0 outside the bounds, 0 without a path" in l for l in log), pick(log, "expansion tiles:"))

    time.sleep(15)
    log = run.cmd("mxreach work %s" % CODE, wait=1.0, show=True)
    line = pick(log, "mxreach work:")
    run.check("the botanist works at the wing", "active none" not in line and "nothing for me to do" not in line, line)

    # The report: the botanist never uses the spawn station on the wing. A player fills it by hand; the botanist
    # then inoculates one grain bag per 6 s of work and leaves the spawn in the output slot.
    log = run.cmd("mxreach fill %s 2" % CODE, wait=1.0)
    run.check("grain and syringe put into the station", any("mxreach fill: station" in l for l in log), pick(log, "mxreach fill"))
    used, station = False, ""
    for _ in range(15):
        log = run.cmd("mxreach work %s" % CODE, wait=1.0)
        station = next((l.strip() for l in log if "station mushroomspawnstation" in l), "")
        m = re.search(r"grain (\d+), syringe (\d+), output (\d+)", station)
        if m and (int(m.group(3)) > 0 or int(m.group(1)) < 2):
            used = True
            break
        time.sleep(12)
    print("      " + station, flush=True)
    run.check("the station on the wing is reachable at 0.6 m", "reach at 0.6 m yes" in station, station)
    run.check("the botanist used the station on the wing", used, station)

    # Control: the same bed on the old floor.
    log = run.cmd("mxreach place %s mushroombed old" % CODE, wait=2.0)
    run.cmd("mxreach setup %s" % CODE, wait=1.0)
    log = run.cmd("mxreach %s" % CODE, wait=2.0, show=True)
    run.check("control: the bed on the old floor is reachable", item_reach(log, "mushroombed", False) == "yes", pick(log, "mushroombed at"))
    return run.summary()


if __name__ == "__main__":
    sys.exit(main())
