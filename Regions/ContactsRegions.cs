using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.UI.Phone.ContactsApp;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace MapExpansions.Regions
{
    /// <summary>
    /// Gives every expansion region a tab in the phone's contacts app, so its people get a place on the relationship
    /// map. A tab is a copy of the last vanilla tab, one step further along the row, with an empty page of its own.
    /// </summary>
    internal static class ContactsRegions
    {
        [HarmonyPatch(typeof(ContactsApp), "Start")]
        private static class StartPatch
        {
            private static void Prefix(ContactsApp __instance)
            {
                try
                {
                    Add(__instance);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"contacts: adding the expansion regions failed; their people are not on the relationship map: {e}");
                }
            }
        }

        private static void Add(ContactsApp app)
        {
            if (RegionRegistry.All.Count == 0)
            {
                Core.Log.Msg("contacts: no expansion region; the contacts app keeps its tabs");
                return;
            }
            var old = app.RegionUIs;
            if (old == null || old.Length < 2)
            {
                Core.Log.Error($"contacts: the contacts app has {old?.Length ?? 0} region tabs; no expansion tab added");
                return;
            }
            var list = new List<ContactsApp.RegionUI>();
            for (int i = 0; i < old.Length; i++) list.Add(old[i]);

            foreach (var e in RegionRegistry.All)
            {
                if (list.Any(r => r.Region == e.Region))
                {
                    Core.Log.Warning($"contacts: region {(int)e.Region} already has a tab");
                    continue;
                }
                var last = list[list.Count - 1];
                var before = list[list.Count - 2];
                var step = last.Button.GetComponent<RectTransform>().anchoredPosition - before.Button.GetComponent<RectTransform>().anchoredPosition;

                var button = Object.Instantiate(last.Button.gameObject, last.Button.transform.parent).GetComponent<Button>();
                button.name = $"{e.Definition.Name} Button";
                button.onClick.RemoveAllListeners();
                var rect = button.GetComponent<RectTransform>();
                rect.anchoredPosition = last.Button.GetComponent<RectTransform>().anchoredPosition + step;
                int labels = 0;
                foreach (var t in button.GetComponentsInChildren<Text>(true)) { t.text = e.Definition.Name; labels++; }
                foreach (var t in button.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(true)) { t.text = e.Definition.Name; labels++; }
                if (labels == 0) Core.Log.Warning($"contacts: the tab of region {(int)e.Region} has no label to name it '{e.Definition.Name}'");

                var ui = new ContactsApp.RegionUI
                {
                    Region = e.Region,
                    Button = button,
                    Container = EmptyLike(last.Container, $"{e.Definition.Name} Circles"),
                    ConnectionsContainer = EmptyLike(last.ConnectionsContainer, $"{e.Definition.Name} Connections"),
                };
                list.Add(ui);
                Core.Log.Msg($"contacts: region {(int)e.Region} '{e.Definition.Name}' has a tab at {rect.anchoredPosition} ({labels} labels)");
            }

            var arr = new Il2CppReferenceArray<ContactsApp.RegionUI>(list.Count);
            for (int i = 0; i < list.Count; i++) arr[i] = list[i];
            app.RegionUIs = arr;
        }

        /// <summary>An empty page laid out like the given one, beside it.</summary>
        private static RectTransform EmptyLike(RectTransform like, string name)
        {
            var go = new GameObject(name, Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(like.parent, false);
            rect.anchorMin = like.anchorMin;
            rect.anchorMax = like.anchorMax;
            rect.pivot = like.pivot;
            rect.anchoredPosition = like.anchoredPosition;
            rect.sizeDelta = like.sizeDelta;
            rect.localScale = like.localScale;
            rect.SetSiblingIndex(like.GetSiblingIndex() + 1);
            go.SetActive(false);
            return rect;
        }

        /// <summary>
        /// S1API places the circles of a region without native circles around the copied template, which can sit at
        /// the edge of the scroll area where the map cannot scroll far enough to show them. The page is moved so its
        /// people sit in the middle of what the map shows, then the first of them is selected, like the first contact of a
        /// vanilla region. Returns the number of circles on the page.
        /// </summary>
        private static int CentreAndSelect(ContactsApp app, ContactsApp.RegionUI ui)
        {
            var circles = ui.Container.GetComponentsInChildren<Il2CppScheduleOne.UI.Relations.RelationCircle>(true);
            if (circles.Length == 0)
            {
                Core.Log.Msg($"contacts: region {(int)ui.Region} has no people on its page yet");
                return 0;
            }

            var centroid = Vector3.zero;
            foreach (var c in circles) centroid += c.Rect.position;
            centroid /= circles.Length;
            var view = app.ScrollRect.viewport != null ? app.ScrollRect.viewport : app.ScrollRect.GetComponent<RectTransform>();
            var middle = view.TransformPoint(view.rect.center);
            var shift = ui.Container.parent.InverseTransformVector(middle - centroid);
            shift.z = 0f;
            if (shift.sqrMagnitude > 1f)
            {
                ui.Container.localPosition += shift;
                var lines = ui.ConnectionsContainer.parent.InverseTransformVector(middle - centroid);
                lines.z = 0f;
                ui.ConnectionsContainer.localPosition += lines;
                Core.Log.Msg($"contacts: region {(int)ui.Region} page moved by {shift} to the middle of the map");
            }

            // Vanilla's Select also zooms to the circle, and that zoom stops at the edge of the scroll area, which
            // pushes the rest of the page out of view. The page is already centred, so only the panel and the ring move.
            app.uiPanel.ResetSnappedItem();
            app.DetailPanel.Open(circles[0].AssignedNPC);
            app.SelectionIndicator.position = circles[0].Rect.position;
            return circles.Length;
        }

        /// <summary>
        /// Vanilla shows the cartel influence of a region and the unlock rule of the next one by number. An expansion
        /// region has neither, so its tab shows only its own people.
        /// </summary>
        [HarmonyPatch(typeof(ContactsApp), nameof(ContactsApp.SetSelectedRegion))]
        private static class SelectPatch
        {
            private static bool Prefix(ContactsApp __instance, EMapRegion region)
            {
                if (!RegionRegistry.IsOurs(region)) return true;
                try
                {
                    var map = Singleton<Map>.Instance;
                    var data = map.GetRegionData(region);
                    if (data == null)
                    {
                        Core.Log.Error($"contacts: region {(int)region} has no map data; vanilla selects it");
                        return true;
                    }
                    __instance.SelectedRegion = region;
                    __instance.uiPanel.LockMapInput = !data.IsUnlocked;
                    foreach (var ui in __instance.RegionUIs)
                    {
                        var other = map.GetRegionData(ui.Region);
                        ui.Container.gameObject.SetActive(ui.Region == region);
                        ui.ConnectionsContainer.gameObject.SetActive(ui.Region == region);
                        ui.Button.interactable = ui.Region != region;
                        var locked = ui.Button.transform.Find("Locked");
                        if (locked != null && other != null) locked.gameObject.SetActive(!other.IsUnlocked);
                    }
                    __instance.ScrollRect.vertical = data.IsUnlocked;
                    __instance.ScrollRect.horizontal = data.IsUnlocked;
                    __instance.ScrollRect.enabled = data.IsUnlocked;
                    __instance.LowerContainer.gameObject.SetActive(false);
                    __instance.HorizontalScrollbarRectTransform.anchoredPosition = new Vector2(0f, 7f);
                    __instance.RegionLockedContainer.gameObject.SetActive(!data.IsUnlocked);
                    var ours = __instance.RegionUIs.FirstOrDefault(r => r.Region == region);
                    int circles = ours != null ? CentreAndSelect(__instance, ours) : 0;
                    Core.Log.Msg($"contacts: region {(int)region} '{data.Name}' selected ({circles} people)");
                    return false;
                }
                catch (Exception e)
                {
                    Core.Log.Error($"contacts: selecting region {(int)region} failed; vanilla selects it: {e}");
                    return true;
                }
            }
        }
    }
}
