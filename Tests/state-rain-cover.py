"""
In-game check: the roof a world state adds keeps the rain out (MapExpansions World/WeatherCover.cs, state areas).
Uses Based's finished Sweatshop expansion as the host; the game's own rain mask knows nothing of its roof.

Drives a running test slot over the ScheduleMCP bridge (Workspace/tools/bridge/s1.py). Needs Debug builds of
MapExpansions (`mxtp`, `mxcover`) and Based (`baseddiag phase`) in the slot and a save where the player owns the
Sweatshop. It completes the expansion in the save and leaves the weather on heavy rain.

    python _wt-MapExpansions-rain/Tests/state-rain-cover.py [--slot mptest] [--shots DIR]
"""
import argparse
import os
import shutil
import sys
import time


def _bridge():
    folder = os.path.dirname(os.path.abspath(__file__))
    for _ in range(6):
        candidate = os.path.join(folder, "Workspace", "tools", "bridge")
        if os.path.isfile(os.path.join(candidate, "s1.py")):
            return candidate
        folder = os.path.dirname(folder)
    raise SystemExit("[state-rain-cover] Workspace/tools/bridge/s1.py not found above " + os.path.dirname(os.path.abspath(__file__)))


sys.path.insert(0, _bridge())
from s1 import Game  # noqa: E402

# Inside the new wing, beside navigation probe 2, and above its roof.
INSIDE = "-57.5 0 140"
ABOVE = "-57.5 9 140"


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

    def cmd(self, command, wait=0.6):
        mark = self.mark()
        self.g.call("run_console_command", {"command": command, "capture": False})
        time.sleep(wait + 0.4)
        return self.lines(mark)

    def cover(self):
        return next((l for l in self.cmd("mxcover", wait=0.4) if "weather cover:" in l and " at (" in l), "")

    def shot(self, folder, name):
        if not folder:
            return
        try:
            path = self.g.shot()
            if not path or not os.path.isfile(path):
                print("      screenshot %s: the game returned no file (%s)" % (name, path), flush=True)
                return
            shutil.copyfile(path, os.path.join(folder, name))
            print("      screenshot %s from %s" % (name, path), flush=True)
        except Exception as e:  # noqa: BLE001 - a missing picture is reported, the checks go on
            print("      screenshot %s failed: %s" % (name, e), flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default="mptest")
    ap.add_argument("--shots", default="")
    args = ap.parse_args()
    run = Run(args.slot)
    if args.shots:
        os.makedirs(args.shots, exist_ok=True)

    log = run.cmd("baseddiag phase complete", wait=4.0)
    run.check("expansion complete", any("construction moved to complete" in l for l in log), "")
    run.cmd("setweather heavyrain", wait=2.0)
    run.cmd("settime 1200", wait=1.0)

    run.cmd("mxtp " + INSIDE, wait=3.0)
    line = run.cover()
    run.check("inside the wing: a world state area", "in a world state area" in line, line[-200:])
    run.check("inside the wing: under a roof", "under a roof" in line, "")
    run.check("inside the wing: rain off around the camera", "camera effects off" in line, "")
    run.shot(args.shots, "rain-inside.png")

    run.cmd("mxtp " + ABOVE, wait=1.0)
    line = run.cover()
    run.check("above the roof: open sky", "open sky" in line, line[-200:])
    run.check("above the roof: rain on", "camera effects on" in line, "")
    return run.summary()


if __name__ == "__main__":
    sys.exit(main())
