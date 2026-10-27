using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.Misc;
using Il2CppScheduleOne.Doors;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.CharacterCustomization;
using Il2CppScheduleOne.UI.Shop;
using MapExpansions.Lifecycle;
using MapExpansions.Navigation;
using MapExpansions.Source;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// Shops of expansions, run as in the main world: the seller's dialogue gets a choice that opens the shop's screen,
    /// shown while the seller stands at the counter, and the shop's doors let the player in only then. Every peer runs
    /// this for itself; the seller's place comes from the host like any NPC's.
    /// </summary>
    /// <remarks>
    /// A building's interior can have NavMesh that is not joined to the street, as in the prologue, whose shop doors
    /// were closed when it was baked. Each shop door that no path crosses gets a code link through it.
    /// </remarks>
    internal static class Shops
    {
        /// <summary>How far each end of a door link sits from the door, inside and outside.</summary>
        private const float DoorLinkReach = 1.3f;
        private const float DoorLinkWidth = 0.9f;
        /// <summary>Shop updates (two a second) a door waits for NavMesh on both its sides.</summary>
        private const int DoorTriesMax = 120;

        private sealed class Shop
        {
            public string Expansion;
            public ShopDefinition Def;
            public Vector3 Counter;
            /// <summary>Where the code definition has the counter, when a layer moved it; the seller's walks there are led to <see cref="Counter"/>.</summary>
            public Vector3? CodeCounter;
            /// <summary>The seller's walks were looked at once their schedule existed; see <see cref="RouteSeller"/>.</summary>
            public bool SellerRouted;
            public bool RouteWaitLogged;
            /// <summary>Where the seller looks while serving, flat in world space.</summary>
            public Vector3 Facing;
            public readonly List<MainCopies.Part> Parts = new();
            public readonly List<DoorController> Doors = new();
            /// <summary>Lamps that are on while the shop is open, as its main-world original's access zone has them.</summary>
            public readonly List<ToggleableLight> Lights = new();
            public readonly HashSet<int> CheckedDoors = new();
            public readonly Dictionary<int, int> DoorTries = new();
            public NPC Seller;
            public DialogueController.DialogueChoice Choice;
            public bool? Open;
            public float NextSellerLookup;
            public bool SellerMissingLogged;
            /// <summary>Takes a point of the main-world shop to the same point of this one; null when nothing moves.</summary>
            public Matrix4x4? Stage;
        }

        private static readonly List<Shop> All = new();
        private static float _nextTick;

        /// <summary>Main-world objects moved into an expansion's shop while its screen is open, with their own places.</summary>
        private static readonly List<(Transform t, Vector3 pos, Quaternion rot)> Staged = new();
        private static BarbershopUI _stagedScreen;
        private static bool _stagedSeenOpen;
        private static float _stagedSince;
        private const float StageWaitSeconds = 5f;

        public static void Build(BuiltExpansion b)
        {
            var defs = b.Definition.Shops;
            if (defs.Count == 0)
            {
                Core.Log.Msg($"shops: '{b.Id}' has no shops");
                return;
            }
            foreach (var d in defs)
            {
                try
                {
                    if (string.IsNullOrEmpty(d.Id) || string.IsNullOrEmpty(d.Seller))
                    {
                        Core.Log.Error($"shops: '{b.Id}' a shop has no id or seller ('{d.Id}', '{d.Seller}'); it is not built");
                        continue;
                    }
                    if (d.Kind == ShopKind.Store && string.IsNullOrEmpty(d.ShopCode))
                    {
                        Core.Log.Error($"shops: '{b.Id}' shop '{d.Id}' sells from no shop code; it is not built");
                        continue;
                    }
                    var s = new Shop { Expansion = b.Id, Def = d, Counter = b.Root.transform.TransformPoint(d.Counter.Position) };
                    var facing = b.Root.transform.rotation * Quaternion.Euler(0f, d.Counter.Yaw, 0f) * Vector3.forward;
                    facing.y = 0f;
                    s.Facing = facing.normalized;
                    if (Layers.CodeCounter(b.Id, d.Id) is LocalPoint code)
                    {
                        var codeWorld = b.Root.transform.TransformPoint(code.Position);
                        if ((codeWorld - s.Counter).sqrMagnitude > 0.01f) s.CodeCounter = codeWorld;
                    }
                    foreach (var c in MainCopies.Build(b, d.Copies, $"shop '{d.Id}'", Lifecycle.Layers.ShopOwner(d.Id)))
                    {
                        s.Parts.AddRange(c.Parts);
                        s.Lights.AddRange(c.ZoneLights);
                        if (c.Local != null) s.Doors.AddRange(c.Local.GetComponentsInChildren<DoorController>(true));
                    }
                    if (d.Kind == ShopKind.Barbershop) s.Stage = StageOf(b, d);
                    All.Add(s);
                    Core.Log.Msg($"shops: '{b.Id}' shop '{d.Id}' ({d.Kind}{(d.Kind == ShopKind.Store ? " " + d.ShopCode : "")}) " +
                                 $"sold by '{d.Seller}' at {s.Counter}, {s.Parts.Count} networked parts, {s.Lights.Count} lamps on while open");
                }
                catch (Exception e)
                {
                    Core.Log.Error($"shops: '{b.Id}' shop '{d.Id}' failed: {e}");
                }
            }
        }

        /// <summary>
        /// A layer moved the counter, while the seller's schedule still walks to where the code has it. Every walk of
        /// the seller that ends at the code's counter ends at the layer's counter instead.
        /// </summary>
        /// <returns>Whether the seller's schedule was there to look at; until then it is asked again.</returns>
        private static bool RouteSeller(Shop s, Vector3 codeCounter)
        {
            var manager = s.Seller.GetComponentInChildren<NPCScheduleManager>(true);
            var actions = manager != null ? manager.ActionList : null;
            if (actions == null || actions.Count == 0)
            {
                if (!s.RouteWaitLogged)
                    Core.Log.Msg($"shops: '{s.Expansion}' shop '{s.Def.Id}': the schedule of '{s.Def.Seller}' is not built yet; " +
                                 $"its walks are led to the moved counter at {s.Counter} once it is");
                s.RouteWaitLogged = true;
                return false;
            }
            var target = s.Counter;
            if (NavMesh.SamplePosition(target, out var hit, 5f, NavMesh.AllAreas)) target.y = hit.position.y;
            var turn = s.Facing.sqrMagnitude > 0.001f ? Quaternion.LookRotation(s.Facing) : Quaternion.identity;
            int walks = 0, moved = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var walk = actions[i]?.TryCast<Il2CppScheduleOne.NPCs.Schedules.NPCSignal_WalkToLocation>();
                if (walk == null || walk.Destination == null) continue;
                walks++;
                var d = walk.Destination.position;
                if (new Vector2(d.x - codeCounter.x, d.z - codeCounter.z).magnitude > s.Def.CounterReach) continue;
                walk.Destination.SetPositionAndRotation(target, turn);
                moved++;
            }
            if (moved == 0)
                Core.Log.Warning($"shops: '{s.Expansion}' shop '{s.Def.Id}': none of the {walks} walks among the {actions.Count} actions of '{s.Def.Seller}' ends at the code's counter {codeCounter}; " +
                                 $"they may never reach the moved counter at {s.Counter}");
            else
                Core.Log.Msg($"shops: '{s.Expansion}' shop '{s.Def.Id}': {moved} of the {walks} walks of '{s.Def.Seller}' now end at the moved counter {target} instead of {codeCounter}");
            return true;
        }

        private static Matrix4x4? StageOf(BuiltExpansion b, ShopDefinition d)
        {
            if (string.IsNullOrEmpty(d.MainFrame) || string.IsNullOrEmpty(d.Onto))
            {
                Core.Log.Warning($"shops: '{b.Id}' barbershop '{d.Id}' names no main frame or expansion object; its screen shows the main barbershop's chair where it stands");
                return null;
            }
            var main = MainCopies.FindMain(d.MainFrame);
            var onto = MainCopies.FindInExpansion(b, d.Onto);
            if (main == null || onto == null)
            {
                Core.Log.Error($"shops: '{b.Id}' barbershop '{d.Id}': {(main == null ? $"main-world '{d.MainFrame}'" : $"expansion '{d.Onto}'")} not found; its screen shows the main barbershop's chair where it stands");
                return null;
            }
            return Matrix4x4.TRS(onto.position, onto.rotation, Vector3.one) * Matrix4x4.TRS(main.position, main.rotation, Vector3.one).inverse;
        }

        public static void Clear()
        {
            Unstage("the expansion is torn down");
            All.Clear();
        }

        public static void Tick()
        {
            // The screen reports open a frame or more after Open(); the parts stay until it has been open once.
            if (_stagedScreen != null)
            {
                if (_stagedScreen.IsOpen) _stagedSeenOpen = true;
                else if (_stagedSeenOpen) Unstage("the screen closed");
                else if (Time.unscaledTime > _stagedSince + StageWaitSeconds) Unstage($"the screen did not open within {StageWaitSeconds} s");
            }
            if (All.Count == 0 || Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + 0.5f;
            foreach (var s in All)
            {
                try
                {
                    TickShop(s);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"shops: '{s.Expansion}' shop '{s.Def.Id}' update failed: {e}");
                    s.NextSellerLookup = Time.unscaledTime + 10f;
                }
            }
        }

        private static void TickShop(Shop s)
        {
            if (s.Seller == null)
            {
                if (Time.unscaledTime < s.NextSellerLookup) return;
                s.NextSellerLookup = Time.unscaledTime + 5f;
                s.Seller = NPCManager.GetNPC(s.Def.Seller);
                if (s.Seller == null)
                {
                    if (!s.SellerMissingLogged) Core.Log.Warning($"shops: '{s.Expansion}' shop '{s.Def.Id}' waits for its seller '{s.Def.Seller}'");
                    s.SellerMissingLogged = true;
                    return;
                }
                s.Choice = AddChoice(s);
                if (s.Choice == null) return;
            }
            if (s.CodeCounter is Vector3 codeCounter && !s.SellerRouted) s.SellerRouted = RouteSeller(s, codeCounter);

            foreach (var p in s.Parts)
            {
                if (p.Instance == null) continue;
                var door = p.Instance.GetComponent<DoorController>();
                if (door != null && !s.Doors.Contains(door)) s.Doors.Add(door);
            }
            foreach (var door in s.Doors)
                if (door != null && !s.CheckedDoors.Contains(door.GetInstanceID()) && LinkDoor(s, door)) s.CheckedDoors.Add(door.GetInstanceID());

            var npc = s.Seller;
            var pos = npc.transform.position;
            var flat = new Vector2(pos.x - s.Counter.x, pos.z - s.Counter.z);
            bool open = npc.IsConscious && !npc.isInBuilding && flat.magnitude <= s.Def.CounterReach && Mathf.Abs(pos.y - s.Counter.y) < 1.5f;
            if (s.Choice != null) s.Choice.Enabled = open;
            var access = open ? EDoorAccess.Open : EDoorAccess.ExitOnly;
            foreach (var door in s.Doors)
            {
                if (door == null) continue;
                door.PlayerAccess = access;
                // The game raises the blocker when an NPC passes a door the player may not use, and lowers it only when
                // that NPC's opening times out. A door that already stood open keeps it raised after the shop opens.
                var blocker = door.PlayerBlocker;
                if (open && blocker != null && blocker.enabled)
                {
                    blocker.enabled = false;
                    Core.Log.Msg($"shops: '{s.Expansion}' shop '{s.Def.Id}' door '{door.gameObject.name}' let the player through again; its blocker was still up");
                }
            }
            if (open) FaceCounter(s, npc);
            if (s.Open != open)
            {
                int lit = 0;
                foreach (var l in s.Lights)
                {
                    if (l == null) continue;
                    l.isOn = open;
                    lit++;
                }
                Core.Log.Msg($"shops: '{s.Expansion}' shop '{s.Def.Id}' {(open ? "opens" : "closes")}: '{s.Def.Seller}' at {pos}, " +
                             $"{flat.magnitude:0.0} m from the counter, {s.Doors.Count} doors, {lit} lamps {(open ? "on" : "off")}");
                s.Open = open;
            }
        }

        private const float FaceTolerance = 30f;

        /// <summary>
        /// Turns a standing seller to the counter's facing. A walk the schedule skipped ends with a warp that keeps the
        /// direction the seller came from, so a seller can stand at the counter with the back to the door.
        /// </summary>
        private static void FaceCounter(Shop s, NPC npc)
        {
            if (!Il2CppFishNet.InstanceFinder.IsServer) return;
            var movement = npc.Movement;
            if (movement == null || movement.IsMoving || movement.FaceDirectionInProgress) return;
            if (npc.DialogueHandler != null && npc.DialogueHandler.IsDialogueInProgress) return;
            var forward = npc.transform.forward;
            forward.y = 0f;
            float off = Vector3.Angle(forward, s.Facing);
            if (off <= FaceTolerance) return;
            movement.FaceDirection(s.Facing);
            Core.Log.Msg($"shops: '{s.Expansion}' shop '{s.Def.Id}' turns '{s.Def.Seller}' {off:0} degrees to face the counter");
        }

        /// <summary>
        /// Adds a link through the door for every agent type that has NavMesh on both sides but no path across. False
        /// while no agent type has NavMesh on both sides yet, since the expansion's NavMesh can arrive after its doors.
        /// </summary>
        private static bool LinkDoor(Shop s, DoorController door)
        {
            string name = $"shops: '{s.Expansion}' shop '{s.Def.Id}' door '{door.gameObject.name}'";
            var inner = door.InteriorIntObjs;
            var outer = door.ExteriorIntObjs;
            if (inner == null || inner.Length == 0 || inner[0] == null || outer == null || outer.Length == 0 || outer[0] == null)
            {
                Core.Log.Warning($"{name} has no interior or exterior handle; NPCs cross it only where the NavMesh does");
                return true;
            }
            Vector3 a = inner[0].transform.position, b = outer[0].transform.position;
            var axis = b - a;
            axis.y = 0f;
            if (axis.sqrMagnitude < 0.0001f)
            {
                Core.Log.Warning($"{name}: its handles stand on one spot; NPCs cross it only where the NavMesh does");
                return true;
            }
            axis.Normalize();
            var mid = (a + b) * 0.5f;
            Vector3 inside = mid - axis * DoorLinkReach, outside = mid + axis * DoorLinkReach;

            var agents = new List<int>();
            var notes = new List<string>();
            var path = new NavMeshPath();
            int bothSides = 0;
            foreach (int agent in NpcNavigation.AgentTypes())
            {
                var filter = new NavMeshQueryFilter { agentTypeID = agent, areaMask = NavMesh.AllAreas };
                string agentName = NpcNavigation.AgentName(agent);
                bool hasIn = NavMesh.SamplePosition(inside, out NavMeshHit hitIn, 1.5f, filter);
                bool hasOut = NavMesh.SamplePosition(outside, out NavMeshHit hitOut, 1.5f, filter);
                if (!hasIn || !hasOut)
                {
                    notes.Add($"{agentName}: no NavMesh {(hasIn ? "outside" : hasOut ? "inside" : "on either side")}");
                    continue;
                }
                bothSides++;
                if (NavMesh.CalculatePath(hitIn.position, hitOut.position, filter, path) && path.status == NavMeshPathStatus.PathComplete
                    && PathLength(path) < DoorLinkReach * 4f)
                {
                    notes.Add($"{agentName}: already walkable");
                    continue;
                }
                agents.Add(agent);
            }
            if (bothSides == 0)
            {
                int id = door.GetInstanceID();
                s.DoorTries.TryGetValue(id, out int tries);
                s.DoorTries[id] = ++tries;
                if (tries < DoorTriesMax) return false;
                Core.Log.Warning($"{name}: after {tries} tries no agent type has NavMesh on both sides ({string.Join(", ", notes)}); it gets no link");
                return true;
            }
            if (agents.Count == 0)
            {
                Core.Log.Msg($"{name} needs no link ({string.Join(", ", notes)})");
                return true;
            }
            Core.Log.Msg($"{name}: no path through it for {string.Join(", ", agents.Select(NpcNavigation.AgentName))}; linking {inside} to {outside}" +
                         (notes.Count > 0 ? $" ({string.Join(", ", notes)})" : ""));
            NavLinks.Add(inside, outside, DoorLinkWidth, agents);
            return true;
        }

        private static float PathLength(NavMeshPath path)
        {
            var corners = path.corners;
            float length = 0f;
            for (int i = 1; i < corners.Length; i++) length += Vector3.Distance(corners[i - 1], corners[i]);
            return length;
        }

