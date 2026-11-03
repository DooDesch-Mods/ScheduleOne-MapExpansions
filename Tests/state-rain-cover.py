"""
In-game check: the roof a world state adds keeps the rain out and muffles its sound (MapExpansions
World/WeatherCover.cs, state areas and the sound blend).
Uses Based's finished Sweatshop expansion as the host; the game's own rain mask knows nothing of its roof.

Drives a running test slot over the ScheduleMCP bridge (Workspace/tools/bridge/s1.py). Needs Debug builds of
MapExpansions (`mxtp`, `mxcover`) and Based (`baseddiag phase`) in the slot and a save where the player owns the
Sweatshop. It completes the expansion in the save and leaves the weather on heavy rain.

    python _wt-MapExpansions-rain/Tests/state-rain-cover.py [--slot mptest] [--shots DIR]
"""
import argparse
import os
import re
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
# The wing's cover rectangle is x -66..-54, z 135..146.
GRID_X = (-64, -61, -58, -55.5)
GRID_Z = (136.5, 139.5, 142.5, 145)


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
    run.check("inside the wing: under a roof", roofed(line), "")
    run.check("inside the wing: rain off around the camera", "camera effects off" in line, "")
    run.shot(args.shots, "rain-inside.png")

    # The sound: the game muffles the weather by its enclosure blend, from its own enclosure boxes only. A grid over the
    # wing finds the covered spots none of them reaches; there the rain played as outside before the fix.
    run.cmd("mxcover enclosures", wait=0.5)
    run.cmd("mxcover sound off", wait=0.5)
    bare = []
    for x in GRID_X:
        for z in GRID_Z:
            run.cmd("mxtp %s 0 %s" % (x, z), wait=2.5)
            line = run.cover()
            s = sound(line)
            covered = "in a world state area" in line and roofed(line)
            print("      %s %s: %s, %s" % (x, z, "covered" if covered else "not covered", s), flush=True)
            if covered and s["own"] is not None and s["own"] < 0.1:
                bare.append((x, z, s))
    run.check("muffling off: covered spots in the wing without a game enclosure", len(bare) > 0,
              "%d spots: %s" % (len(bare), [(x, z) for x, z, _ in bare]))
    run.cmd("mxcover sound on", wait=0.5)
    for x, z, off in bare[:3]:
        run.cmd("mxtp %s 0 %s" % (x, z), wait=2.5)
        on = sound(run.cover())
        run.check("%s %s: the game's enclosure blend follows the roof" % (x, z), on["game"] is not None and on["game"] > 0.9, on)
        run.check("%s %s: the rain is quieter than unmuffled" % (x, z),
                  on["volume"] is not None and off["volume"] is not None and on["volume"] < off["volume"] * 0.8,
                  "volume %s muffled, %s unmuffled" % (on["volume"], off["volume"]))

    run.cmd("mxtp " + ABOVE, wait=2.5)
    line = run.cover()
    run.check("above the roof: open sky", not roofed(line) and ", open sky;" in line, line[-200:])
    run.check("above the roof: rain on", "camera effects on" in line, "")
    above = sound(line)
    run.check("above the roof: the rain sounds as outside", above["game"] is not None and above["game"] < 0.1, above)
    return run.summary()


def roofed(line):
    """Whether the camera spot itself is under a roof; the entity query count later in the line says "under a roof" too."""
    return ", under a roof;" in line


def sound(line):
    """The sound part of an mxcover line: our blend, the game's enclosure blend and the rain's volume."""
    def number(pattern):
        m = re.search(pattern, line)
        return float(m.group(1)) if m else None
    return {"ours": number(r"sound blend ([0-9.]+)"),
            "game": number(r"game enclosure blend ([0-9.]+)"),
            "own": number(r"its own enclosures ([0-9.]+)"),
            "volume": number(r"rain volume ([0-9.]+)")}


if __name__ == "__main__":
    sys.exit(main())
