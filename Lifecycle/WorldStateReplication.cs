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
            /// <summary>
            /// Native tasks of this peer, in their original order. They stay out of the queue until the acknowledgement;
            /// requestsByConnection keeps them counted, so vanilla cannot signal replication complete.
            /// </summary>
            internal readonly List<ReplicationQueue.ReplicationRequest> Held = new();
            internal int Requests;
            internal string LastFailure;
        }
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static readonly Dictionary<int, Gate> Gates = new();
        // Requests of connections that are gone. Vanilla drops an invalid request and cleans its bookkeeping.
        private static readonly List<ReplicationQueue.ReplicationRequest> Orphaned = new();
        // Reused between frames, so an update with nothing to do allocates nothing.
        private static readonly List<ReplicationQueue.ReplicationRequest> Released = new();
        private static readonly List<int> Dead = new();
        private static readonly List<int> Expired = new();
        private static readonly List<int> Due = new();
        private static double Now => Clock.Elapsed.TotalSeconds;

        private static Gate For(NetworkConnection connection)
        {
            int peer = connection.ClientId;
            if (Gates.TryGetValue(peer, out var gate) && gate.Connection.Pointer == connection.Pointer) return gate;
            if (gate != null) Orphaned.AddRange(gate.Held);
            Gates[peer] = gate = new Gate { Connection = connection, Ack = new PeerWorldStateAck(Now) };
            return gate;
        }
        internal static bool Require(int peerId, long revision)
        {
            if (!Registry.HasWorldStates || !InstanceFinder.IsServer || revision < 0 ||
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
        internal static void Reset()
        {
            int held = Gates.Values.Sum(g => g.Held.Count) + Orphaned.Count + Released.Count;
            if (held > 0) Core.Log.Msg($"world state replication: {held} held native tasks dropped with the session");
            Gates.Clear();
            Orphaned.Clear();
            Released.Clear();
        }

        private static void Sweep(ReplicationQueue native)
        {
            var queue = native.queue;
            double now = Now;
            Dead.Clear();
            Expired.Clear();
            Due.Clear();
            foreach (var pair in Gates)
            {
                var gate = pair.Value;
                if (!gate.Connection.IsValid)
                {
                    Orphaned.AddRange(gate.Held);
                    gate.Held.Clear();
                    Dead.Add(pair.Key);
                }
                else if (gate.Ack.Confirmed)
                {
                    Released.AddRange(gate.Held);
                    gate.Held.Clear();
                }
                else if (gate.Ack.Expire(now)) Expired.Add(pair.Key);
            }
            foreach (int peer in Dead)
            {
                Gates.Remove(peer);
                Core.Log.Msg($"world state replication: peer {peer} left; its gate is closed");
            }
            InsertAtFront(queue, Released);
            InsertAtFront(queue, Orphaned);
            foreach (int peer in Expired) Kick(peer, Gates[peer], now);

            for (int i = 0; i < queue.Count;)
            {
                var request = queue[i];
                var target = request?.Target;
                if (target == null || !target.IsValid || target.IsHost) { i++; continue; }
                var gate = For(target);
                if (gate.Ack.Confirmed) { i++; continue; }
                gate.Held.Add(request);
                queue.RemoveAt(i);
            }

            foreach (var pair in Gates)
                if (pair.Value.Ack.ShouldRequest(now)) Due.Add(pair.Key);
            foreach (int peer in Due) RequestSnapshot(peer, Gates[peer], now);
        }

        /// <summary>Earlier tasks go before new ones and keep their order. Each is taken off the list once it is back.</summary>
        private static void InsertAtFront(Il2CppSystem.Collections.Generic.List<ReplicationQueue.ReplicationRequest> queue, List<ReplicationQueue.ReplicationRequest> requests)
        {
            for (int i = requests.Count - 1; i >= 0; i--)
            {
                queue.Insert(0, requests[i]);
                requests.RemoveAt(i);
            }
        }

        private static void RequestSnapshot(int peer, Gate gate, double now)
        {
            gate.Requests++;
            if (gate.Requests == 1)
                Core.Log.Msg($"world state replication: withholding peer {peer}'s native tasks until its snapshot ACK ({gate.Held.Count} held)");
            else
                Core.Log.Warning($"world state replication: peer {peer} has not acknowledged revision {gate.Ack.Revision} after {now - gate.Ack.Started:0.0}s; " +
                                 $"snapshot request {gate.Requests}, {gate.Held.Count} native tasks held, last request: {gate.LastFailure ?? "delivered to the consumer"}");
            gate.LastFailure = MapExpansionsApi.RaisePeerWorldStateRequested(peer);
        }

        private static void Kick(int peer, Gate gate, double now)
        {
            string cause = gate.LastFailure ?? (gate.Ack.Revision < 0 ? "the consumer never required a revision for this peer" : "the peer did not acknowledge the snapshot");
            Core.Log.Error($"world state replication: disconnecting peer {peer} during its join: no acknowledgement of revision {gate.Ack.Revision} after " +
                           $"{now - gate.Ack.Started:0.00}s (limit {PeerWorldStateAck.TimeoutSeconds}s), {gate.Requests} snapshot requests, {gate.Held.Count} native tasks held; cause: {cause}");
            gate.Connection.Disconnect(true);
        }

        [HarmonyPatch(typeof(ReplicationQueue), nameof(ReplicationQueue.Update))]
        private static class BeforeNativeReplication
        {
            private static void Prefix(ReplicationQueue __instance)
            {
                // Runs every frame: installs without world states and idle hosts leave here without allocating.
                if (!Registry.HasWorldStates || !InstanceFinder.IsServer) return;
                if (Gates.Count == 0 && __instance.queue.Count == 0) return;
                try { Sweep(__instance); }
                catch (Exception e)
                {
                    Core.Log.Error($"world state replication: failed to withhold native tasks; original update will not execute: {e}");
                    throw;
                }
            }
        }
    }
}