#if DEBUG
        /// <summary>
        /// Picks the seller's shop choice of every shop whose id starts with <paramref name="id"/>, as the player would
        /// in the dialogue; a closed shop's choice is picked anyway and says so. No id lists the shops.
        /// </summary>
        public static void Choose(string id)
        {
            if (string.Equals(id, "close", StringComparison.OrdinalIgnoreCase))
            {
                CloseScreens();
                return;
            }
            int n = 0;
            foreach (var s in All)
            {
                if (!string.IsNullOrEmpty(id) && !s.Def.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)) continue;
                n++;
                if (string.IsNullOrEmpty(id) || s.Choice == null)
                {
                    Core.Log.Msg($"shops: '{s.Expansion}' shop '{s.Def.Id}' ({s.Def.Kind}), seller '{s.Def.Seller}', open {s.Open?.ToString() ?? "unknown"}, " +
                                 $"choice {(s.Choice == null ? "not added yet" : "ready")}, {s.Doors.Count} doors, {s.CheckedDoors.Count} checked");
                    continue;
                }
                Core.Log.Msg($"shops: picking \"{s.Choice.ChoiceText}\" of '{s.Def.Id}' (shown to the player: {s.Choice.Enabled})");
                s.Choice.onChoosen.Invoke();
            }
            if (n == 0) Core.Log.Warning($"shops: no shop id starts with '{id}'");
        }

        /// <summary>Closes every store, pawn shop and barbershop screen that is open.</summary>
        private static void CloseScreens()
        {
            int closed = 0;
            var all = ShopInterface.AllShops;
            for (int i = 0; all != null && i < all.Count; i++)
            {
                if (all[i] == null || !all[i].IsOpen) continue;
                all[i].SetIsOpen(false);
                closed++;
            }
            var pawn = Il2CppScheduleOne.DevUtilities.Singleton<PawnShopInterface>.Instance;
            if (pawn != null && pawn.IsOpen)
            {
                pawn.Close();
                closed++;
            }
            foreach (var barber in Object.FindObjectsOfType<BarbershopUI>(true))
            {
                if (!barber.IsOpen) continue;
                barber.Close();
                closed++;
            }
            Core.Log.Msg($"shops: {closed} screens closed");
        }
