"""
In-game check: normal play keeps the log quiet (MapExpansions#59, #60).

- #60: walking in and out of a roofed expansion wing writes the weather cover lines once, not on every change; the
  changes are still counted (`mxcover`).
- #59: Botanists working across the seams of the Sweatshop wing raise no "walking back and forth" warning while they
  get their work done.

Uses Based's finished Sweatshop wing and Based's Botanist layout check (Based/tests/botanist-pots.py) to put two
Botanists to work there. Needs Debug builds of MapExpansions and Based in a running test slot with template
`mrmonkey-dock`:

    pwsh -File Workspace/tools/session/session.ps1 -Build _wt-MapExpansions-x,Based -Template mrmonkey-dock -NoApp
    python _wt-MapExpansions-x/Tests/log-noise.py [--slot mptest] [--minutes 6] [--only cover|pacing|seams]

Prints PASS/FAIL per check.
"""
import argparse
import os
import re
import subprocess
import sys
import time


def _root():
    folder = os.path.dirname(os.path.abspath(__file__))
    for _ in range(6):
        if os.path.isfile(os.path.join(folder, "Workspace", "tools", "bridge", "s1.py")):
            return folder
        folder = os.path.dirname(folder)
    raise SystemExit("[log-noise] Workspace/tools/bridge/s1.py not found above " + os.path.dirname(os.path.abspath(__file__)))


ROOT = _root()
sys.path.insert(0, os.path.join(ROOT, "Workspace", "tools", "bridge"))
from s1 import Game  # noqa: E402

# Inside the Sweatshop's new wing and above its roof (state-rain-cover.py).
INSIDE = "-57.5 0 140"
ABOVE = "-57.5 9 140"
ROUNDS = 6
# One line per kind of change; the first of each kind is still logged.
TRANSITION = re.compile(r"weather cover: (camera under a roof, \d+ rain|camera under open sky, \d+ rain|camera under open sky, the weather sounds"
                        r"|camera under a roof, the weather sounds|\d+ rain and cloud renderers appeared)")
CHANGES = re.compile(r"(\d+) roof changes")
CROSSED = re.compile(r"^\[(\d+):(\d+):(\d+)\.\d+\].*nav link: (.+?) crossed a seam \(")
SELFTEST = re.compile(r"mxpacing: 20 crossings each; worker warned (\d+) times, pacer warned (\d+) times")
# The warning before the fix: 10 crossings of one NPC within a minute.
OLD_BURST, OLD_WINDOW = 10, 60


class Run:
    def __init__(self, slot):
        for stream in (sys.stdout, sys.stderr):
            stream.reconfigure(encoding="utf-8", errors="replace")
        self.slot = slot
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

    def log_file(self):
        latest = os.path.join(self.g.root, "MelonLoader", "Latest.log")
        return latest if os.path.isfile(latest) else self.g.log_path()

    def mark(self):
        log = self.log_file()
        return log, (os.path.getsize(log) if log else 0)

    @staticmethod
    def since(mark):
        log, offset = mark
        if not log:
            return []
        with open(log, encoding="utf-8", errors="replace") as f:
            f.seek(offset)
            return f.read().splitlines()

    def cmd(self, command, wait=0.6):
        mark = self.mark()
        self.g.run(command, max_wait_ms=10000)
        time.sleep(wait)
        return self.since(mark)


