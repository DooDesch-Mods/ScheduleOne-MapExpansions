using System.Text.Json;
using System.Text.Json.Nodes;
using MelonLoader.Utils;
using UnityEngine;

namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// The JSON layer of an expansion: entries a map editor wrote, laid over the code definition each time the world
    /// is built. An entry replaces the code entry of the same kind and key, takes it out with <c>"removed": true</c>, or
    /// adds a new one. The code entries are kept as a snapshot and the definition's lists are refilled with copies of
    /// them before each build, so a layer that changes between two builds never stacks on its own earlier result.
    /// </summary>
    internal static class Layers
    {
        internal const int Format = 1;

        /// <summary>Owner name of the expansion's own copies, as <see cref="World.MainCopies"/> logs it.</summary>
        internal const string ExpansionOwner = "expansion";

        /// <summary>Owner name of a shop's copies in a layer.</summary>
        internal static string ShopOwner(string shopId) => "shop:" + shopId;

        /// <summary>Layer text set while the game runs, by expansion id. It wins over the loose file and the resource.</summary>
        private static readonly Dictionary<string, string> Live = new(StringComparer.Ordinal);

        /// <summary>The code definition's entries as the consumer registered them.</summary>
        private sealed class CodeEntries
        {
            public List<ObjectRule> Rules;
            public List<CopyDefinition> Copies;
            public Dictionary<string, List<CopyDefinition>> ShopCopies;
        }

        private static readonly Dictionary<string, CodeEntries> Code = new(StringComparer.Ordinal);

        /// <summary>What the layer of one expansion holds; saving writes exactly these entries.</summary>
        internal sealed class State
        {
            public readonly HashSet<(string owner, string id)> Copies = new();
            public readonly HashSet<(string owner, string id)> RemovedCopies = new();
            /// <summary>Copies the layer adds; they are written in full, the others only with their place.</summary>
            public readonly HashSet<(string owner, string id)> AddedCopies = new();
            public readonly HashSet<string> Rules = new(StringComparer.Ordinal);
            public readonly HashSet<string> RemovedRules = new(StringComparer.Ordinal);
            public readonly HashSet<(string owner, string copy, string id)> Boxes = new();
            public readonly HashSet<(string owner, string copy, string id)> RemovedBoxes = new();

            public int Count => Copies.Count + RemovedCopies.Count + Rules.Count + RemovedRules.Count + Boxes.Count + RemovedBoxes.Count;
        }

        /// <summary>The layer state of each expansion as of its last build, changed by the editor until it saves.</summary>
        internal static readonly Dictionary<string, State> States = new(StringComparer.Ordinal);

        /// <summary>Whether the code definition has a rule with this id; a layer then removes it instead of dropping it.</summary>
        internal static bool IsCodeRule(string expansionId, string key) =>
            Code.TryGetValue(expansionId, out var c) && c.Rules.Any(r => (string.IsNullOrEmpty(r.Id) ? r.MadeId() : r.Id) == key);

        /// <summary>Whether a copy of the code definition has a box with this id.</summary>
        internal static bool IsCodeBox(string expansionId, string owner, string copy, string id)
        {
            if (!Code.TryGetValue(expansionId, out var c)) return false;
            var list = owner == ExpansionOwner ? c.Copies : c.ShopCopies.TryGetValue(owner, out var l) ? l : null;
            return list?.FirstOrDefault(x => x.Id == copy)?.Boxes.Any(x => x.Id == id) == true;
        }

        internal static State StateOf(string expansionId)
        {
            if (!States.TryGetValue(expansionId, out var s)) States[expansionId] = s = new State();
            return s;
        }

        /// <summary>Path of the loose layer file that wins over the embedded one, next to the mods.</summary>
        internal static string LooseFile(string expansionId) => Path.Combine(MelonEnvironment.ModsDirectory, expansionId, "map.json");

        /// <summary>Replaces the layer of an expansion until the game ends; null drops the replacement.</summary>
        internal static void SetLive(string expansionId, string json)
        {
            if (json == null)
            {
                Live.Remove(expansionId);
                Core.Log.Msg($"layer: '{expansionId}' live layer dropped");
                return;
            }
            Live[expansionId] = json;
            Core.Log.Msg($"layer: '{expansionId}' live layer set, {json.Length} characters; it is used from the next world build on");
        }

        /// <summary>The layer text and where it came from, or null when the expansion has none.</summary>
        internal static (string json, string from) Read(ExpansionDefinition def)
        {
            if (Live.TryGetValue(def.Id, out var live)) return (live, "editor");
            var loose = LooseFile(def.Id);
            if (File.Exists(loose))
            {
                Core.Log.Warning($"layer: '{def.Id}' uses the loose file {loose} instead of its embedded layer");
                return (File.ReadAllText(loose), loose);
            }
            var src = def.Layer;
            if (src?.Assembly == null || string.IsNullOrEmpty(src.ResourceName)) return (null, null);
            using var stream = src.Assembly.GetManifestResourceStream(src.ResourceName);
            if (stream == null)
            {
                var names = string.Join(", ", src.Assembly.GetManifestResourceNames());
                Core.Log.Error($"layer: '{def.Id}' resource '{src.ResourceName}' is not in {src.Assembly.GetName().Name} (it has: {names}); the code definition is built alone");
                return (null, null);
            }
            using var reader = new StreamReader(stream);
            return (reader.ReadToEnd(), src.Assembly.GetName().Name + ":" + src.ResourceName);
        }

        /// <summary>
        /// Refills the definition from its code entries and lays the current layer over them. Runs before each build
        /// of the world. A broken layer is logged and leaves the code definition as it is.
        /// </summary>
        internal static void Apply(ExpansionDefinition def)
        {
            Restore(def);
            var state = new State();
            States[def.Id] = state;
            var (json, from) = Read(def);
            if (json == null)
            {
                Core.Log.Msg($"layer: '{def.Id}' has no layer; the code definition is built alone");
                return;
            }
            var root = Parse(def, json, from);
            if (root == null) return;
            ApplyNode(def, root, state, from);
        }

        /// <summary>Parses and checks a layer; null when it cannot be used, with the reason logged.</summary>
        internal static JsonNode Parse(ExpansionDefinition def, string json, string from)
        {
            JsonNode root;
            try
            {
                root = JsonNode.Parse(json);
            }
            catch (Exception e)
            {
                Core.Log.Error($"layer: '{def.Id}' layer from {from} is no valid JSON ({e.Message}); the code definition is built alone");
                return null;
            }
            int format = root?["format"]?.GetValue<int>() ?? 0;
            if (format != Format)
            {
                Core.Log.Error($"layer: '{def.Id}' layer from {from} has format {format}, this MapExpansions reads {Format}; the code definition is built alone");
                return null;
            }
            return root;
        }

        private static void ApplyNode(ExpansionDefinition def, JsonNode root, State state, string from)
        {
            var notes = new List<string>();

            // Copies, per owner list.
            // Models and prefabs are copies made from something else than a template; one list holds all three.
            var copyEntries = Array(root, "copies").Concat(Array(root, "models")).Concat(Array(root, "prefabs")).Select(e => (owner: Str(e, "owner") ?? ExpansionOwner, e)).ToList();
            foreach (var group in copyEntries.GroupBy(x => x.owner, StringComparer.Ordinal))
            {
                var list = CopyList(def, group.Key);
                if (list == null)
                {
                    notes.Add($"{group.Count()} copies of owner '{group.Key}', which the definition does not have");
                    continue;
                }
                string owner = group.Key;
                var merged = LayerMerge.Merge(list, c => c.Id,
                    group.Select(x => new LayerMerge.Entry<JsonNode>(Str(x.e, "id"), Bool(x.e, "removed"), x.e)),
                    (c, e) => { ReadCopy(e, c); return c; },
                    e => NewCopy(e));
                list.Clear();
                list.AddRange(merged.Items);
                var code = Code[def.Id];
                foreach (var key in merged.Layered)
                {
                    var entry = group.Last(x => Str(x.e, "id") == key).e;
                    if (Bool(entry, "removed"))
                    {
                        state.RemovedCopies.Add((owner, key));
                        continue;
                    }
                    state.Copies.Add((owner, key));
                    if (!CodeCopy(code, owner, key)) state.AddedCopies.Add((owner, key));
                }
                Report(def, notes, $"copies of '{owner}'", merged);
            }

            // Rules.
            var ruleEntries = Array(root, "rules").ToList();
            if (ruleEntries.Count > 0)
            {
                var merged = LayerMerge.Merge(def.Hide.ToList(), r => r.Key,
                    ruleEntries.Select(e => new LayerMerge.Entry<JsonNode>(Str(e, "id"), Bool(e, "removed"), e)),
                    (r, e) => NewRule(e) ?? r,
                    e => NewRule(e));
                def.Hide.Clear();
                def.Hide.AddRange(merged.Items);
                foreach (var key in merged.Layered)
                {
                    if (Bool(ruleEntries.Last(e => Str(e, "id") == key), "removed")) state.RemovedRules.Add(key);
                    else state.Rules.Add(key);
                }
                Report(def, notes, "rules", merged);
            }

            // Boxes, per copy.
            var boxEntries = Array(root, "boxes").Select(e => (owner: Str(e, "owner") ?? ExpansionOwner, copy: Str(e, "copy"), e)).ToList();
            foreach (var group in boxEntries.GroupBy(x => (x.owner, x.copy)))
            {
                var copy = CopyList(def, group.Key.owner)?.FirstOrDefault(c => c.Id == group.Key.copy);
                if (copy == null)
                {
                    notes.Add($"{group.Count()} boxes of copy '{group.Key.owner}/{group.Key.copy}', which is not built");
                    continue;
                }
                var merged = LayerMerge.Merge(copy.Boxes.ToList(), x => x.Id,
                    group.Select(x => new LayerMerge.Entry<JsonNode>(Str(x.e, "id"), Bool(x.e, "removed"), x.e)),
                    (x, e) => NewBox(e) ?? x,
                    e => NewBox(e));
                copy.Boxes.Clear();
                copy.Boxes.AddRange(merged.Items);
                foreach (var key in merged.Layered)
                {
                    var k = (group.Key.owner, group.Key.copy, key);
                    if (Bool(group.Last(x => Str(x.e, "id") == key).e, "removed")) state.RemovedBoxes.Add(k);
                    else state.Boxes.Add(k);
                }
                Report(def, notes, $"boxes of '{group.Key.owner}/{group.Key.copy}'", merged);
            }

            foreach (var n in notes) Core.Log.Warning($"layer: '{def.Id}' {n}; left out");
            Core.Log.Msg($"layer: '{def.Id}' layer from {from}: {state.Copies.Count} copies ({state.AddedCopies.Count} new), {state.Rules.Count} rules, " +
                         $"{state.Boxes.Count} boxes laid over the code; {state.RemovedCopies.Count + state.RemovedRules.Count + state.RemovedBoxes.Count} entries taken out");
        }

        private static void Report<T>(ExpansionDefinition def, List<string> notes, string what, LayerMerge.Result<T> r)
        {
            foreach (var k in r.Orphans) Core.Log.Warning($"layer: '{def.Id}' {what}: '{k}' is removed, but the code has no such entry");
            foreach (var k in r.Failed) notes.Add($"{what}: '{k}' is new but lacks what it needs to be built");
            foreach (var k in r.Twice) Core.Log.Warning($"layer: '{def.Id}' {what}: '{k}' appears more than once; the last entry counts");
        }

        /// <summary>The list of copies a layer owner names, or null.</summary>
        internal static List<CopyDefinition> CopyList(ExpansionDefinition def, string owner)
        {
            if (owner == ExpansionOwner) return def.Copies;
            if (owner != null && owner.StartsWith("shop:", StringComparison.Ordinal))
                return def.Shops.FirstOrDefault(s => s.Id == owner.Substring(5))?.Copies;
            return null;
        }

        private static bool CodeCopy(CodeEntries code, string owner, string id)
        {
            if (owner == ExpansionOwner) return code.Copies.Any(c => c.Id == id);
            return code.ShopCopies.TryGetValue(owner, out var list) && list.Any(c => c.Id == id);
        }

        /// <summary>Takes a snapshot of the code entries once, then refills the definition with fresh copies of them.</summary>
        private static void Restore(ExpansionDefinition def)
        {
            if (!Code.TryGetValue(def.Id, out var code))
            {
                code = new CodeEntries
                {
                    Rules = def.Hide.ToList(),
                    Copies = def.Copies.ToList(),
                    ShopCopies = def.Shops.ToDictionary(s => ShopOwner(s.Id), s => s.Copies.ToList(), StringComparer.Ordinal),
                };
                Code[def.Id] = code;
            }
            def.Hide.Clear();
            def.Hide.AddRange(code.Rules.Select(r => r.Clone()));
            def.Copies.Clear();
            def.Copies.AddRange(code.Copies.Select(c => c.Clone()));
            foreach (var s in def.Shops)
            {
                if (!code.ShopCopies.TryGetValue(ShopOwner(s.Id), out var list)) continue;
                s.Copies.Clear();
                s.Copies.AddRange(list.Select(c => c.Clone()));
            }
        }

        /// <summary>Every copy of the definition with the owner name a layer uses for it.</summary>
        internal static List<(string owner, string shop, CopyDefinition def)> AllCopies(ExpansionDefinition def)
        {
            var list = def.Copies.Select(c => (ExpansionOwner, (string)null, c)).ToList();
            foreach (var s in def.Shops)
                list.AddRange(s.Copies.Select(c => (ShopOwner(s.Id), s.Id, c)));
            return list;
        }

        // ---- reading entries

        private static IEnumerable<JsonNode> Array(JsonNode root, string name) =>
            root[name] is JsonArray a ? a.Where(e => e != null) : Enumerable.Empty<JsonNode>();

        private static string Str(JsonNode e, string name) => e?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        private static bool Bool(JsonNode e, string name) => e?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        private static float? Num(JsonNode e, string name) => e?[name] is JsonValue v && v.TryGetValue<float>(out var f) ? f : null;

        private static Vector3? Vec(JsonNode n)
        {
            if (n is not JsonArray a || a.Count != 3) return null;
            return new Vector3(a[0].GetValue<float>(), a[1].GetValue<float>(), a[2].GetValue<float>());
        }

        private static Bounds? Box(JsonNode n)
        {
            var min = Vec(n?["min"]);
            var max = Vec(n?["max"]);
            if (min == null || max == null) return null;
            var b = new Bounds();
            b.SetMinMax(Vector3.Min(min.Value, max.Value), Vector3.Max(min.Value, max.Value));
            return b;
        }

        private static T? Enum<T>(JsonNode e, string name) where T : struct =>
            Str(e, name) is string s && System.Enum.TryParse<T>(s, true, out var v) ? v : null;

        private static void ReadCopy(JsonNode e, CopyDefinition d)
        {
            if (Str(e, "template") is string t) d.Template = t;
            if (Enum<WorldSide>(e, "templateSide") is WorldSide side) d.TemplateSide = side;
            if (e["frame"] != null) d.Frame = Str(e, "frame");
            if (e["onto"] != null) d.Onto = Str(e, "onto");
            if (e["at"] is JsonNode at)
                d.At = new LocalPoint(Vec(at["pos"]) ?? d.At.Position, Num(at, "yaw") ?? d.At.Yaw);
            if (Vec(e["ontoOffset"]) is Vector3 off) d.OntoOffset = off;
            if (Vec(e["ontoRotation"]) is Vector3 rot) d.OntoRotation = rot;
            if (Num(e, "scale") is float scale) d.Scale = scale;
            if (e["ownMesh"] != null) d.OwnMesh = Bool(e, "ownMesh");
            if (e["file"] != null) d.Model = Str(e, "file");
            if (e["prefab"] != null) d.Prefab = Str(e, "prefab");
            if (Enum<ModelCollider>(e, "collider") is ModelCollider col) d.Collider = col;
            if (e["exclude"] is JsonArray ex)
            {
                d.Exclude.Clear();
                d.Exclude.AddRange(ex.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)));
            }
        }

        /// <summary>A copy the layer adds; it needs at least an id and a template, a model file or a prefab.</summary>
        private static CopyDefinition NewCopy(JsonNode e)
        {
            var d = new CopyDefinition { Id = Str(e, "id") };
            ReadCopy(e, d);
            bool made = !string.IsNullOrEmpty(d.Template) || !string.IsNullOrEmpty(d.Model) || !string.IsNullOrEmpty(d.Prefab);
            return string.IsNullOrEmpty(d.Id) || !made ? null : d;
        }

        /// <summary>A rule from a layer entry; it needs a name and an effect.</summary>
        private static ObjectRule NewRule(JsonNode e)
        {
            var name = Str(e, "name");
            var effect = Enum<ObjectEffect>(e, "effect");
            if (string.IsNullOrEmpty(name) || effect == null) return null;
            var r = new ObjectRule
            {
                Id = Str(e, "id"),
                Key = Str(e, "id"),
                Side = Enum<WorldSide>(e, "side") ?? WorldSide.Main,
                Name = name,
                Parent = Str(e, "parent"),
                Effect = effect.Value,
            };
            if (Box(e["area"]) is Bounds area) r.Area = area;
            if (Num(e, "bottom") is float bottom) r.Bottom = bottom;
            if (Num(e, "topShift") is float top) r.TopShift = top;
            foreach (var o in Array(e, "openings")) if (Box(o) is Bounds b) r.Openings.Add(b);
            foreach (var o in Array(e, "windows")) if (Box(o) is Bounds b) r.Windows.Add(b);
            foreach (var o in Array(e, "bands")) r.Bands.Add((Num(o, "from") ?? 0f, Str(o, "material")));
            r.CeilingMaterial = Str(e, "ceiling");
            r.FloorMaterial = Str(e, "floor");
            if (e["frame"] is JsonNode f)
            {
                r.FrameMaterial = Str(f, "material");
                r.FrameWidth = Num(f, "width") ?? r.FrameWidth;
            }
            if (Num(e, "inset") is float inset) r.LiningInset = inset;
            return r;
        }

        private static BoxDefinition NewBox(JsonNode e)
        {
            var id = Str(e, "id");
            var kind = Enum<BoxKind>(e, "kind");
            var box = Box(e);
            if (string.IsNullOrEmpty(id) || kind == null || box == null) return null;
            return new BoxDefinition { Id = id, Kind = kind.Value, Box = box.Value, Material = Str(e, "material") };
        }

        // ---- writing entries

        /// <summary>The layer entry of one copy as it stands in the definition now; new copies are written in full.</summary>
        internal static JsonObject CopyEntry(string owner, CopyDefinition d, bool full)
        {
            var o = new JsonObject { ["id"] = d.Id, ["owner"] = owner };
            if (full && !string.IsNullOrEmpty(d.Model))
            {
                o["file"] = d.Model;
                o["collider"] = d.Collider.ToString();
            }
            else if (full && !string.IsNullOrEmpty(d.Prefab)) o["prefab"] = d.Prefab;
            else if (full)
            {
                o["template"] = d.Template;
                if (d.TemplateSide != WorldSide.Main) o["templateSide"] = d.TemplateSide.ToString();
                if (!string.IsNullOrEmpty(d.Frame)) o["frame"] = d.Frame;
                if (!string.IsNullOrEmpty(d.Onto)) o["onto"] = d.Onto;
                if (!d.OwnMesh) o["ownMesh"] = false;
                if (d.Exclude.Count > 0) o["exclude"] = new JsonArray(d.Exclude.Select(x => (JsonNode)x).ToArray());
            }
            if (string.IsNullOrEmpty(d.Onto))
                o["at"] = new JsonObject { ["pos"] = Vector(d.At.Position), ["yaw"] = Round(d.At.Yaw) };
            else
            {
                o["ontoOffset"] = Vector(d.OntoOffset);
                o["ontoRotation"] = Vector(d.OntoRotation);
            }
            // Scales come as fractions like 1 / 0.9; three digits would shift a large model by millimetres.
            o["scale"] = MathF.Round(d.Scale, 5);
            return o;
        }

        /// <summary>The full layer entry of a rule.</summary>
        internal static JsonObject RuleEntry(ObjectRule r)
        {
            var o = new JsonObject { ["id"] = r.Key, ["side"] = r.Side.ToString(), ["name"] = r.Name };
            if (!string.IsNullOrEmpty(r.Parent)) o["parent"] = r.Parent;
            if (r.Area.size != Vector3.zero) o["area"] = BoxNode(r.Area);
            o["effect"] = r.Effect.ToString();
            if (r.Bottom != 0f) o["bottom"] = Round(r.Bottom);
            if (r.TopShift != 0f) o["topShift"] = Round(r.TopShift);
            if (r.Openings.Count > 0) o["openings"] = new JsonArray(r.Openings.Select(b => (JsonNode)BoxNode(b)).ToArray());
            if (r.Windows.Count > 0) o["windows"] = new JsonArray(r.Windows.Select(b => (JsonNode)BoxNode(b)).ToArray());
            if (r.Bands.Count > 0)
                o["bands"] = new JsonArray(r.Bands.Select(b => (JsonNode)new JsonObject { ["from"] = Round(b.From), ["material"] = b.Material }).ToArray());
            if (!string.IsNullOrEmpty(r.CeilingMaterial)) o["ceiling"] = r.CeilingMaterial;
            if (!string.IsNullOrEmpty(r.FloorMaterial)) o["floor"] = r.FloorMaterial;
            if (!string.IsNullOrEmpty(r.FrameMaterial)) o["frame"] = new JsonObject { ["material"] = r.FrameMaterial, ["width"] = Round(r.FrameWidth) };
            if (r.LiningInset != 0.01f) o["inset"] = Round(r.LiningInset);
            return o;
        }

        internal static JsonObject BoxEntry(string owner, string copy, BoxDefinition b)
        {
            var o = new JsonObject { ["id"] = b.Id, ["owner"] = owner, ["copy"] = copy, ["kind"] = b.Kind.ToString() };
            o["min"] = Vector(b.Box.min);
            o["max"] = Vector(b.Box.max);
            if (b.Kind == BoxKind.Panel) o["material"] = b.Material;
            return o;
        }

        private static JsonObject BoxNode(Bounds b) => new() { ["min"] = Vector(b.min), ["max"] = Vector(b.max) };

        private static JsonArray Vector(Vector3 v) => new(Round(v.x), Round(v.y), Round(v.z));

        /// <summary>Millimetres and thousandths of a degree; more digits only make the diff noisy.</summary>
        private static float Round(float f) => MathF.Round(f, 3);

        /// <summary>
        /// The layer of an expansion as its state holds it: every layered entry as the definition holds it now, and
        /// every removal. Stable order, indented, one entry per line block.
        /// </summary>
        internal static string Write(ExpansionDefinition def, State state)
        {
            var copies = new List<JsonObject>();
            var models = new List<JsonObject>();
            var prefabs = new List<JsonObject>();
            foreach (var (owner, id) in state.Copies)
            {
                var d = CopyList(def, owner)?.FirstOrDefault(c => c.Id == id);
                if (d == null)
                {
                    Core.Log.Warning($"layer: write '{def.Id}': copy '{owner}/{id}' is no longer in the definition; it is not written");
                    continue;
                }
                var to = !string.IsNullOrEmpty(d.Model) ? models : !string.IsNullOrEmpty(d.Prefab) ? prefabs : copies;
                to.Add(CopyEntry(owner, d, state.AddedCopies.Contains((owner, id))));
            }
            copies.AddRange(state.RemovedCopies.Select(k => new JsonObject { ["id"] = k.id, ["owner"] = k.owner, ["removed"] = true }));

            var rules = new List<JsonObject>();
            foreach (var key in state.Rules)
            {
                var r = def.Hide.FirstOrDefault(x => x.Key == key);
                if (r == null)
                {
                    Core.Log.Warning($"layer: write '{def.Id}': rule '{key}' is no longer in the definition; it is not written");
                    continue;
                }
                rules.Add(RuleEntry(r));
            }
            rules.AddRange(state.RemovedRules.Select(k => new JsonObject { ["id"] = k, ["removed"] = true }));

            var boxes = new List<JsonObject>();
            foreach (var (owner, copy, id) in state.Boxes)
            {
                var b = CopyList(def, owner)?.FirstOrDefault(c => c.Id == copy)?.Boxes.FirstOrDefault(x => x.Id == id);
                if (b == null)
                {
                    Core.Log.Warning($"layer: write '{def.Id}': box '{owner}/{copy}/{id}' is no longer in the definition; it is not written");
                    continue;
                }
                boxes.Add(BoxEntry(owner, copy, b));
            }
            boxes.AddRange(state.RemovedBoxes.Select(k => new JsonObject { ["id"] = k.id, ["owner"] = k.owner, ["copy"] = k.copy, ["removed"] = true }));

            var root = new JsonObject { ["format"] = Format, ["expansion"] = def.Id };
            root["copies"] = Sorted(copies, "owner", "id");
            if (models.Count > 0) root["models"] = Sorted(models, "owner", "id");
            if (prefabs.Count > 0) root["prefabs"] = Sorted(prefabs, "owner", "id");
            if (rules.Count > 0) root["rules"] = Sorted(rules, "id");
            if (boxes.Count > 0) root["boxes"] = Sorted(boxes, "owner", "copy", "id");
            // Indented output uses the platform line end; the file keeps one line end everywhere.
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
        }

        private static JsonArray Sorted(List<JsonObject> items, params string[] keys)
        {
            IOrderedEnumerable<JsonObject> ordered = null;
            foreach (var k in keys)
            {
                Func<JsonObject, string> f = o => Str(o, k) ?? "";
                ordered = ordered == null ? items.OrderBy(f, StringComparer.Ordinal) : ordered.ThenBy(f, StringComparer.Ordinal);
            }
            return new JsonArray((ordered ?? items.OrderBy(_ => 0)).Cast<JsonNode>().ToArray());
        }
    }
}