#endif

        private static DialogueController.DialogueChoice AddChoice(Shop s)
        {
            var controller = s.Seller.DialogueHandler != null ? s.Seller.DialogueHandler.GetComponent<DialogueController>() : null;
            if (controller == null) controller = s.Seller.GetComponentInChildren<DialogueController>(true);
            if (controller == null)
            {
                Core.Log.Error($"shops: '{s.Expansion}' seller '{s.Def.Seller}' has no dialogue controller; shop '{s.Def.Id}' cannot sell");
                return null;
            }
            var choice = new DialogueController.DialogueChoice
            {
                ChoiceText = string.IsNullOrEmpty(s.Def.ChoiceText) ? DefaultText(s.Def.Kind) : s.Def.ChoiceText,
                Enabled = false,
                ShowWorldspaceDialogue = true,
            };
            if (choice.onChoosen == null) choice.onChoosen = new UnityEvent();
            var def = s.Def;
            string expansion = s.Expansion;
            UnityAction chosen = new Action(() => OpenScreen(s));
            choice.onChoosen.AddListener(chosen);
            controller.Choices.Add(choice);
            Core.Log.Msg($"shops: '{expansion}' seller '{def.Seller}' offers \"{choice.ChoiceText}\" for shop '{def.Id}' (choice {controller.Choices.Count - 1})");
            return choice;
        }

        /// <summary>Moves the main barbershop's chair and camera into the expansion's shop until the screen closes.</summary>
        private static void StageBarbershop(BarbershopUI screen, Matrix4x4 move, string expansion, string id)
        {
            Unstage("another shop opens");
            var shop = screen.CharacterCustomizationShop;
            if (shop == null)
            {
                Core.Log.Error($"shops: '{expansion}' barbershop '{id}': the screen has no customization shop; it shows the main barbershop");
                return;
            }
            var parts = new List<Transform>();
            if (shop.CameraPosition != null) parts.Add(shop.CameraPosition);
            if (shop.RigContainer != null) parts.Add(shop.RigContainer.parent != null && shop.RigContainer.parent != shop.transform ? shop.RigContainer.parent : shop.RigContainer);
            if (parts.Count < 2) Core.Log.Warning($"shops: '{expansion}' barbershop '{id}': camera or chair of the screen is missing; {parts.Count} part moves");
            foreach (var t in parts)
            {
                Staged.Add((t, t.position, t.rotation));
                var m = move * Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                t.SetPositionAndRotation(m.GetColumn(3), m.rotation);
            }
            _stagedScreen = screen;
            _stagedSeenOpen = false;
            _stagedSince = Time.unscaledTime;
            Core.Log.Msg($"shops: '{expansion}' barbershop '{id}': {parts.Count} parts of the main barbershop stand in it while the screen is open");
        }

        private static void Unstage(string why)
        {
            if (Staged.Count == 0)
            {
                _stagedScreen = null;
                return;
            }
            int n = 0;
            foreach (var (t, pos, rot) in Staged)
            {
                if (t == null) continue;
                t.SetPositionAndRotation(pos, rot);
                n++;
            }
            Core.Log.Msg($"shops: {n}/{Staged.Count} barbershop parts back in the main world ({why})");
            Staged.Clear();
            _stagedScreen = null;
        }

        private static string DefaultText(ShopKind kind) => kind switch
        {
            ShopKind.PawnShop => "I'd like to pawn something",
            ShopKind.Barbershop => "I'd like a hair cut",
            _ => "I'd like to purchase something",
        };

        private static void OpenScreen(Shop s)
        {
            string expansion = s.Expansion;
            var d = s.Def;
            try
            {
                switch (d.Kind)
                {
                    case ShopKind.Store:
                        var all = ShopInterface.AllShops;
                        ShopInterface shop = null;
                        for (int i = 0; all != null && i < all.Count; i++)
                            if (all[i] != null && all[i].ShopCode == d.ShopCode) { shop = all[i]; break; }
                        if (shop == null)
                        {
                            Core.Log.Error($"shops: '{expansion}' shop '{d.Id}': no main-world shop '{d.ShopCode}'; nothing opens");
                            return;
                        }
                        shop.SetIsOpen(true);
                        break;
                    case ShopKind.PawnShop:
                        var pawn = Il2CppScheduleOne.DevUtilities.Singleton<PawnShopInterface>.Instance;
                        if (pawn == null)
                        {
                            Core.Log.Error($"shops: '{expansion}' shop '{d.Id}': the pawn shop screen is missing; nothing opens");
                            return;
                        }
                        pawn.Open();
                        break;
                    case ShopKind.Barbershop:
                        var barber = Object.FindObjectsOfType<BarbershopUI>(true).FirstOrDefault();
                        if (barber == null)
                        {
                            Core.Log.Error($"shops: '{expansion}' shop '{d.Id}': the barbershop screen is missing; nothing opens");
                            return;
                        }
                        if (s.Stage.HasValue) StageBarbershop(barber, s.Stage.Value, expansion, d.Id);
                        barber.Open();
                        break;
                }
                Core.Log.Msg($"shops: '{expansion}' shop '{d.Id}' screen opened ({d.Kind})");
            }
            catch (Exception e)
            {
                Core.Log.Error($"shops: '{expansion}' shop '{d.Id}' screen failed to open: {e}");
            }
        }
    }
}
