using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(MapExpansions.Editor.Core), "MapExpansions.Editor", "0.0.0-dev", "DooDesch")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonAdditionalDependencies("MapExpansions")]

namespace MapExpansions.Editor
{
    /// <summary>
    /// The map editor for MapExpansions: a key opens a fly camera over the loaded world with docks around it,
    /// and what an author moves is written into the expansion's JSON layer.
    /// </summary>
    public sealed class Core : MelonMod
    {
        internal static MelonLogger.Instance Log { get; private set; }

        private static MelonPreferences_Entry<string> _openKey;
        private static MelonPreferences_Entry<string> _layerFiles;
        private static KeyCode _key = KeyCode.F4;

        /// <summary>Seconds between two drafts of unsaved changes.</summary>
        private const float DraftEvery = 60f;

        private static float _sinceDraft;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            var cat = MelonPreferences.CreateCategory("MapExpansionsEditor");
            _openKey = cat.CreateEntry("OpenKey", "F4", description: "Key that opens and closes the map editor (a Unity KeyCode name).");
            _layerFiles = cat.CreateEntry("LayerFiles", "",
                description: "Where saving writes each expansion's layer, as id=path pairs split by ';'. " +
                             "An expansion without a path is written to Mods/<id>/map.json.");
            ReadKey();
            _openKey.OnEntryValueChanged.Subscribe((_, _) => ReadKey());
#if DEBUG
            TestExpansion.RegisterIfAsked();
#endif
            Log.Msg($"initialized; {HarmonyInstance.GetPatchedMethods().Count()} methods patched, open key {_key}");
        }

        private static void ReadKey()
        {
            if (Enum.TryParse(_openKey.Value, true, out KeyCode k) && k != KeyCode.None)
            {
                _key = k;
                return;
            }
            Log.Warning($"OpenKey '{_openKey.Value}' is no Unity KeyCode name; the editor keeps {_key}");
        }

        /// <summary>The file a save writes for an expansion: the configured one, else the loose file next to the mods.</summary>
        internal static string LayerFile(string expansionId)
        {
            foreach (var pair in (_layerFiles.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq).Trim() == expansionId) return pair.Substring(eq + 1).Trim();
            }
            return Lifecycle.Layers.LooseFile(expansionId);
        }

        /// <summary>Points saving for one expansion at a file for this session; the preferences file is not written.</summary>
        internal static void SetLayerFile(string expansionId, string path)
        {
            var pairs = (_layerFiles.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => !x.TrimStart().StartsWith(expansionId + "=", StringComparison.Ordinal)).ToList();
            pairs.Add(expansionId + "=" + path);
            _layerFiles.Value = string.Join(";", pairs);
            Log.Msg($"layer file of '{expansionId}' for this session: {path}");
        }

        public override void OnUpdate()
        {
            EditorInput.BeginFrame();
            if (EditorInput.KeyDown(_key)) EditorMode.Toggle($"key {_key}");
            // While the player walks after Tab, Tab brings the editor back; in the editor Tab is its own key.
            else if (EditorMode.Playing && !EditorMode.IsOpen && EditorInput.KeyDown(KeyCode.Tab) && !Il2CppScheduleOne.GameInput.IsTyping)
                EditorMode.Resume("tab");
            EditorMode.Tick();
            EditorInput.EndFrame();

            _sinceDraft += Time.unscaledDeltaTime;
            if (_sinceDraft >= DraftEvery)
            {
                _sinceDraft = 0f;
                if (Saving.HasChanges) Saving.Draft("every minute");
            }
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (sceneName != Lifecycle.BuildPipeline.MainScene) return;
            EditorMode.SceneGone();
            if (Saving.HasChanges) Saving.Draft("leaving the world");
            Saving.Forget("the world unloaded");
            Edits.Forget("the world unloaded");
            AreaEdits.Forget();
            AreaEdits.ForgetMaterials();
        }
    }
}
