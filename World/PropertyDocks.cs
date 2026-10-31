using Il2CppFishNet.Object;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Delivery;
using Il2CppScheduleOne.Map;
using MapExpansions.Lifecycle;
using UnityEngine;
using Property = Il2CppScheduleOne.Property.Property;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    internal sealed class PropertyDockLease
    {
        internal Property Property;
        internal LoadingDock Dock;
        internal ParkingLot Parking;
        internal GameObject Root;
        internal int Index;
        internal bool Attached;

        internal void CheckRemovable()
        {
            if (Dock != null && Dock.IsInUse) throw new InvalidOperationException($"property '{Property.PropertyCode}' dock {Index} is occupied");
            if (Parking?.ParkingSpots != null)
                foreach (var spot in Parking.ParkingSpots)
                    if (spot != null && spot.OccupantVehicle != null)
                        throw new InvalidOperationException($"property '{Property.PropertyCode}' dock {Index} has a parked vehicle");
            var deliveries = DeliveryManager.Instance;
            if (deliveries?.Deliveries != null)
                foreach (var delivery in deliveries.Deliveries)
                    if (delivery != null && delivery.DestinationCode == Property.PropertyCode && delivery.LoadingDockIndex == Index && delivery.Status != EDeliveryStatus.Completed)
                        throw new InvalidOperationException($"property '{Property.PropertyCode}' dock {Index} is referenced by delivery '{delivery.DeliveryID}'");
            if (deliveries?.DisplayedDeliveryHistory != null)
                foreach (var receipt in deliveries.DisplayedDeliveryHistory)
                    if (receipt != null && receipt.DestinationCode == Property.PropertyCode && receipt.LoadingDockIndex == Index)
                        throw new InvalidOperationException($"property '{Property.PropertyCode}' dock {Index} is referenced by retained delivery history");
        }

        internal void Remove()
        {
            if (Attached) CheckRemovable();
            if (Attached)
            {
                DockIndexPolicy.RequireTailRemoval(Property.LoadingDocks?.Length ?? 0, new[] { Index });
                if (Property.LoadingDocks[Index]?.Pointer != Dock.Pointer) throw new InvalidOperationException("added loading dock slot was replaced externally");
                Property.LoadingDocks = new Il2CppReferenceArray<LoadingDock>(Property.LoadingDocks.Take(Index).ToArray());
                Attached = false;
            }
            if (Root != null) Root.SetActive(false);
            ReleaseGuids();
            if (Root != null) Object.Destroy(Root);
        }

        internal void ReleaseGuids()
        {
            GuidLeases.Release(Dock, $"dock {Index}");
            GuidLeases.Release(Parking, $"dock {Index} parking");
        }
    }

    internal static class PropertyDocks
    {
        internal static IEnumerable<string> Validate(PropertyDockDefinition def)
        {
            if (def == null) { yield return "dock definition is null"; yield break; }
            if (string.IsNullOrWhiteSpace(def.Id) || string.IsNullOrWhiteSpace(def.TemplatePropertyCode)) yield return "dock needs an id and template property code";
            if (def.Index < 0 || def.TemplateDockIndex < 0) yield return "dock indices must be nonnegative";
            foreach (var point in new LocalPoint?[] { def.At, def.Entry, def.Exit, def.HiddenVehicleAccess })
                if (point.HasValue && (!float.IsFinite(point.Value.Position.x) || !float.IsFinite(point.Value.Position.y) ||
                    !float.IsFinite(point.Value.Position.z) || !float.IsFinite(point.Value.Yaw))) yield return "dock placement contains a non-finite coordinate";
        }

        /// <summary>
        /// Whether the dock has a place. A definition may get it after registration, so this is required when the dock is
        /// applied and only warned about when it is registered.
        /// </summary>
        internal static bool HasPlacement(PropertyDockDefinition def) => def.At.Position != Vector3.zero || def.At.Yaw != 0f;

        internal static void CheckRemovalOrder(IEnumerable<PropertyDockLease> leases)
        {
            foreach (var group in leases.Where(x => x.Attached).GroupBy(x => x.Property.Pointer))
            {
                var property = group.First().Property;
                DockIndexPolicy.RequireTailRemoval(property.LoadingDocks?.Length ?? 0, group.Select(x => x.Index));
                foreach (var lease in group)
                    if (property.LoadingDocks[lease.Index]?.Pointer != lease.Dock?.Pointer)
                        throw new InvalidOperationException($"property '{property.PropertyCode}' added loading dock slot {lease.Index} was replaced externally");
            }
        }

        internal static PropertyDockLease Apply(string expansion, string extension, Property property, PropertyDockDefinition def)
        {
            var errors = Validate(def).ToArray();
            if (errors.Length > 0) throw new InvalidOperationException(string.Join("; ", errors));
            if (!HasPlacement(def)) throw new InvalidOperationException($"dock '{def.Id}' has no At; a dock needs a world position");
            DockIndexPolicy.RequireAppend(property.LoadingDocks?.Length ?? 0, def.Index);
            Property sourceProperty = null;
            foreach (var candidate in Property.Properties)
                if (candidate != null && candidate.PropertyCode == def.TemplatePropertyCode) { sourceProperty = candidate; break; }
            if (sourceProperty?.LoadingDocks == null || def.TemplateDockIndex >= sourceProperty.LoadingDocks.Length)
                throw new InvalidOperationException($"dock '{def.Id}' template '{def.TemplatePropertyCode}' has no dock {def.TemplateDockIndex}");
            var source = sourceProperty.LoadingDocks[def.TemplateDockIndex];
            if (source == null) throw new InvalidOperationException($"dock '{def.Id}' template is null");
            var dockGuid = new Il2CppSystem.Guid(Places.StableGuid(expansion, "property-dock", extension + "/" + def.Id).ToString());
            var lotGuid = new Il2CppSystem.Guid(Places.StableGuid(expansion, "property-dock-lot", extension + "/" + def.Id).ToString());
            GuidLeases.RequireFree(dockGuid, $"dock '{def.Id}'");
            GuidLeases.RequireFree(lotGuid, $"dock '{def.Id}' parking");
            var root = new GameObject($"MapExpansions.{expansion}.{extension}.Dock.{def.Id}");
            root.SetActive(false);
            root.transform.SetParent(property.transform, true);
            var lease = new PropertyDockLease { Property = property, Root = root, Index = def.Index };
            try
            {
                var copy = Object.Instantiate(source.gameObject, root.transform);
                copy.name = def.Id;
                copy.transform.SetPositionAndRotation(def.At.Position, Quaternion.Euler(0, def.At.Yaw, 0));
                copy.transform.localScale = source.transform.lossyScale;
                var dock = copy.GetComponent<LoadingDock>();
                lease.Dock = dock;
                if (dock == null || copy.GetComponentsInChildren<LoadingDock>(true).Length != 1 || copy.GetComponentsInChildren<NetworkObject>(true).Length != 0)
                    throw new InvalidOperationException($"dock '{def.Id}' template must contain one local dock and no network objects");
                RequireInside(copy.transform, dock.VehicleDetector?.transform, "vehicle detector");
                RequireInside(copy.transform, dock.Parking?.transform, "parking lot");
                RequireInside(copy.transform, dock.uiPoint, "UI point");
                if (dock.accessPoints == null || dock.accessPoints.Length == 0) throw new InvalidOperationException("loading dock has no worker access point");
                foreach (var point in dock.accessPoints) RequireInside(copy.transform, point, "worker access point");
                var parking = dock.Parking;
                lease.Parking = parking;
                if (copy.GetComponentsInChildren<ParkingLot>(true).Length != 1) throw new InvalidOperationException("loading dock must contain exactly one parking lot");
                RequireInside(copy.transform, parking.EntryPoint, "road entry");
                RequireInside(copy.transform, parking.HiddenVehicleAccessPoint, "hidden vehicle access");
                if (parking.UseExitPoint) RequireInside(copy.transform, parking.ExitPoint, "road exit");
                dock.BakedGUID = dockGuid.ToString(); dock._GUID_k__BackingField = dockGuid;
                parking.BakedGUID = lotGuid.ToString(); parking._GUID_k__BackingField = lotGuid;
                dock.ParentProperty = property;
                dock._DynamicOccupant_k__BackingField = null; dock._StaticOccupant_k__BackingField = null;
                dock.InputSlots = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.ItemFramework.ItemSlot>();
                dock.OutputSlots = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.ItemFramework.ItemSlot>();
                foreach (var detector in copy.GetComponentsInChildren<Il2CppScheduleOne.DevUtilities.VehicleDetector>(true))
                {
                    detector.vehicles = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Vehicles.LandVehicle>();
                    detector.closestVehicle = null;
                }
                parking.ParkingSpots = new Il2CppSystem.Collections.Generic.List<ParkingSpot>();
                var spots = copy.GetComponentsInChildren<ParkingSpot>(true);
                if (spots.Length == 0) throw new InvalidOperationException("loading dock parking lot has no parking spot");
                foreach (var spot in spots)
                {
                    RequireInside(copy.transform, spot.AlignmentPoint, "parking alignment point");
                    spot._parentParentLot = parking;
                    spot._OccupantVehicle_k__BackingField = null;
                }
                OverridePoint(parking.EntryPoint, def.Entry);
                OverridePoint(parking.ExitPoint, def.Exit);
                OverridePoint(parking.HiddenVehicleAccessPoint, def.HiddenVehicleAccess);
                var docks = property.LoadingDocks?.ToList() ?? new List<LoadingDock>();
                docks.Add(dock);
                property.LoadingDocks = new Il2CppReferenceArray<LoadingDock>(docks.ToArray());
                lease.Attached = true;
                copy.SetActive(true);
                root.SetActive(true);
                if (Il2Cpp.GUIDManager.GetObject<LoadingDock>(dockGuid)?.Pointer != dock.Pointer ||
                    Il2Cpp.GUIDManager.GetObject<ParkingLot>(lotGuid)?.Pointer != parking.Pointer ||
                    property.LoadingDocks[def.Index]?.Pointer != dock.Pointer || dock.ParentProperty?.Pointer != property.Pointer || parking.ParkingSpots.Count != spots.Length)
                    throw new InvalidOperationException($"dock '{def.Id}' native registration readback failed");
                Core.Log.Msg($"property dock: '{expansion}/{extension}/{def.Id}' appended slot {def.Index} on '{property.PropertyCode}', GUID {dockGuid}");
                return lease;
            }
            catch (Exception original)
            {
                try { lease.Remove(); }
                catch (Exception cleanup) { throw new AggregateException("property dock build and cleanup failed", original, cleanup); }
                throw;
            }
        }

        private static void RequireInside(Transform root, Transform point, string role)
        {
            if (point == null || point.Pointer != root.Pointer && !point.IsChildOf(root))
                throw new InvalidOperationException($"loading dock {role} must be inside its copied hierarchy");
        }
        private static void OverridePoint(Transform target, LocalPoint? point)
        {
            if (!point.HasValue) return;
            if (target == null) throw new InvalidOperationException("loading dock override has no template point");
            target.SetPositionAndRotation(point.Value.Position, Quaternion.Euler(0, point.Value.Yaw, 0));
        }
    }
}
