"""
Co-op check for the networked door and light switch parts (CopyDefinition with VisualOnly=false; Based#67, #68).

Uses Based's finished Storage Unit: the loft's copied metal door and the three copied wall switches (loft, east, south)
whose lamps follow them through `LightSwitch = "copy:<id>"`. The host opens the door and turns the loft switch off
before the client joins; the client must find both that way (late join). Then the client works the door and the loft
switch, and the host follows; the host works the east switch, and the client follows. Each switch drives only its own
lamps: the other copies' lamps and Unit 1's lamps stay as they were. Prints PASS/FAIL per check.

    pwsh -File Workspace/tools/session/duo.ps1 -Build _wt-MapExpansions-x,Based
    python _wt-MapExpansions-x/Tests/door-switch-coop.py --host mptest2 --client mptest4 [--join browser]
"""
import argparse
import importlib.util
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
    raise SystemExit("[door-switch-coop] Workspace/tools/bridge/s1.py not found above " + os.path.dirname(os.path.abspath(__file__)))


ROOT = _root()
S1 = os.path.join(ROOT, "Workspace", "tools", "bridge", "s1.py")
_spec = importlib.util.spec_from_file_location("folder_join", os.path.join(ROOT, "Based", "tests", "folder-join.py"))
folder_join = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(folder_join)
Log, Run = folder_join.Log, folder_join.Run

PHASES = ["ray.storageunit-loft:complete", "ray.storageunit-east:complete", "ray.storageunit-south:complete"]
# The loft door's hinge, and the roof above it: the game closes an open door when no player is within 40 m.
DOOR_AT = ("-9.0", "4.4", "105.5", "1.5")
ROOF_SPOT = ("-5", "11", "108")
SWITCHES = {
    "loft-switch": ((-8.62, 4.45, 104.5), 3),
    "east-switch": ((.55, 4.45, 103.93), 3),
    "south-switch": ((.55, 4.45, 101.37), 2),
}
# Unit 1's own switch in the hall below; its lamps hang on saved listeners.
UNIT_SWITCH = (-4.62, 1.3, 104.78)
SHOT_DIR = os.path.join(ROOT, "_drafts", "me-board-sweep", "coop")


def command(game, text, until, timeout=30):
    """Runs a console command, waits for the line `until`, and returns the log lines written meanwhile."""
    log = Log(game)
    answer = game.run(text, until=until, max_wait_ms=timeout * 1000)
    if not answer.get("ok", True) and answer.get("error"):
        print("      '%s' on %s: %s" % (text, game.slot if hasattr(game, "slot") else "?", answer.get("error")), flush=True)
    log.wait(until, 3)
    return log.lines()


def door(game, action=""):
    """(open, line) of the one active working door at the loft door, or (None, detail)."""
    lines = command(game, " ".join(["mxdoors", *DOOR_AT, action]).strip(), r"mxdoors .* doors within")
    working = [l for l in lines if "mxdoors working" in l and "active True" in l]
    if len(working) != 1:
        return None, "%d active working doors: %s" % (len(working), [w.split("] ", 2)[-1][:160] for w in working])
    m = re.search(r"open (True|False), handles", working[0])
    return (None if m is None else m.group(1) == "True"), working[0].split("] ", 2)[-1][:260]


def door_settled(game, want, timeout=8):
    end = time.time() + timeout
    state, detail = None, ""
    while time.time() < end:
        state, detail = door(game)
        if state == want:
            break
        time.sleep(1)
    return state, detail


def switch(game, name, action=""):
    """(on, line) of the active copied switch `name`, or (None, detail)."""
    x, y, z = map(str, SWITCHES[name][0])
    lines = command(game, " ".join(["mxswitch", x, y, z, "0.6", action]).strip(), r"mxswitch .* switches within")
    found = [l for l in lines if "mxswitch '" in l and "active True" in l]
    if len(found) != 1:
        return None, "%d active switches: %s" % (len(found), [f.split("] ", 2)[-1][:160] for f in found])
    m = re.search(r"on (True|False), lamps", found[0])
    return (None if m is None else m.group(1) == "True"), found[0].split("] ", 2)[-1][:260]


def switch_settled(game, name, want, timeout=8):
    end = time.time() + timeout
    state, detail = None, ""
    while time.time() < end:
        state, detail = switch(game, name)
        if state == want:
            break
        time.sleep(1)
    return state, detail


def unit_lamps(game):
    """(on, lamps, lit) of Unit 1's switch, read from its listener lamps."""
    x, y, z = map(str, UNIT_SWITCH)
    lines = command(game, "mxswitch %s %s %s 0.3" % (x, y, z), r"mxswitch .* switches within")
    m = re.search(r"on (True|False), .*?listener lamps (\d+) lit (\d+)", "\n".join(lines))
    return (m.group(1) == "True", int(m.group(2)), int(m.group(3))) if m else None


