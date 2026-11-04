"""
In-game check: a spawn station on an expansion stays usable after the expansion's navigation changes.

The game caches NavMesh samples (NavMeshUtility.SamplePosition) and clears the cache only on a save load. A sample
taken while the Sweatshop wing is still walled off (phase none) lands 1.5 m away in the old room and keeps that
answer after the wing is finished, and the spawn station allows its access point only 0.6 m from it, so the botanist
never uses the station. MapExpansions clears the cache after each bake; `--keep` switches that off
(`mxreach keepcache on`) to see the behaviour without it: then the last three checks fail.

Needs Debug builds of MapExpansions (`mxreach`) and Based (`baseddiag phase`) in a running test slot, on a save whose
wing is not built yet (save 5 from the template).

    python _wt-MapExpansions-botanist/Tests/stale-sample.py [--slot mptest] [--keep]
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

# The station lands on wing tile 18,9 at (-55.00, -0.25, 140.00) in this save; its access point is 0.75 m in front.
ACCESS = (-55.00, -0.25, 140.75)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default="mptest")
    ap.add_argument("--keep", action="store_true", help="keep the sample cache across bakes, as before the fix")
    ap.add_argument("--phase", default="none", help="construction phase the samples are taken in (none = wing walled off)")
    args = ap.parse_args()
    run = Run(args.slot)

    log = run.cmd("mxreach keepcache %s" % ("on" if args.keep else "off"), wait=0.5)
    run.check("cache mode set", any("mxreach keepcache" in l for l in log), pick(log, "keepcache"))
    run.cmd("settime 1000", wait=1.0)
    log = run.cmd("baseddiag phase %s" % args.phase, wait=6.0)
    print("      " + pick(log, "phase"), flush=True)
    run.cmd("addemployee botanist %s" % CODE, wait=5.0)

    # What any employee query near the future station does while the wing is a building site.
    x, y, z = ACCESS
    samples = []
    for dx, dz in ((0, 0), (0.3, 0), (-0.3, 0), (0, 0.3), (0, -0.3)):
        log = run.cmd("mxreach sample %.2f %.2f %.2f" % (x + dx, y, z + dz), wait=0.3)
        samples.append(pick(log, "mxreach sample"))
    for s in samples:
        print("      " + s, flush=True)
    run.check("the building site answered a sample near the station", any("->" in s and "no NavMesh" not in s for s in samples), samples[0])

    log = run.cmd("baseddiag phase complete", wait=6.0)
    run.check("wing complete", any("construction moved to complete" in l or "already" in l or "complete" in l for l in log), pick(log, "phase"))
    print("      " + (pick(log, "sample cache") or "no cache line in the log"), flush=True)

    # The same query once the wing is walkable: a stale cache answers with the building-site hit.
    log = run.cmd("mxreach sample %.2f %.2f %.2f" % ACCESS, wait=0.3)
    after = pick(log, "mxreach sample")
    print("      after: " + after, flush=True)
    m = re.search(r"\(([\d.]+) m\)", after)
    run.check("after completion the access point samples within 0.6 m", bool(m) and float(m.group(1)) <= 0.6, after)

    # The bed takes tile 16,12 first, so the station lands on 18,9 with its access point at ACCESS.
    for item, where in (("locker", "old"), ("smallstoragerack", "old"), ("mushroombed", "wing"), ("mushroomspawnstation", "wing")):
        log = run.cmd("mxreach place %s %s %s" % (CODE, item, where), wait=2.0)
        run.check("built %s (%s)" % (item, where), any("mxreach place: built " + item in l for l in log), pick(log, "mxreach place"))
    log = run.cmd("mxreach setup %s" % CODE, wait=2.0)
    run.check("botanist set up", any("mxreach setup:" in l and "paid for today" in l for l in log), pick(log, "mxreach setup"))
    log = run.cmd("mxreach fill %s 2" % CODE, wait=1.0)
    run.check("grain and syringe put into the station", any("mxreach fill: station" in l for l in log), pick(log, "mxreach fill"))

    used, station = False, ""
    for _ in range(12):
        log = run.cmd("mxreach work %s" % CODE, wait=1.0)
        station = next((l.strip() for l in log if "station mushroomspawnstation" in l), "")
        m = re.search(r"grain (\d+), syringe (\d+), output (\d+)", station)
        if m and (int(m.group(3)) > 0 or int(m.group(1)) < 2):
            used = True
            break
        time.sleep(12)
    print("      " + station, flush=True)
    log = run.cmd("mxreach %s" % CODE, wait=2.0)
    print("      " + next((l.strip()[-260:] for l in log if "point 0 at (-55.00" in l), "no access point line"), flush=True)
    run.check("the station on the wing is reachable at 0.6 m", "reach at 0.6 m yes" in station, station)
    run.check("the botanist used the station on the wing", used, station)
    return run.summary()


if __name__ == "__main__":
    sys.exit(main())
