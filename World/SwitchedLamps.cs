using Il2CppScheduleOne.Misc;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Lets the lamps of a copy follow a wall switch of the main world. The switch only knows the lamps it was built
    /// with, and it is networked by the game, so every peer reads its state and switches its own copy.
    /// </summary>
    internal static class SwitchedLamps
    {
        private const float Interval = .2f;
        private const string IndependentVisual = "MapExpansions independent visual";

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

        internal static void Follow(string name, GameObject copy, string path, List<Surface> surfaces)
        {
            var target = MainCopies.FindMain(path);
            var modular = target != null ? target.GetComponent<ModularSwitch>() : null;
            if (modular == null)
            {
                Core.Log.Error($"lamps: {name}: no ModularSwitch at '{path}'; its lamps keep their state");
                return;
            }
            // A batched renderer was replaced by its independent child and draws nothing itself anymore.
            foreach (var surface in surfaces)
                if (!surface.Renderer.enabled && surface.Renderer.transform.Find(IndependentVisual) is Transform child && child.GetComponent<MeshRenderer>() is MeshRenderer visual)
                    surface.Renderer = visual;
            var lights = copy.GetComponentsInChildren<Light>(true);
            if (lights.Length == 0 && surfaces.Count == 0)
                Core.Log.Warning($"lamps: {name}: the copy has nothing to switch");
            Groups.Add(new Group { Name = name, Path = path, Owner = copy, Switch = modular, Lights = lights, Surfaces = surfaces });
            Core.Log.Msg($"lamps: {name} follows '{path}' with {lights.Length} lights and {surfaces.Count} glowing surfaces");
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
                if (group.Switch == null)
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

        private static void Apply(Group group, bool on)
        {
            foreach (var light in group.Lights)
                if (light != null) light.enabled = on;
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
