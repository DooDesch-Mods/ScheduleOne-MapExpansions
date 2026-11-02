using System.Collections;
using Il2CppFishNet.Object;
using Il2CppScheduleOne.Calling;
using Il2CppScheduleOne.Misc;
using MapExpansions.Lifecycle;
using MapExpansions.Source;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// Pay phones of an expansion. The capture strips the game's pay phone script from the source scene's phones, so
    /// they would stand there without ringing. Each one is replaced by a copy of a working pay phone of the main world at
    /// its pose. The copy hears the call manager like the game's own phones, rings, blinks and is answered the same way,
    /// and the game finds it among its pay phones. It hangs under the expansion's root and goes with it on teardown.
    /// </summary>
    internal static class PayPhones
    {
        private const string IndependentVisual = "MapExpansions independent visual";

        private sealed class Live
        {
            public string Name;
            public PayPhone Phone;
        }

        private static readonly List<Live> Phones = new();
        private static object _sync;

        public static void Build(BuiltExpansion b)
        {
            var sources = b.Source?.PayPhones;
            if (sources == null || sources.Count == 0)
            {
                Core.Log.Msg($"payphones: '{b.Id}' has no pay phones");
                return;
            }
            if (b.Content == null)
            {
                Core.Log.Error($"payphones: '{b.Id}' has no placed content; its {sources.Count} pay phones stay without function");
                return;
            }
            var template = Template();
            if (template == null)
            {
                Core.Log.Error($"payphones: the main world has no working pay phone to copy; the {sources.Count} pay phones of '{b.Id}' stay without function");
                return;
            }
            var holder = MainCopies.TemplateHolder(b);
            int built = 0;
            foreach (var source in sources)
            {
                try
                {
                    var stripped = Resolve(b.Content.transform, source);
                    if (stripped == null)
                    {
                        Core.Log.Error($"payphones: '{b.Id}' pay phone '{source.Path}' is not in the placed copy; it stays without function");
                        continue;
                    }
                    if (!stripped.gameObject.activeInHierarchy)
                    {
                        Core.Log.Msg($"payphones: '{b.Id}' pay phone '{source.Path}' is hidden in the expansion; it is not replaced");
                        continue;
                    }
                    var phone = Replace(b, template, stripped, holder, out int unbatched);
                    if (phone == null) continue;
                    Phones.Add(new Live { Name = $"'{b.Id}' '{source.Path}'", Phone = phone });
                    built++;
                    Core.Log.Msg($"payphones: '{b.Id}' '{source.Path}' replaced by a working copy of '{PathOf(template.transform)}' at {phone.transform.position}, " +
                                 $"{unbatched} batched renderers drawn on their own meshes");
                }
                catch (Exception e)
                {
                    Core.Log.Error($"payphones: '{b.Id}' replacing '{source.Path}' failed; it stays without function: {e}");
                }
            }
            if (built == sources.Count) Core.Log.Msg($"payphones: '{b.Id}' {built}/{sources.Count} pay phones work");
            else Core.Log.Warning($"payphones: '{b.Id}' {built}/{sources.Count} pay phones work");
            if (built > 0 && _sync == null) _sync = MelonCoroutines.Start(SyncQueued());
        }

        public static void Clear()
        {
            if (_sync != null) MelonCoroutines.Stop(_sync);
            _sync = null;
            if (Phones.Count > 0) Core.Log.Msg($"payphones: {Phones.Count} pay phones of expansions released");
            Phones.Clear();
        }

        /// <summary>A pay phone of the main world, not one an expansion made.</summary>
        private static PayPhone Template()
        {
            foreach (var phone in Object.FindObjectsOfType<PayPhone>())
            {
                if (phone == null || phone.IntObj == null || phone.Light == null || phone.RingSound == null) continue;
                var root = phone.transform.root;
                if (root.name.StartsWith("MapExpansions.", StringComparison.Ordinal)) continue;
                return phone;
            }
            return null;
        }

        private static Transform Resolve(Transform root, SourcePayPhone source)
        {
            var t = root;
            foreach (int i in source.Indices)
            {
                if (i < 0 || i >= t.childCount) return null;
                t = t.GetChild(i);
            }
            int slash = source.Path.LastIndexOf('/');
            string name = slash < 0 ? source.Path : source.Path.Substring(slash + 1);
            if (t.name != name)
            {
                Core.Log.Warning($"payphones: '{source.Path}' resolves to '{t.name}' in the placed copy; the hierarchy differs from the capture");
                return null;
            }
            return t;
        }

        private static PayPhone Replace(BuiltExpansion b, PayPhone template, Transform stripped, Transform holder, out int unbatched)
        {
            unbatched = 0;
            // Built under the inactive holder, so nothing of the copy wakes before it stands in place.
            var go = Object.Instantiate(template.gameObject, holder);
            go.name = stripped.name;
            try
            {
                if (go.GetComponentsInChildren<NetworkObject>(true).Length > 0)
                    throw new InvalidOperationException($"the main world's pay phone '{PathOf(template.transform)}' carries a networked object; a local copy of it would not work");
                foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                    if (mr.isPartOfStaticBatch) unbatched++;
                // A batched renderer holds its vertices where the original stands; its own mesh draws where the copy stands.
                if (unbatched > 0)
                {
                    IndependentCopies.Extract(b, template.transform, go, true, new List<Object>());
                    RepointLampSurfaces(go);
                }
                // The main world's lightmap shows the original's spot.
                foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                    if (mr.lightmapIndex >= 0 && mr.lightmapIndex < 65534) mr.lightmapIndex = -1;
            }
            catch
            {
                Object.DestroyImmediate(go);
                throw;
            }
            stripped.gameObject.SetActive(false);
            stripped.name += " (stripped)";
            go.transform.SetParent(stripped.parent, false);
            go.transform.localPosition = stripped.localPosition;
            go.transform.localRotation = stripped.localRotation;
            go.transform.localScale = stripped.localScale;
            go.SetActive(true);
            var phone = go.GetComponent<PayPhone>();
            if (phone == null) Core.Log.Error($"payphones: '{b.Id}' copy '{go.name}' lost its pay phone script");
            return phone;
        }

        /// <summary>
        /// The ringing light switches the glowing material of renderers it was built with. A batched one was replaced by
        /// its independent child and draws nothing itself anymore, so the light switches the child instead.
        /// </summary>
        private static void RepointLampSurfaces(GameObject go)
        {
            foreach (var lamp in go.GetComponentsInChildren<ToggleableLight>(true))
            {
                var meshes = lamp.lightSurfacesMeshes;
                if (meshes == null) continue;
                for (int i = 0; i < meshes.Length; i++)
                {
                    var mr = meshes[i];
                    if (mr == null || mr.enabled) continue;
                    if (mr.transform.Find(IndependentVisual) is Transform child && child.GetComponent<MeshRenderer>() is MeshRenderer visual)
                        meshes[i] = visual;
                    else
                        Core.Log.Warning($"payphones: lamp '{lamp.name}' of '{go.name}' switches the disabled renderer '{mr.name}'; its glow does not show");
                }
            }
        }

        /// <summary>
        /// A copy hears calls queued after its Start only. One queued before it, while the save loaded, is given to it
        /// here, so it rings like the game's phones.
        /// </summary>
        private static IEnumerator SyncQueued()
        {
            yield return null;
            yield return null;
            var cm = CallManager.Instance;
            if (cm == null)
            {
                Core.Log.Warning("payphones: no call manager; the copies wait for the next queued call");
                _sync = null;
                yield break;
            }
            var queued = cm.QueuedCallData;
            if (queued == null)
            {
                Core.Log.Msg($"payphones: no call is queued; {Phones.Count} copies ring with the next one");
                _sync = null;
                yield break;
            }
            int given = 0;
            foreach (var live in Phones)
            {
                if (live.Phone == null || live.Phone.QueuedCall != null) continue;
                live.Phone.QueuedCall = queued;
                live.Phone.UpdateCallState();
                given++;
            }
            Core.Log.Msg($"payphones: the queued call '{queued.name}' was given to {given} copies that missed it");
            _sync = null;
        }

        internal static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (var p = t; p != null; p = p.parent) names.Add(p.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
