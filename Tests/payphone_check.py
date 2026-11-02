"""Pay phones of expansions in a running game: replaced, found by the game, ringing, answered.

Needs a Debug build of MapExpansions and an expansion with pay phones (Serena Flats) in a loaded save.

    python -I tests/payphone_check.py --slot mptest2 [--shot]

Prints PASS or FAIL per check and exits 1 when one fails. Leaves no call queued or open behind.
"""
import argparse
import math
import os
import re
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "Workspace", "tools", "bridge"))
import s1  # noqa: E402

PHONE = re.compile(r"\[dev\] mxpayphone: '(?P<path>[^']+)' at \((?P<pos>[^)]+)\) in (?P<exp>\S+), stand (?:\((?P<stand>[^)]+)\)|none), "
                   r"[-\d.]+ m away; components (?P<comps>\S+); queued '(?P<queued>[^']*)', active '(?P<active>[^']*)', "
                   r"light (?P<light>\S+), ring sound (?P<ring>\S+)")

results = []


def check(name, ok, evidence):
    results.append(ok)
    print("%s  %s" % ("PASS" if ok else "FAIL", name))
    for line in evidence if isinstance(evidence, list) else [evidence]:
        print("      " + str(line))


def run(game, command, max_wait=8000):
    answer = game.run(command, max_wait_ms=max_wait, settle_ms=800)
    if not answer.get("ok"):
        raise RuntimeError("%s: %s" % (command, answer))
    return [l["text"] for l in answer["result"].get("lines", [])]


def phones(game):
    found = []
    for text in run(game, "mxpayphone"):
        m = PHONE.search(text)
        if m:
            d = m.groupdict()
            d["pos"] = [float(v) for v in d["pos"].split(",")]
            d["stand"] = [float(v) for v in d["stand"].split(",")] if d["stand"] else None
            d["text"] = text
            found.append(d)
    return found


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--slot", default=None)
    parser.add_argument("--shot", action="store_true", help="screenshot at the first expansion pay phone while it rings")
    a = parser.parse_args()
    game = s1.Game(a.slot) if a.slot else s1.Game()

    # 1. The build replaced every captured pay phone.
    built = game.log(grep="payphones:", lines=40)
    summary = [l for l in built if re.search(r"payphones: '[^']+' \d+/\d+ pay phones work", l)]
    replaced = [l for l in built if "replaced by a working copy" in l]
    full = [l for l in summary if (lambda m: m and m.group(1) == m.group(2) and m.group(1) != "0")(re.search(r"(\d+)/(\d+) pay phones work", l))]
    check("build log: every captured pay phone replaced", bool(full) and len(replaced) > 0, summary + replaced)
    errors = [l for l in built if "ERROR" in l.upper() or "WARN" in l.upper()]
    check("build log: no pay phone warning or error", not errors, errors or "none")

    # 2. The game finds the copies as PayPhone components inside the expansion.
    found = phones(game)
    exp = [p for p in found if p["exp"] != "main"]
    check("FindObjectsOfType<PayPhone> lists expansion phones with the PayPhone script",
          len(exp) > 0 and all("PayPhone" in p["comps"].split(",") for p in exp), [p["text"] for p in exp] or "no expansion phone")
    # The phone models of the expansion hang under a live (active) copy; the stripped visuals are off.
    answer = game.world("find_world_objects", {"name": "PayPhone_low", "under": "MapExpansions.", "kind": "renderer"})
    items = (answer.get("result") or {}).get("objects") or []
    # The source cache holds the captured scene, inactive for the whole session.
    items = [o for o in items if not o.get("path", "").startswith("MapExpansions.SourceCache/")]
    live = [o for o in items if "(stripped)" not in o.get("path", "")]
    stripped = [o for o in items if "(stripped)" in o.get("path", "")]
    check("find PayPhone_low: expansion phone models are active, the stripped visuals are off",
          len(live) > 0 and all(o.get("active") for o in live) and not any(o.get("active") for o in stripped),
          ["%s active=%s" % (o.get("path"), o.get("active")) for o in items] or answer)
    check("main world pay phones still there", any(p["exp"] == "main" for p in found), "%d main phones" % sum(p["exp"] == "main" for p in found))
    if not exp:
        return finish()

    # 3. A queued call reaches the expansion phones and they ring next to the player.
    run(game, "mxpayphone end")
    run(game, "mxpayphone ring")
    target = exp[0]
    px, py, pz = target["pos"]
    sx, _, sz = target["stand"] or [px, py, pz + 1.0]
    yaw = math.degrees(math.atan2(px - sx, pz - sz))
    # The player's position is about a metre above the feet; lower and the player drops through the sidewalk.
    game.world("teleport", {"x": sx, "y": py + 1.0, "z": sz, "yaw": yaw})
    time.sleep(1.0)
    at = game.world("player_position").get("result", {}).get("position") or [0, -1000, 0]
    dist = math.dist(at, [px, py + 1.0, pz])
    check("player stands at the expansion phone", dist < 2.5, "player at %s, %.1f m from the phone" % (at, dist))
    rang, light, queued_ok, samples = False, False, False, []
    for _ in range(8):
        time.sleep(1.0)
        now = [p for p in phones(game) if p["path"] == target["path"]]
        if not now:
            continue
        samples.append(now[0]["text"])
        rang |= now[0]["ring"] == "playing"
        light |= now[0]["light"] == "True"
        queued_ok = now[0]["queued"] == "MapExpansions.TestCall"
    check("expansion phone holds the queued call", bool(samples) and queued_ok, samples[-1:] or "no sample")
    check("expansion phone light blinks", light, samples[-1:] or "no sample")
    check("expansion phone rings next to the player", rang, [s for s in samples if "ring sound playing" in s][:1] or samples[-1:])
    if a.shot:
        # A parked camera a few metres in front of the phone, looking at its handset.
        dx, dz = sx - px, sz - pz
        n = math.hypot(dx, dz) or 1.0
        eye = [px + dx / n * 2.5, py + 1.7, pz + dz / n * 2.5]
        answer = game.world("camera_shot", {"eye": eye, "target": [px, py + 1.2, pz], "settleFrames": 10}, timeout_ms=15000)
        path = (answer.get("result") or {}).get("path")
        check("screenshot taken at the ringing expansion phone", bool(path), path or answer)

    # 4. Answering through the phone's own handler opens the call.
    lines = run(game, "mxpayphone answer exp")
    ans = [l for l in lines if "mxpayphone answer:" in l]
    ok = bool(ans) and target["path"] in ans[0] and "hover message 'Answer phone'" in ans[0] and "call open True" in ans[0] \
        and "active 'MapExpansions.TestCall'" in ans[0]
    check("answering the expansion phone opens the call", ok, ans or lines[-3:])
    run(game, "mxpayphone end")
    after = [p for p in phones(game) if p["path"] == target["path"]]
    check("after the call the phone is quiet", bool(after) and after[0]["queued"] == "none" and after[0]["active"] == "none",
          [p["text"] for p in after])
    return finish()


def finish():
    passed = sum(results)
    print("%d/%d checks passed" % (passed, len(results)))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
