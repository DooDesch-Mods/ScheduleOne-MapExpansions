"""
In-game check: a world state's geometry is edited and rebuilt while the game runs (MapExpansions Editor/StateEdits.cs,
Lifecycle/StateLayers.cs, WorldStates.Restate), and the saved layer applies on the next start.

Uses Based's Sweatshop expansion as the host. Needs Debug builds of MapExpansions with its editor, Based, and the
ScheduleMCP bridge in the slot. The test moves one copy of a state, rebuilds the state live, proves the position with
find_world_objects, saves the layer, restarts the slot with session.ps1 -KeepSlot and checks that the layer file
put the copy at the moved spot. The layer file of the expansion is put back as it was at the end, unless --keep.

    python _wt-MapExpansions-state-editor/Tests/state-live-edit.py [--slot mptest] [--shots DIR] [--no-restart] [--keep]
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time


def _bridge():
    folder = os.path.dirname(os.path.abspath(__file__))
    for _ in range(6):
        candidate = os.path.join(folder, "Workspace", "tools", "bridge")
        if os.path.isfile(os.path.join(candidate, "s1.py")):
            return candidate
        folder = os.path.dirname(folder)
    raise SystemExit("[state-live-edit] Workspace/tools/bridge/s1.py not found above " + os.path.dirname(os.path.abspath(__file__)))


BRIDGE = _bridge()
sys.path.insert(0, BRIDGE)
from s1 import Game  # noqa: E402

EXPANSION = "based_ming_upper"
STATE = "frame"
COPY = "barrels"
# The renderer the barrels copy is built from; find_world_objects matches renderers and colliders by name.
MESH = "SM_Prop_BarrelStack_01"
DELTA = (0.0, 0.0, 1.2)
# Looking south over the upper floor of the new wing; the barrels stand right of the middle, the move brings them closer.
EYE = (-57.2, 2.45, 144.6)
TARGET = (-58.6, -0.2, 141.6)
TOLERANCE = 0.05


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

    def cmd(self, command, max_wait_ms=15000):
        """Runs a console command and returns the log lines it wrote."""
        answer = self.g.run(command, max_wait_ms=max_wait_ms)
        if not answer.get("ok"):
            print("      %s: the bridge answered %s" % (command, json.dumps(answer)[:300]), flush=True)
        lines = (answer.get("result") or {}).get("lines") or []
        return ["[%s] [%s] %s" % (l.get("level"), l.get("source"), l.get("text")) for l in lines]

    def find(self):
        """Positions of the barrels copy's renderers under the expansion root."""
        answer = self.g.call("find_world_objects", {"name": MESH, "exact": True, "kind": "renderer",
                                                    "under": "MapExpansions." + EXPANSION, "limit": 20}, timeout_ms=30000)
        objects = (answer.get("result") or {}).get("objects") or []
        return [tuple(o["position"]) for o in objects if "position" in o]

    def shot(self, folder, name):
        if not folder:
            return
        try:
            answer = self.g.call("camera_shot", {"eye": list(EYE), "target": list(TARGET), "fov": 75, "hideHud": True},
                                 timeout_ms=15000)
            path = (answer.get("result") or {}).get("path")
            if not path or not os.path.isfile(path):
                print("      screenshot %s: the game returned no file (%s)" % (name, answer), flush=True)
                return
            shutil.copyfile(path, os.path.join(folder, name))
            print("      screenshot %s from %s" % (name, path), flush=True)
        except Exception as e:  # noqa: BLE001 - a missing picture is reported, the checks go on
            print("      screenshot %s failed: %s" % (name, e), flush=True)


def copy_position(lines):
    """The world position of the barrels copy from the [state] copy line of `mapedit state <expansion>`."""
    key = "%s/state:%s/%s:" % (EXPANSION, STATE, COPY)
    for line in lines:
        if key in line:
            m = re.search(r"at \((-?[0-9.]+), (-?[0-9.]+), (-?[0-9.]+)\)", line)
            if m:
                return tuple(float(v) for v in m.groups())
    return None


def near(a, b, tol=TOLERANCE):
    return a is not None and b is not None and all(abs(x - y) <= tol for x, y in zip(a, b))


def has(lines, text):
    return any(text in l for l in lines)


def game_errors(lines):
    """Error lines of the game itself; Unity's navmesh builder reports unreadable source meshes on every state bake."""
    return [l for l in lines if l.startswith("[error] [Unity]")]


def first(lines, text):
    return next((l for l in lines if text in l), "")


def describe(run):
    run.cmd("mapedit state %s %s" % (EXPANSION, STATE))
    return copy_position(run.cmd("mapedit state " + EXPANSION))