def lamp_lines(lines, name, state=None):
    """How many copied lamps report a switch of `name` (on, off, or either)."""
    rx = re.compile(r"lamps: .* (%s) by 'copy:%s'" % (state or "on|off", re.escape(name)))
    return sum(1 for l in lines if rx.search(l))


def tp(slot, spot):
    subprocess.run([sys.executable, S1, "--slot", slot, "tp", *spot], capture_output=True, text=True)


def shot(slot, name, eye, target):
    out = subprocess.run([sys.executable, S1, "--slot", slot, "cam", "shot", "--eye", *map(str, eye), "--target", *map(str, target),
                          "--hud", "hide", "--settle", "20"], capture_output=True, text=True).stdout
    m = re.search(r'"path":\s*"([^"]+)"', out)
    if not m:
        print("      shot %s failed: %s" % (name, out.strip()[-200:]), flush=True)
        return
    os.makedirs(SHOT_DIR, exist_ok=True)
    target_path = os.path.join(SHOT_DIR, name + ".png")
    with open(m.group(1).replace("\\\\", "\\"), "rb") as src, open(target_path, "wb") as dst:
        dst.write(src.read())
    print("      %s" % target_path, flush=True)


def flip_and_follow(run, actor, actor_name, watcher, watcher_name, name, state):
    """`actor` clicks the copied switch `name` to `state`; both peers must show it, and the watcher's lamps follow."""
    want = state == "on"
    others = [n for n in SWITCHES if n != name]
    watch = Log(watcher)
    unit_before = unit_lamps(watcher)
    switch(actor, name, state)
    a, a_detail = switch_settled(actor, name, want)
    w, w_detail = switch_settled(watcher, name, want)
    run.check("%s turns '%s' %s: %s shows it %s" % (actor_name, name, state, actor_name, state), a == want, a_detail)
    run.check("%s turns '%s' %s: %s shows it %s" % (actor_name, name, state, watcher_name, state), w == want, w_detail)
    time.sleep(1.5)
    lines = watch.lines()
    lamps = SWITCHES[name][1]
    followed = lamp_lines(lines, name, state)
    run.check("%s turns '%s' %s: its %d lamps follow on %s" % (actor_name, name, state, lamps, watcher_name), followed >= lamps,
              "%d lamp lines" % followed)
    stray = {n: lamp_lines(lines, n) for n in others if lamp_lines(lines, n)}
    run.check("%s turns '%s' %s: the other rooms' lamps stay as they were on %s" % (actor_name, name, state, watcher_name), not stray, stray or "none switched")
    unit_after = unit_lamps(watcher)
    run.check("%s turns '%s' %s: Unit 1's lamps stay as they were on %s" % (actor_name, name, state, watcher_name),
              unit_before is not None and unit_after == unit_before, (unit_before, unit_after))


