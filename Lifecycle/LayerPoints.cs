using System.Text.Json.Nodes;
using UnityEngine;

namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// The gameplay points of a layer: locations, regions, deal locations, parking lots, buildings, and the seller
    /// and counter of shops. Each kind is a JSON array named like the definition's list; an entry is keyed by its
    /// "id" (the name for locations and regions), replaces the code entry of the same key, takes it out with
    /// <c>"removed": true</c>, or adds a new one. Shops are only changed: a shop needs copies and a seller's day that
    /// the code gives it. Points are built once per world build, so a change takes effect on the next one.
    /// </summary>
    internal static partial class Layers
    {
        internal const string LocationsKind = "locations";
        internal const string RegionsKind = "regions";
        internal const string DealsKind = "dealLocations";
        internal const string LotsKind = "parkingLots";
        internal const string BuildingsKind = "buildings";
        internal const string ShopsKind = "shops";

        internal static readonly string[] PointKinds = { LocationsKind, RegionsKind, DealsKind, LotsKind, BuildingsKind, ShopsKind };

        /// <summary>The code definition's points as the consumer registered them.</summary>
        private sealed class CodePoints
        {
            public List<LocationDefinition> Locations;
            public List<RegionDefinition> Regions;
            public List<DealLocationDefinition> Deals;
            public List<ParkingLotDefinition> Lots;
            public List<BuildingDefinition> Buildings;
            public Dictionary<string, (string Seller, LocalPoint Counter, float Reach, string Choice)> Shops;
        }

        private static readonly Dictionary<string, CodePoints> CodePointsOf = new(StringComparer.Ordinal);

        /// <summary>Takes a snapshot of the code's points once, then refills the definition with fresh copies of them.</summary>
        private static void RestorePoints(ExpansionDefinition def)
        {
            if (!CodePointsOf.TryGetValue(def.Id, out var code))
            {
                code = new CodePoints
                {
                    Locations = def.Locations.Select(Clone).ToList(),
                    Regions = def.Regions.Select(Clone).ToList(),
                    Deals = def.DealLocations.Select(Clone).ToList(),
                    Lots = def.ParkingLots.Select(Clone).ToList(),
                    Buildings = def.Buildings.Select(Clone).ToList(),
                    Shops = def.Shops.Where(s => !string.IsNullOrEmpty(s.Id))
                        .ToDictionary(s => s.Id, s => (s.Seller, s.Counter, s.CounterReach, s.ChoiceText), StringComparer.Ordinal),
                };
                CodePointsOf[def.Id] = code;
            }
            Refill(def.Locations, code.Locations, Clone);
            Refill(def.Regions, code.Regions, Clone);
            Refill(def.DealLocations, code.Deals, Clone);
            Refill(def.ParkingLots, code.Lots, Clone);
            Refill(def.Buildings, code.Buildings, Clone);
            foreach (var s in def.Shops)
            {
                if (s.Id == null || !code.Shops.TryGetValue(s.Id, out var c)) continue;
                s.Seller = c.Seller;
                s.Counter = c.Counter;
                s.CounterReach = c.Reach;
                s.ChoiceText = c.Choice;
            }
        }

        private static void Refill<T>(List<T> list, List<T> code, Func<T, T> clone)
        {
            list.Clear();
            list.AddRange(code.Select(clone));
        }

        /// <summary>The counter a shop has in the code definition, before a layer moved it; null for a shop the code does not have.</summary>
        internal static LocalPoint? CodeCounter(string expansionId, string shopId) =>
            shopId != null && CodePointsOf.TryGetValue(expansionId, out var c) && c.Shops.TryGetValue(shopId, out var s) ? s.Counter : null;

        /// <summary>Whether the code definition has a point of this kind and key; a layer then removes it instead of dropping it.</summary>
        internal static bool IsCodePoint(string expansionId, string kind, string key)
        {
            if (!CodePointsOf.TryGetValue(expansionId, out var c)) return false;
            return kind switch
            {
                LocationsKind => c.Locations.Any(x => x.Name == key),
                RegionsKind => c.Regions.Any(x => x.Name == key),
                DealsKind => c.Deals.Any(x => x.Id == key),
                LotsKind => c.Lots.Any(x => x.Id == key),
                BuildingsKind => c.Buildings.Any(x => x.Id == key),
                ShopsKind => c.Shops.ContainsKey(key),
                _ => false,
            };
        }

        // ---- clones

        private static LocationDefinition Clone(LocationDefinition x) => new(x.Name, x.LocalPosition, x.LocalYaw);

        private static RegionDefinition Clone(RegionDefinition x)
        {
            var r = new RegionDefinition { Name = x.Name, UnlockedByDefault = x.UnlockedByDefault, StandInRegion = x.StandInRegion };
            r.Polygon.AddRange(x.Polygon);
            return r;
        }

        private static DealLocationDefinition Clone(DealLocationDefinition x) =>
            new() { Id = x.Id, Name = x.Name, Description = x.Description, StandPoint = x.StandPoint, Region = x.Region };

        private static ParkingLotDefinition Clone(ParkingLotDefinition x)
        {
            var l = new ParkingLotDefinition { Id = x.Id, Name = x.Name, Entry = x.Entry };
            l.Spots.AddRange(x.Spots);
            return l;
        }

        private static BuildingDefinition Clone(BuildingDefinition x)
        {
            var b = new BuildingDefinition { Id = x.Id, Name = x.Name, Knock = x.Knock };
            b.Doors.AddRange(x.Doors);
            b.DoorModels.AddRange(x.DoorModels);
            return b;
        }

        // ---- reading

        private static LocalPoint? Pt(JsonNode n)
        {
            if (n == null || Vec(n["pos"]) is not Vector3 p) return null;
            return new LocalPoint(p, Num(n, "yaw") ?? 0f);
        }

        private static List<LocalPoint> Pts(JsonNode n) =>
            n is JsonArray a ? a.Select(Pt).Where(x => x != null).Select(x => x.Value).ToList() : null;

        /// <summary>Fills a location from an entry; true when it has what a new location needs.</summary>
        private static bool Read(JsonNode e, LocationDefinition d)
        {
            d.Name = Str(e, "id") ?? d.Name;
            if (Pt(e["at"]) is not LocalPoint at) return false;
            d.LocalPosition = at.Position;
            d.LocalYaw = at.Yaw;
            return !string.IsNullOrEmpty(d.Name);
        }

        private static bool Read(JsonNode e, RegionDefinition d)
        {
            d.Name = Str(e, "id") ?? d.Name;
            if (e["polygon"] is JsonArray poly)
            {
                d.Polygon.Clear();
                foreach (var c in poly)
                    if (c is JsonArray xz && xz.Count == 2) d.Polygon.Add(new Vector2(xz[0].GetValue<float>(), xz[1].GetValue<float>()));
            }
            if (e["unlocked"] != null) d.UnlockedByDefault = Bool(e, "unlocked");
            if (Num(e, "standIn") is float standIn) d.StandInRegion = (int)standIn;
            return !string.IsNullOrEmpty(d.Name) && d.Polygon.Count >= 3;
        }

        private static bool Read(JsonNode e, DealLocationDefinition d)
        {
            d.Id = Str(e, "id") ?? d.Id;
            if (e["name"] != null) d.Name = Str(e, "name");
            if (e["description"] != null) d.Description = Str(e, "description") ?? string.Empty;
            if (e["region"] != null) d.Region = Str(e, "region");
            bool placed = Pt(e["at"]) is LocalPoint at;
            if (placed) d.StandPoint = Pt(e["at"]).Value;
            return placed && !string.IsNullOrEmpty(d.Id) && !string.IsNullOrEmpty(d.Name);
        }

        private static bool Read(JsonNode e, ParkingLotDefinition d)
        {
            d.Id = Str(e, "id") ?? d.Id;
            if (e["name"] != null) d.Name = Str(e, "name");
            bool placed = Pt(e["entry"]) is LocalPoint entry;
            if (placed) d.Entry = Pt(e["entry"]).Value;
            if (Pts(e["spots"]) is List<LocalPoint> spots)
            {
                d.Spots.Clear();
                d.Spots.AddRange(spots);
            }
            return placed && !string.IsNullOrEmpty(d.Id) && !string.IsNullOrEmpty(d.Name);
        }

        private static bool Read(JsonNode e, BuildingDefinition d)
        {
            d.Id = Str(e, "id") ?? d.Id;
            if (e["name"] != null) d.Name = Str(e, "name");
            if (e["knock"] != null) d.Knock = Bool(e, "knock");
            if (Pts(e["doors"]) is List<LocalPoint> doors)
            {
                d.Doors.Clear();
                d.Doors.AddRange(doors);
            }
            if (e["doorModels"] is JsonArray models)
            {
                d.DoorModels.Clear();
                d.DoorModels.AddRange(models.Select(x => x?.GetValue<string>() ?? ""));
            }
            return !string.IsNullOrEmpty(d.Id) && !string.IsNullOrEmpty(d.Name) && d.Doors.Count > 0;
        }

        private static void Read(JsonNode e, ShopDefinition s)
        {
            if (e["seller"] != null) s.Seller = Str(e, "seller");
            if (Pt(e["counter"]) is LocalPoint counter) s.Counter = counter;
            if (Num(e, "counterReach") is float reach) s.CounterReach = reach;
            if (e["choiceText"] != null) s.ChoiceText = Str(e, "choiceText");
        }

        /// <summary>Lays one kind's entries over its list and records what the layer holds.</summary>
        private static void MergePoints<T>(ExpansionDefinition def, JsonNode root, State state, List<string> notes, string kind,
            List<T> list, Func<T, string> keyOf, Func<T, T> clone, Func<JsonNode, T, bool> read, Func<T> make) where T : class
        {
            var entries = Array(root, kind).ToList();
            if (entries.Count == 0) return;
            var merged = LayerMerge.Merge(list.ToList(), keyOf,
                entries.Select(e => new LayerMerge.Entry<JsonNode>(Str(e, "id"), Bool(e, "removed"), e)),
                (c, e) =>
                {
                    var x = clone(c);
                    read(e, x);
                    return x;
                },
                e =>
                {
                    var x = make();
                    return read(e, x) ? x : null;
                });
            list.Clear();
            list.AddRange(merged.Items);
            foreach (var key in merged.Layered)
            {
                if (Bool(entries.Last(e => Str(e, "id") == key), "removed")) state.RemovedPoints.Add((kind, key));
                else state.Points.Add((kind, key));
            }
            Report(def, notes, kind, merged);
        }

        private static void ApplyPoints(ExpansionDefinition def, JsonNode root, State state, List<string> notes)
        {
            MergePoints(def, root, state, notes, LocationsKind, def.Locations, x => x.Name, Clone, Read, () => new LocationDefinition());
            MergePoints(def, root, state, notes, RegionsKind, def.Regions, x => x.Name, Clone, Read, () => new RegionDefinition());
            MergePoints(def, root, state, notes, DealsKind, def.DealLocations, x => x.Id, Clone, Read, () => new DealLocationDefinition());
            MergePoints(def, root, state, notes, LotsKind, def.ParkingLots, x => x.Id, Clone, Read, () => new ParkingLotDefinition());
            MergePoints(def, root, state, notes, BuildingsKind, def.Buildings, x => x.Id, Clone, Read, () => new BuildingDefinition());

            foreach (var e in Array(root, ShopsKind))
            {
                var id = Str(e, "id");
                var shop = def.Shops.FirstOrDefault(s => s.Id == id);
                if (shop == null)
                {
                    notes.Add($"shops: '{id}' is not a shop of the code; shops come from the code, a layer only moves their counter or changes the seller");
                    continue;
                }
                if (Bool(e, "removed"))
                {
                    notes.Add($"shops: '{id}' is marked removed; a layer cannot take out a shop");
                    continue;
                }
                Read(e, shop);
                state.Points.Add((ShopsKind, id));
            }
        }

        // ---- writing

        private static JsonObject PtNode(LocalPoint p) => new() { ["pos"] = Vector(p.Position), ["yaw"] = Round(p.Yaw) };

        private static JsonArray PtsNode(IEnumerable<LocalPoint> list) => new(list.Select(p => (JsonNode)PtNode(p)).ToArray());

        /// <summary>The full layer entry of a point as the definition holds it now, or null when there is none.</summary>
        internal static JsonObject PointEntry(ExpansionDefinition def, string kind, string key)
        {
            switch (kind)
            {
                case LocationsKind:
                    var loc = def.Locations.FirstOrDefault(x => x.Name == key);
                    return loc == null ? null : new JsonObject { ["id"] = loc.Name, ["at"] = PtNode(new LocalPoint(loc.LocalPosition, loc.LocalYaw)) };
                case RegionsKind:
                    var r = def.Regions.FirstOrDefault(x => x.Name == key);
                    return r == null ? null : new JsonObject
                    {
                        ["id"] = r.Name,
                        ["polygon"] = new JsonArray(r.Polygon.Select(p => (JsonNode)new JsonArray(Round(p.x), Round(p.y))).ToArray()),
                        ["unlocked"] = r.UnlockedByDefault,
                        ["standIn"] = r.StandInRegion,
                    };
                case DealsKind:
                    var d = def.DealLocations.FirstOrDefault(x => x.Id == key);
                    if (d == null) return null;
                    var o = new JsonObject { ["id"] = d.Id, ["name"] = d.Name, ["at"] = PtNode(d.StandPoint) };
                    if (!string.IsNullOrEmpty(d.Description)) o["description"] = d.Description;
                    if (!string.IsNullOrEmpty(d.Region)) o["region"] = d.Region;
                    return o;
                case LotsKind:
                    var l = def.ParkingLots.FirstOrDefault(x => x.Id == key);
                    return l == null ? null : new JsonObject { ["id"] = l.Id, ["name"] = l.Name, ["entry"] = PtNode(l.Entry), ["spots"] = PtsNode(l.Spots) };
                case BuildingsKind:
                    var b = def.Buildings.FirstOrDefault(x => x.Id == key);
                    if (b == null) return null;
                    var bo = new JsonObject { ["id"] = b.Id, ["name"] = b.Name, ["doors"] = PtsNode(b.Doors) };
                    if (b.Knock) bo["knock"] = true;
                    if (b.DoorModels.Count > 0) bo["doorModels"] = new JsonArray(b.DoorModels.Select(x => (JsonNode)x).ToArray());
                    return bo;
                case ShopsKind:
                    var s = def.Shops.FirstOrDefault(x => x.Id == key);
                    if (s == null) return null;
                    var so = new JsonObject { ["id"] = s.Id, ["seller"] = s.Seller, ["counter"] = PtNode(s.Counter), ["counterReach"] = Round(s.CounterReach) };
                    if (!string.IsNullOrEmpty(s.ChoiceText)) so["choiceText"] = s.ChoiceText;
                    return so;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Puts a point into the definition as <paramref name="entry"/> describes it, in place of the one with the same
        /// key, or takes it out when <paramref name="entry"/> is null. False with the reason logged when that fails.
        /// </summary>
        internal static bool PutPoint(ExpansionDefinition def, string kind, string key, JsonObject entry)
        {
            switch (kind)
            {
                case LocationsKind: return Put(def, kind, key, entry, def.Locations, x => x.Name, Clone, Read, () => new LocationDefinition());
                case RegionsKind: return Put(def, kind, key, entry, def.Regions, x => x.Name, Clone, Read, () => new RegionDefinition());
                case DealsKind: return Put(def, kind, key, entry, def.DealLocations, x => x.Id, Clone, Read, () => new DealLocationDefinition());
                case LotsKind: return Put(def, kind, key, entry, def.ParkingLots, x => x.Id, Clone, Read, () => new ParkingLotDefinition());
                case BuildingsKind: return Put(def, kind, key, entry, def.Buildings, x => x.Id, Clone, Read, () => new BuildingDefinition());
                case ShopsKind:
                    var shop = def.Shops.FirstOrDefault(s => s.Id == key);
                    if (shop == null || entry == null)
                    {
                        Core.Log.Warning($"layer: '{def.Id}' shop '{key}' {(shop == null ? "does not exist" : "cannot be taken out")}; nothing changed");
                        return false;
                    }
                    Read(entry, shop);
                    return true;
                default:
                    Core.Log.Warning($"layer: '{def.Id}' has no point kind '{kind}'");
                    return false;
            }
        }

        private static bool Put<T>(ExpansionDefinition def, string kind, string key, JsonObject entry, List<T> list,
            Func<T, string> keyOf, Func<T, T> clone, Func<JsonNode, T, bool> read, Func<T> make) where T : class
        {
            int at = list.FindIndex(x => keyOf(x) == key);
            if (entry == null)
            {
                if (at < 0)
                {
                    Core.Log.Warning($"layer: '{def.Id}' {kind} '{key}' is not in the definition; nothing to take out");
                    return false;
                }
                list.RemoveAt(at);
                return true;
            }
            var x = at >= 0 ? clone(list[at]) : make();
            if (!read(entry, x) && at < 0)
            {
                Core.Log.Warning($"layer: '{def.Id}' {kind} '{key}' lacks what a new entry needs: {entry.ToJsonString()}");
                return false;
            }
            if (at >= 0) list[at] = x;
            else list.Add(x);
            return true;
        }

        /// <summary>The point arrays of a layer, removals included, in stable order.</summary>
        private static void WritePoints(ExpansionDefinition def, State state, JsonObject root)
        {
            foreach (var kind in PointKinds)
            {
                var items = new List<JsonObject>();
                foreach (var (k, key) in state.Points.Where(p => p.kind == kind))
                {
                    var o = PointEntry(def, kind, key);
                    if (o == null)
                    {
                        Core.Log.Warning($"layer: write '{def.Id}': {kind} '{key}' is no longer in the definition; it is not written");
                        continue;
                    }
                    items.Add(o);
                }
                items.AddRange(state.RemovedPoints.Where(p => p.kind == kind).Select(p => new JsonObject { ["id"] = p.id, ["removed"] = true }));
                if (items.Count > 0) root[kind] = Sorted(items, "id");
            }
        }
    }
}