def restart(run):
    """Restarts the slot as it is; session.ps1 stops the running game of this session's lease first."""
    root = os.path.dirname(os.path.dirname(os.path.dirname(BRIDGE)))
    script = os.path.join(root, "Workspace", "tools", "session", "session.ps1")
    started = time.time()
    proc = subprocess.run(["pwsh", "-NoProfile", "-File", script, "-Slot", run.slot, "-KeepSlot", "-Save", "5", "-NoApp",
                           "-Purpose", "state-live-edit"], capture_output=True, text=True, encoding="utf-8", errors="replace")
    tail = [l for l in proc.stdout.splitlines() if l.strip()][-3:]
    print("      session.ps1 exit %d after %.0f s: %s" % (proc.returncode, time.time() - started, " | ".join(tail)), flush=True)
    if proc.returncode != 0:
        print(proc.stdout[-3000:], proc.stderr[-2000:], sep="\n", flush=True)
    run.g = Game(run.slot)
    return proc.returncode == 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default="mptest")
    ap.add_argument("--shots", default="")
    ap.add_argument("--no-restart", action="store_true", help="skip the restart and the checks after it")
    ap.add_argument("--keep", action="store_true", help="leave the saved layer file in the slot")
    args = ap.parse_args()
    run = Run(args.slot)
    if args.shots:
        os.makedirs(args.shots, exist_ok=True)

    layer_file = os.path.join(run.g.root, "Mods", EXPANSION, "map.json")
    backup = None
    if os.path.isfile(layer_file):
        with open(layer_file, "rb") as handle:
            backup = handle.read()
    print("      layer file %s %s" % (layer_file, "exists, kept for the end" if backup is not None else "does not exist yet"), flush=True)

    try:
        # 1. Set the state; it becomes the edit target.
        log = run.cmd("mapedit state %s %s" % (EXPANSION, STATE))
        run.check("state set and edit target", has(log, "stands in '%s'; edit target '%s/%s'" % (STATE, EXPANSION, STATE)),
                  first(log, "[state] state"))
        for line in game_errors(log):
            print("      game error while the state applied: %s" % line.splitlines()[0], flush=True)
        before = copy_position(run.cmd("mapedit state " + EXPANSION))
        run.check("state copy listed with its position", before is not None, before)
        if before is None:
            return run.summary()
        moved = tuple(round(b + d, 3) for b, d in zip(before, DELTA))
        found = run.find()
        run.check("find: the copy stands at the listed spot", any(near(p, before) for p in found), found)
        run.shot(args.shots, "state-before.png")

        # 2. Move it over the console, then rebuild the state live.
        log = run.cmd("mapedit select %s/state:%s/%s" % (EXPANSION, STATE, COPY))
        run.check("select the state copy", not has(log, "no placed copy"), first(log, "select"))
        log = run.cmd("mapedit move %g %g %g" % DELTA)
        run.check("move logged", has(log, "verschoben"), first(log, "verschoben"))
        log = run.cmd("mapedit restate", max_wait_ms=30000)
        run.check("restate rebuilt the state", has(log, "[state] restate (console): '%s' rebuilt in '%s'" % (EXPANSION, STATE)),
                  first(log, "[state] restate"))
        errors = [l for l in log if "MapExpansions" in l.split("] ", 2)[1] and l.startswith("[error]") or "Exception" in l]
        run.check("restate without MapExpansions errors or exceptions", not errors, errors[:3])
        for line in game_errors(log):
            print("      game error during restate: %s" % line.splitlines()[0], flush=True)
        after = copy_position(run.cmd("mapedit state " + EXPANSION))
        run.check("listed position moved by %s" % (DELTA,), near(after, moved), "%s, expected %s" % (after, moved))
        found = run.find()
        run.check("find: the copy stands at the moved spot", any(near(p, moved) for p in found), found)
        run.check("find: nothing left at the old spot", not any(near(p, before) for p in found), found)
        run.shot(args.shots, "state-after.png")

        # 3. A wrong state names what exists.
        log = run.cmd("mapedit state %s nosuch" % EXPANSION)
        run.check("unknown state is refused with the states it has", has(log, "has no state 'nosuch'"), first(log, "nosuch"))

        # 4. Save the layer; the file holds the state section with the copy.
        log = run.cmd("mapedit save")
        run.check("save logged", has(log, "saved (console): '%s' layer" % EXPANSION), first(log, "save"))
        entry = None
        try:
            with open(layer_file, encoding="utf-8") as handle:
                saved = json.load(handle)
            copies = (((saved.get("states") or {}).get(STATE) or {}).get("copies") or [])
            entry = next((c for c in copies if c.get("id") == COPY), None)
        except (OSError, ValueError) as e:
            print("      reading %s failed: %s" % (layer_file, e), flush=True)
        run.check("layer file: states.%s.copies has %s with a position" % (STATE, COPY), entry is not None and "at" in entry, entry)

        # 5. Read the file back while the game runs.
        log = run.cmd("mapedit reload", max_wait_ms=30000)
        run.check("reload read the file and rebuilt", has(log, "states read from"), first(log, "[state] reload"))
        run.check("after reload the copy stays moved", near(copy_position(run.cmd("mapedit state " + EXPANSION)), moved))

        if args.no_restart:
            return run.summary()

        # 6. Restart; the layer file applies at the world build.
        if not run.check("restart the slot", restart(run)):
            return run.summary()
        path = run.g.log_path()
        with open(path, encoding="utf-8", errors="replace") as handle:
            boot = handle.read().splitlines()
        resolved = first(boot, "layer (world build): '%s' states resolved" % EXPANSION)
        m = re.search(r"states resolved, (\d+) with a layer section", resolved)
        run.check("world build read the state section", m is not None and int(m.group(1)) >= 1, resolved)
        again = describe(run)
        run.check("after restart the copy stands at the moved spot", near(again, moved), "%s, expected %s" % (again, moved))
        found = run.find()
        run.check("find after restart: the copy stands at the moved spot", any(near(p, moved) for p in found), found)
        run.shot(args.shots, "state-after-restart.png")
        return run.summary()
    finally:
        if not args.keep:
            try:
                if backup is not None:
                    with open(layer_file, "wb") as handle:
                        handle.write(backup)
                    print("      layer file put back as it was", flush=True)
                elif os.path.isfile(layer_file):
                    os.remove(layer_file)
                    print("      layer file removed, as before the test", flush=True)
            except OSError as e:
                print("      putting the layer file back failed: %s" % e, flush=True)


if __name__ == "__main__":
    sys.exit(main())