def cover(run):
    run.cmd("baseddiag phase complete", wait=4.0)
    run.cmd("setweather heavyrain", wait=2.0)
    run.cmd("settime 1200", wait=1.0)
    # The first change of each kind is logged; settle it before counting.
    run.cmd("mxtp " + ABOVE, wait=3.0)
    run.cmd("mxtp " + INSIDE, wait=3.0)
    run.cmd("mxtp " + ABOVE, wait=3.0)
    before = CHANGES.search(" ".join(run.cmd("mxcover", wait=0.4)))
    mark = run.mark()
    for _ in range(ROUNDS):
        run.cmd("mxtp " + INSIDE, wait=3.0)
        run.cmd("mxtp " + ABOVE, wait=3.0)
    lines = run.since(mark)
    after = CHANGES.search(" ".join(run.cmd("mxcover", wait=0.4)))
    noisy = [l for l in lines if TRANSITION.search(l)]
    run.check("#60: %d walks in and out add no weather cover line to the log" % ROUNDS, not noisy,
              "%d lines, first: %s" % (len(noisy), noisy[0].split("] ", 2)[-1][:160]) if noisy else "none")
    counted = int(after.group(1)) - int(before.group(1)) if before and after else None
    run.check("#60: the roof changes are still counted (mxcover)", counted is not None and counted >= 2 * ROUNDS,
              "%s changes counted" % counted if counted is not None else "no 'roof changes' in mxcover")


def pacing(run):
    """The warning itself: a worker's back-and-forth between two stations stays quiet, a real pacer warns once."""
    m = SELFTEST.search(" ".join(run.cmd("mxpacing", wait=0.4)))
    run.check("#59: a worker crossing one seam between pot and table raises no warning", bool(m) and m.group(1) == "0",
              m.group(0) if m else "no mxpacing line")
    run.check("#59: an NPC crossing one seam back and forth toward one spot warns once", bool(m) and m.group(2) == "1",
              m.group(0) if m else "no mxpacing line")


def seams(run, minutes):
    mark = run.mark()
    script = os.path.join(ROOT, "Based", "tests", "botanist-pots.py")
    print("  running %s for %s minutes" % (os.path.relpath(script, ROOT), minutes), flush=True)
    out = subprocess.run([sys.executable, script, "--slot", run.slot, "--minutes", str(minutes)], capture_output=True, text=True,
                         encoding="utf-8", errors="replace")
    for line in (out.stdout or "").splitlines():
        if line.startswith(("PASS", "FAIL", "=====")):
            print("    botanist-pots: " + line, flush=True)
    lines = run.since(mark)
    crossings = [m for m in (CROSSED.search(l) for l in lines) if m]
    pacing = [l for l in lines if "back and forth" in l]
    run.check("#59: the Botanists crossed the wing's seams while working", len(crossings) >= 10, "%d crossing lines" % len(crossings))
    # Where the old rule would have warned: a minute in which one NPC crossed 10 times.
    times = {}
    for m in crossings:
        times.setdefault(m.group(4), []).append(int(m.group(1)) * 3600 + int(m.group(2)) * 60 + int(m.group(3)))
    bursts = {}
    for npc, ts in times.items():
        start, n = None, 0
        for i, t in enumerate(ts):
            if start is None or t - start > OLD_WINDOW:
                start, n = t, 0
            n += 1
            if n == OLD_BURST:
                bursts[npc] = bursts.get(npc, 0) + 1
    print("  crossings per NPC: %s; minutes in which the old rule would have warned: %s"
          % ({k: len(v) for k, v in times.items()}, bursts or "none"), flush=True)
    run.check("#59: no 'walking back and forth' warning while they work", not pacing,
              "%d warnings, first: %s" % (len(pacing), pacing[0].split("] ", 2)[-1][:200]) if pacing else "none")
    working = [l for l in (out.stdout or "").splitlines() if "keeps harvesting" in l]
    run.check("#59: the Botanists get their work done", working and all(l.startswith("PASS") for l in working),
              "; ".join(l[:120] for l in working) or "botanist-pots exit %d, no harvest check" % out.returncode)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default="mptest")
    ap.add_argument("--minutes", type=float, default=6)
    ap.add_argument("--only", default="")
    args = ap.parse_args()
    run = Run(args.slot)
    if args.only in ("", "cover"):
        cover(run)
    if args.only in ("", "pacing", "seams"):
        pacing(run)
    if args.only in ("", "seams"):
        seams(run, args.minutes)
    return run.summary()


if __name__ == "__main__":
    sys.exit(main())
