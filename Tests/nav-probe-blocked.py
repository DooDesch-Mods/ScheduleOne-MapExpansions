"""
In-game check: an object no property owns must not fail a world state's navigation bake and block saving
(MapExpansions Navigation/PropertyNavigation.cs, BakeState). Uses Based's Sweatshop expansion as the host.

Drives a running test slot over the ScheduleMCP bridge (Workspace/tools/bridge/s1.py). Needs Debug builds of
MapExpansions (`mxwall`) and Based (`baseddiag phase`) in the slot and a save where the player owns the Sweatshop.
It changes the expansion's stage in the save and ends with the expansion complete and no dev walls.

    python _wt-MapExpansions-navprobe/Tests/nav-probe-blocked.py [--slot mptest]
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
    raise SystemExit("[nav-probe-blocked] Workspace/tools/bridge/s1.py not found above " + os.path.dirname(os.path.abspath(__file__)))


sys.path.insert(0, _bridge())
from s1 import Game  # noqa: E402

STATE = "based_ming_upper/complete"
# A wall across the new wing at knee to head height: it cuts probe 2 (-58, 139.5) off from the doorway (z 136.7).
WALL = "-61 -0.25 138.4 -53 2.3 138.6"
DONE = re.compile(r"state nav: '%s' baked|world state '%s' failed" % (re.escape(STATE), re.escape(STATE)))


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

    def lines(self, mark, grep=None):
        path, offset = mark
        now = self.g.log_path()
        if now != path:
            offset = 0
        if not now:
            return []
        with open(now, "rb") as handle:
            handle.seek(offset)
            found = handle.read().decode("utf-8", errors="replace").splitlines()
        if grep:
            rx = re.compile(grep)
            found = [line for line in found if rx.search(line)]
        return found

    def wait_line(self, mark, rx, timeout=20.0):
        end = time.time() + timeout
        while True:
            for line in self.lines(mark):
                if rx.search(line):
                    return line
            if time.time() >= end:
                return None
            time.sleep(0.4)

    def cmd(self, command, wait=0.6):
        mark = self.mark()
        self.g.call("run_console_command", {"command": command, "capture": False})
        time.sleep(wait + 0.4)
        return self.lines(mark)

    def complete_with(self, wall):
        """Moves the build back to the roof stage, places the wall, then completes it. Returns the log since the mark."""
        self.cmd("mxwall off")
        self.cmd("baseddiag phase roof", wait=2.0)
        if wall:
            placed = self.cmd("mxwall " + wall, wait=0.4)
            self.check("dev wall placed (%s)" % wall, any("mxwall placed" in l for l in placed), next((l for l in placed if "mxwall" in l), "no mxwall line"))
        mark = self.mark()
        self.cmd("baseddiag phase complete", wait=0.4)
        done = self.wait_line(mark, DONE)
        self.check("complete state reported", done is not None, done or "no bake or failure line in 20 s")
        return mark

    def save(self):
        mark = self.mark()
        self.g.call("save_now", {}, timeout_ms=30000)
        time.sleep(2.0)
        return self.lines(mark)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default="mptest")
    run = Run(ap.parse_args().slot)

    # 1. A static object no property owns cuts the room in two.
    mark = run.complete_with(WALL)
    log = run.lines(mark)
    failed = [l for l in log if "world state '%s' failed" % STATE in l]
    run.check("static wall: the complete state applies", not failed, failed[0][:200] if failed else "")
    warned = [l for l in log if "no complete path to probe" in l]
    run.check("static wall: the blocked probe is a warning", any("[WARNING]" in l for l in warned), warned[0][:220] if warned else "no probe warning")
    run.check("static wall: the warning names the wall", any("DevWall" in l for l in warned), "")
    saved = run.save()
    blocked = [l for l in saved if "save blocked" in l.lower() or "Save blocked" in l]
    run.check("static wall: saving is not blocked", not blocked, blocked[0][:200] if blocked else "")

    # 2. A loose physics prop of the same size stays out of the bake.
    mark = run.complete_with(WALL + " loose")
    log = run.lines(mark)
    held = next((l for l in log if "loose props" in l and STATE in l), "")
    m = re.search(r"and (\d+) loose props", held)
    run.check("loose wall: kept out of the bake", bool(m) and int(m.group(1)) >= 1, held[:200] or "no hold line")
    run.check("loose wall: every probe connected", any("probes connected" in l and STATE in l for l in log), "")

    # 3. Clean finish: no wall, every probe connected.
    mark = run.complete_with(None)
    run.check("no wall: every probe connected", any("probes connected" in l and STATE in l for l in run.lines(mark)), "")
    saved = run.save()
    run.check("no wall: saving is not blocked", not [l for l in saved if "save blocked" in l.lower()], "")
    return run.summary()


if __name__ == "__main__":
    sys.exit(main())
