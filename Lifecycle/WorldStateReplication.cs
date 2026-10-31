using HarmonyLib;
using Il2CppFishNet;
using Il2CppFishNet.Connection;
using Il2CppScheduleOne.Networking;

namespace MapExpansions.Lifecycle
{
    internal static class WorldStateReplication
    {
        private sealed class Gate
        {
            internal NetworkConnection Connection;
            internal PeerWorldStateAck Ack;
            internal bool Requested;
        }
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static readonly Dictionary<int, Gate> Gates = new();
        private static bool Enabled => Registry.Usable().Any(d => d.WorldStates.Count > 0);
        private static double Now => Clock.Elapsed.TotalSeconds;

        private static Gate For(NetworkConnection connection)
        {
            int peer = connection.ClientId;
            if (!Gates.TryGetValue(peer, out var gate) || gate.Connection.Pointer != connection.Pointer)
                Gates[peer] = gate = new Gate { Connection = connection, Ack = new PeerWorldStateAck(Now) };
            return gate;
        }
        internal static bool Require(int peerId, long revision)
        {
            if (!Enabled || !InstanceFinder.IsServer || revision < 0 ||
                !InstanceFinder.ServerManager.Clients.TryGetValue(peerId, out var connection) || connection.IsHost)
            { Core.Log.Error($"world state replication: cannot require revision {revision} for remote peer {peerId}"); return false; }
            var gate = For(connection);
            if (!gate.Ack.Require(revision, Now))
            { Core.Log.Error($"world state replication: rejected peer {peerId} revision {revision}, current {gate.Ack.Revision}, timedOut={gate.Ack.TimedOut}"); return false; }
            return true;
        }
        internal static bool Confirm(int peerId, long revision)
        {
            if (!Gates.TryGetValue(peerId, out var gate) || !gate.Ack.Confirm(revision))
            { Core.Log.Warning($"world state replication: peer {peerId} acknowledgement {revision} does not match a pending live revision"); return false; }
            Core.Log.Msg($"world state replication: peer {peerId} confirmed revision {revision}; native replication released");
            return true;
        }
        internal static void Reset() => Gates.Clear();

        private sealed class Held
        {
            internal readonly List<ReplicationQueue.ReplicationRequest> Requests = new();
            internal bool Restored;
            internal void Restore(ReplicationQueue queue)
            {
                if (Restored) return;
                // Withheld requests go before any new requests, retaining their original order for each peer.
                while (Requests.Count > 0)
                {
                    int i = Requests.Count - 1;
                    var request = Requests[i];
                    if (!queue.queue.Contains(request)) queue.queue.Insert(0, request);
                    Requests.RemoveAt(i);
                }
                Restored = true;
            }
        }

        [HarmonyPatch(typeof(ReplicationQueue), nameof(ReplicationQueue.Update))]
        private static class BeforeNativeReplication
        {
            private static void Prefix(ReplicationQueue __instance, out Held __state)
            {
                __state = new Held();
                if (!InstanceFinder.IsServer || !Enabled) return;
                var requestPeers = new HashSet<int>();
                try
                {
                    foreach (var pair in Gates.ToArray())
                    {
                        var gate = pair.Value;
                        if (!gate.Connection.IsValid) { Gates.Remove(pair.Key); continue; }
                        if (gate.Ack.Expire(Now))
                        {
                            Core.Log.Error($"world state replication: peer {pair.Key} failed revision {gate.Ack.Revision} ACK after {Now - gate.Ack.Started:0.00}s (limit {PeerWorldStateAck.TimeoutSeconds}s); disconnecting before native replication timeout");
                            gate.Connection.Disconnect(true);
                        }
                    }
                    for (int i = __instance.queue.Count - 1; i >= 0; i--)
                    {
                        var request = __instance.queue[i];
                        var target = request?.Target;
                        if (target == null || !target.IsValid || target.IsHost) continue;
                        var gate = For(target);
                        if (gate.Ack.Confirmed) continue;
                        __state.Requests.Insert(0, request);
                        __instance.queue.RemoveAt(i);
                        // requestsByConnection deliberately retains the task, so vanilla cannot signal replication complete.
                        if (!gate.Requested) { gate.Requested = true; requestPeers.Add(target.ClientId); }
                    }
                    foreach (int peer in requestPeers)
                    {
                        Core.Log.Msg($"world state replication: withholding peer {peer}'s native tasks until its snapshot ACK");
                        MapExpansionsApi.RaisePeerWorldStateRequested(peer);
                    }
                }
                catch (Exception e)
                {
                    Core.Log.Error($"world state replication: failed to withhold native tasks; original update will not execute: {e}");
                    __state.Restore(__instance);
                    throw;
                }
            }
            private static void Postfix(ReplicationQueue __instance, Held __state) => __state?.Restore(__instance);
            private static Exception Finalizer(ReplicationQueue __instance, Held __state, Exception __exception)
            {
                __state?.Restore(__instance);
                return __exception;
            }
        }
    }
}
