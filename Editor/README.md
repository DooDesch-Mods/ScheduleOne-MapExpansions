# MapExpansions map editor

`MapExpansions.Editor` is a tool for expansion authors and is not part of the package. F4 opens a fly camera over the
loaded world; what an author moves is written into the expansion's JSON layer (`Mods/<id>/map.json`, or the file
`LayerFiles` names in the `MapExpansionsEditor` preferences). The layer format, including the per-state sections, is
described in `Workspace/docs/MapExpansions/world-states.md`, "State layers and live editing".

Every function is also a dev console command, `mapedit <command>`. `mapedit help` lists them. Each command logs its
success, and each refusal or failure with its cause.

## World states

| Command | Effect |
| --- | --- |
| `mapedit state <expansion> <state>` | Applies the state (or `dormant`) and makes it the edit target. Lists its copies with world positions and its rules. |
| `mapedit state <expansion>` | Lists the states, the standing one, the target, and the target's copies and rules. |
| `mapedit select <expansion>/state:<state>/<copy>` | Selects a copy of a state; a bare copy id prefers the edit target's copy. |
| `mapedit move dx dy dz`, `turn <deg>`, `scale <f>`, `set x\|y\|z\|yaw\|scale <v>` | Changes the selection, with undo. |
| `mapedit boxes <copy key>`, `mapedit boxmove dx dy dz` | Lists a copy's boxes; moves the selected box or rule area. |
| `mapedit staterule <key> area x0 y0 z0 x1 y1 z1` / `del` | Sets or removes a rule of the edit target. Not an undo step. |
| `mapedit restate [expansion]` | Lays the unsaved changes over the states in memory and rebuilds the standing state. |
| `mapedit save` | Writes the layer file with all state sections. |
| `mapedit reload [expansion]` | Reads the layer file again and rebuilds the state; refused while changes are unsaved. |

## Building a stage as an agent

One stage, one game session, one slot (`Workspace/tools/session/session.ps1`):

1. `mapedit state <expansion> <state>`. The log lists each copy as `copy <expansion>/state:<state>/<id>: at (x, y, z)`.
2. `mapedit select <expansion>/state:<state>/<id>`, then `mapedit move dx dy dz` or `mapedit set x <value>`.
3. `mapedit restate`. The log reads `[state] restate (console): '<expansion>' rebuilt in '<state>'`.
4. Check it: `python Workspace/tools/bridge/s1.py --slot <n> find <mesh> --under MapExpansions.<expansion>` for the
   position, `s1.py --slot <n> cam shot --eye x y z --target x y z --hud hide` or `cam rig` for the picture. Open each
   picture before the next step.
5. Repeat 2 to 4, then `mapedit save`. The next world build reads the file; `mapedit reload` reads it at once.

`Tests/state-live-edit.py` runs this sequence on Based's Sweatshop stage `frame`, restarts the slot and checks that
the saved layer applies.
