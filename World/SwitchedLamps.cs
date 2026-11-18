using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Misc;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Lets the lamps of a copy follow a wall switch: one of the main world, or a networked copy of one ("copy:&lt;id&gt;").
    /// The switch only knows the lamps it was built with, and it is networked by the game, so every peer reads its
    /// state and switches its own copy.
    /// </summary>
    internal static class SwitchedLamps
    {
        private const float Interval = .2f;
        private const string IndependentVisual = "MapExpansions independent visual";
        internal const string CopyPrefix = "copy:";
        /// <summary>How long lamps wait for their copied switch before the log says it has not arrived.</summary>
        private const float CopyWaitWarning = 30f;

        internal sealed class Surface
        {
            public MeshRenderer Renderer;
            public int Index;
            public Material On, Off;
        }

        private sealed class Group
        {
            public string Name, Path;
            public GameObject Owner;
            public ModularSwitch Switch;
            public Light[] Lights;
            public List<Surface> Surfaces;
            public bool? Last;
            /// <summary>Set for a copied switch: where to look for it, and since when the lamps wait for it.</summary>
            public string Expansion, StateOwner, CopyId;
            public float WaitingSince;
            public bool Warned;
        }

        private static readonly List<Group> Groups = new();
        private static float _next;

        /// <summary>What glows in the copy, read while its lamp scripts are still on it.</summary>
        internal static List<Surface> Capture(GameObject copy)
        {
            var surfaces = new List<Surface>();
            foreach (var lamp in copy.GetComponentsInChildren<ToggleableLight>(true))
            {
                var meshes = lamp.lightSurfacesMeshes;
                if (meshes == null) continue;
                foreach (var mesh in meshes)
                    if (mesh != null) surfaces.Add(new Surface { Renderer = mesh, Index = lamp.MaterialIndex, On = lamp.lightOnMat, Off = lamp.lightOffMat });
            }
            return surfaces;
        }

        internal static void Follow(string name, string expansion, string owner, GameObject copy, string path, List<Surface> surfaces)
        {
            ModularSwitch modular = null;
            string copyId = null;
            if (path.StartsWith(CopyPrefix, StringComparison.Ordinal))
            {
                copyId = path.Substring(CopyPrefix.Length).Trim();
                if (copyId.Length == 0)
                {
                    Core.Log.Error($"lamps: {name}: light switch '{path}' names no copy; its lamps keep their state");
                    return;
                }
            }
            else
            {
                var target = MainCopies.FindMain(path);
                modular = target != null ? target.GetComponent<ModularSwitch>() : null;
                if (modular == null)
                {
                    Core.Log.Error($"lamps: {name}: no ModularSwitch at '{path}'; its lamps keep their state");
                    return;
                }
            }
            // A batched renderer was replaced by its independent child and draws nothing itself anymore.
            foreach (var surface in surfaces)
                if (!surface.Renderer.enabled && surface.Renderer.transform.Find(IndependentVisual) is Transform child && child.GetComponent<MeshRenderer>() is MeshRenderer visual)
                    surface.Renderer = visual;
            var lights = copy.GetComponentsInChildren<Light>(true);
            if (lights.Length == 0 && surfaces.Count == 0)
                Core.Log.Warning($"lamps: {name}: the copy has nothing to switch");
            Groups.Add(new Group
            {
                Name = name, Path = path, Owner = copy, Switch = modular, Lights = lights, Surfaces = surfaces,
                Expansion = copyId == null ? null : expansion, StateOwner = owner, CopyId = copyId, WaitingSince = Time.unscaledTime
            });
            Core.Log.Msg(copyId == null
                ? $"lamps: {name} follows '{path}' with {lights.Length} lights and {surfaces.Count} glowing surfaces"
                : $"lamps: {name} follows the copied switch '{copyId}' with {lights.Length} lights and {surfaces.Count} glowing surfaces, once it is spawned");
        }

        internal static void Tick()
        {
            if (Groups.Count == 0 || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;
            for (int i = Groups.Count - 1; i >= 0; i--)
            {
                var group = Groups[i];
                if (group.Owner == null)
                {
                    Core.Log.Msg($"lamps: {group.Name} is gone and no longer follows '{group.Path}'");
                    Groups.RemoveAt(i);
                    continue;
                }
                if (group.Switch == null && group.CopyId != null)
                {
                    if (group.Last != null)
                    {
                        Core.Log.Msg($"lamps: the copied switch '{group.CopyId}' of {group.Name} is gone; its lamps wait for it to be spawned again");
                        group.Last = null;
                        group.WaitingSince = Time.unscaledTime;
                    }
                    if (!FindCopiedSwitch(group)) continue;
                }
                else if (group.Switch == null)
                {
                    Core.Log.Warning($"lamps: the switch '{group.Path}' of {group.Name} is gone; its lamps keep their state");
                    Groups.RemoveAt(i);
                    continue;
                }
                bool on = group.Switch.isOn;
                if (group.Last == on) continue;
                group.Last = on;
                Apply(group, on);
                Core.Log.Msg($"lamps: {group.Name} {(on ? "on" : "off")} by '{group.Path}'");
            }
        }

        /// <summary>
        /// A copied switch is a networked part: it stands only once the host has spawned it, and a state switch or a
        /// rejoin spawns it anew. The lamps look for it until it is there, and again whenever it is gone.
        /// </summary>
        private static bool FindCopiedSwitch(Group group)
        {
            var modular = MainCopies.SwitchOfCopy(group.Expansion, group.CopyId);
            if (modular == null)
            {
                if (!group.Warned && Time.unscaledTime - group.WaitingSince > CopyWaitWarning)
                {
                    group.Warned = true;
                    Core.Log.Warning($"lamps: {group.Name}: the copied switch '{group.CopyId}' has not arrived after {CopyWaitWarning:0} s; its lamps keep their state until it does");
                }
                return false;
            }
            group.Switch = modular;
            group.Last = null;
            group.Warned = false;
            Core.Log.Msg($"lamps: {group.Name} found the copied switch '{group.CopyId}' at {modular.transform.position}");
            return true;
        }

        private static void Apply(Group group, bool on)
        {
            // A vanilla lamp's OptimizedLight rewrites light.enabled from its own Enabled flag on every player
            // movement step, so a lamp switched off only through the Light comes back on as soon as the player walks.
            foreach (var light in group.Lights)
            {
                if (light == null) continue;
                var optimized = light.GetComponent<OptimizedLight>();
                if (optimized != null) optimized.Enabled = on;
                else light.enabled = on;
            }
            foreach (var surface in group.Surfaces)
            {
                var material = on ? surface.On : surface.Off;
                if (surface.Renderer == null || material == null) continue;
                var materials = surface.Renderer.sharedMaterials;
                if (surface.Index < 0 || surface.Index >= materials.Length)
                {
                    Core.Log.Warning($"lamps: {group.Name}: '{surface.Renderer.name}' has no material slot {surface.Index}");
                    continue;
                }
                materials[surface.Index] = material;
                surface.Renderer.sharedMaterials = materials;
            }
        }
    }
}