def jump(run, host):
    for phase in PHASES:
        text = "baseddiag site=storageunit phase " + phase
        line = None
        for attempt in range(12):
            lines = command(host, text, r"construction moved to|baseddiag phase failed", 90)
            line = next((l for l in reversed(lines) if "construction moved to" in l or "phase failed" in l), None)
            if line and "construction moved to" in line:
                break
            print("      '%s' not taken yet (attempt %d): %s" % (text, attempt + 1, (line or "no reply")[-160:]), flush=True)
            time.sleep(5)
        if not run.check("host: jump %s" % phase, bool(line and "construction moved to" in line), (line or "")[-200:]):
            return False
    return True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="mptest2")
    parser.add_argument("--client", default="mptest4")
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--join", choices=("locallobby", "browser"), default="locallobby")
    parser.add_argument("--joined", action="store_true", help="the client is already in the host's world")
    args = parser.parse_args()
    run = Run(args.host, args.client)
    host, client = run.host, run.client

    command(host, "settime 1200", r"settime|time", 5)
    if not jump(run, host):
        sys.exit(run.summary())
    time.sleep(6)
    tp(args.host, ROOF_SPOT)
    time.sleep(2)

    # Before the join: the host opens the door and turns the loft switch off.
    state, detail = door(host)
    run.check("host: the loft door is one spawned working door the owner may open",
              state is not None and "spawned True" in detail and "player may open True" in detail, detail)
    door(host, "open")
    state, detail = door_settled(host, True)
    run.check("host opens the loft door before the join", state is True, detail)
    switch(host, "loft-switch", "on")
    host_log = Log(host)
    switch(host, "loft-switch", "off")
    state, detail = switch_settled(host, "loft-switch", False)
    run.check("host turns the loft switch off before the join", state is False, detail)
    time.sleep(1.5)
    run.check("host: the loft lamps go off", lamp_lines(host_log.lines(), "loft-switch", "off") >= SWITCHES["loft-switch"][1],
              "%d lamp lines" % lamp_lines(host_log.lines(), "loft-switch", "off"))
    for name in ("east-switch", "south-switch"):
        switch(host, name, "on")

    if not args.joined:
        # Different Personnel packs hang the join on "synching"; that is the rig, not the door or the switch.
        if not run.same_packs():
            sys.exit(run.summary())
        client_log = Log(client)
        joined = run.join_locallobby(args.timeout) if args.join == "locallobby" else run.join(args.timeout)
        if not joined:
            sys.exit(run.summary())
        run.finish_creator(client_log, 300)
        for name in SWITCHES:
            found = client_log.wait("found the copied switch '%s'" % name, 60)
            run.check("client: lamps find the copied switch '%s'" % name, bool(found), (found or "no line in 60 s").split("] ", 2)[-1][:200])
        time.sleep(2)
        lines = client_log.lines()
        run.check("client: late joiner's loft lamps start off", lamp_lines(lines, "loft-switch", "off") >= SWITCHES["loft-switch"][1]
                  and lamp_lines(lines, "loft-switch", "on") == 0,
                  "off %d, on %d" % (lamp_lines(lines, "loft-switch", "off"), lamp_lines(lines, "loft-switch", "on")))
        run.check("client: late joiner's east lamps start on", lamp_lines(lines, "east-switch", "on") >= SWITCHES["east-switch"][1],
                  "on %d" % lamp_lines(lines, "east-switch", "on"))

    state, detail = switch(client, "loft-switch")
    run.check("client: late joiner finds the loft switch off", state is False, detail)
    state, detail = door(client)
    run.check("client: late joiner finds the loft door open", state is True, detail)
    run.check("client: the door is spawned and the client may open it",
              "spawned True" in detail and "player may open True" in detail, detail)
    shot(args.client, "client-door-open-loft-dark", (-6.6, 4.9, 105.48), (-8.9, 4.4, 105.48))

    # The client works the door; the host follows.
    door(client, "close")
    c, c_detail = door_settled(client, False)
    h, h_detail = door_settled(host, False)
    run.check("client closes the door: closed on the client", c is False, c_detail)
    run.check("client closes the door: closed on the host", h is False, h_detail)
    door(client, "open")
    c, c_detail = door_settled(client, True)
    h, h_detail = door_settled(host, True)
    run.check("client opens the door: open on the client", c is True, c_detail)
    run.check("client opens the door: open on the host", h is True, h_detail)

    # The client works the loft switch, the host the east switch.
    flip_and_follow(run, client, "client", host, "host", "loft-switch", "on")
    flip_and_follow(run, client, "client", host, "host", "loft-switch", "off")
    flip_and_follow(run, host, "host", client, "client", "east-switch", "off")
    flip_and_follow(run, host, "host", client, "client", "east-switch", "on")
    flip_and_follow(run, client, "client", host, "host", "loft-switch", "on")

    # Unit 1's own switch leaves every copy alone.
    host_log = Log(host)
    before = unit_lamps(host)
    x, y, z = map(str, UNIT_SWITCH)
    command(client, "mxswitch %s %s %s 0.3 off" % (x, y, z), r"mxswitch .* switches within")
    time.sleep(2)
    after = unit_lamps(host)
    lines = host_log.lines()
    copied = sum(lamp_lines(lines, n) for n in SWITCHES)
    run.check("client turns Unit 1's switch off: its lamps go out on the host", after is not None and after[2] == 0, (before, after))
    run.check("client turns Unit 1's switch off: no copied lamp switches on the host", copied == 0, "%d lamp lines" % copied)
    command(client, "mxswitch %s %s %s 0.3 on" % (x, y, z), r"mxswitch .* switches within")
    time.sleep(2)
    back = unit_lamps(host)
    run.check("client turns Unit 1's switch back on", back is not None and before is not None and back[2] == before[2], (before, back))

    # The host closes the door; the client follows.
    door(host, "close")
    h, h_detail = door_settled(host, False)
    c, c_detail = door_settled(client, False)
    run.check("host closes the door: closed on the host", h is False, h_detail)
    run.check("host closes the door: closed on the client", c is False, c_detail)
    shot(args.host, "host-door-closed-loft-lit", (-6.6, 4.9, 105.48), (-8.9, 4.4, 105.48))
    sys.exit(run.summary())


if __name__ == "__main__":
    main()
