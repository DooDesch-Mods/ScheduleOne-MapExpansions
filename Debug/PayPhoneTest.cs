#if DEBUG
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Calling;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.ScriptableObjects;
using Il2CppScheduleOne.UI.Phone;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Debug
{
    /// <summary>
    /// mxpayphone                 every pay phone the game finds: place, expansion, components, call state, ring sound
    /// mxpayphone ring            queues a test call on the pay phones, as a quest call would
    /// mxpayphone answer [exp]    answers at the pay phone nearest to the player (exp: nearest one of an expansion) through
    ///                            its own interaction handler, as the player's click would
    /// mxpayphone end             completes the open call and takes the test call off the pay phones
    /// </summary>
    internal static class PayPhoneTest
    {
        private const string TestCallName = "MapExpansions.TestCall";
        private static PhoneCallData _test;

        internal static void Handle(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "": List(); break;
                case "ring": Ring(); break;
                case "answer": Answer(parts.Length > 2 && parts[2] == "exp"); break;
                case "end": End(); break;
                default: Core.Log.Warning($"[dev] mxpayphone: unknown '{sub}'; use ring, answer [exp] or end"); break;
            }
        }

        private static void List()
        {
            var phones = Object.FindObjectsOfType<PayPhone>();
            var cm = CallManager.Instance;
            Core.Log.Msg($"[dev] mxpayphone: {phones.Length} pay phones; queued call '{(cm?.QueuedCallData != null ? cm.QueuedCallData.name : "none")}'");
            var cam = PlayerSingleton<PlayerCamera>.Instance;
            foreach (var p in phones)
            {
                var pos = p.transform.position;
                string exp = MapExpansionsApi.ExpansionAt(pos) ?? "main";
                var comps = p.GetComponents<Component>().Select(c => c.GetIl2CppType().Name);
                float dist = cam != null ? Vector3.Distance(cam.transform.position, pos) : -1f;
                string stand = p.CameraPosition != null ? p.CameraPosition.position.ToString("F2") : "none";
                Core.Log.Msg($"[dev] mxpayphone: '{World.PayPhones.PathOf(p.transform)}' at {pos.ToString("F2")} in {exp}, stand {stand}, {dist:0.0} m away; components {string.Join(",", comps)}; " +
                             $"queued '{(p.QueuedCall != null ? p.QueuedCall.name : "none")}', active '{(p.ActiveCall != null ? p.ActiveCall.name : "none")}', " +
                             $"light {(p.Light != null ? p.Light.IsOn.ToString() : "missing")}, ring sound {(p.RingSound != null ? (p.RingSound.IsPlaying ? "playing" : "silent") : "missing")}");
            }
        }

        private static void Ring()
        {
            var cm = CallManager.Instance;
            if (cm == null)
            {
                Core.Log.Warning("[dev] mxpayphone ring: no call manager");
                return;
            }
            if (_test == null)
            {
                var caller = ScriptableObject.CreateInstance<CallerID>();
                caller.Name = "MapExpansions test";
                _test = ScriptableObject.CreateInstance<PhoneCallData>();
                _test.name = TestCallName;
                _test.CallerID = caller;
                var stage = new PhoneCallData.Stage
                {
                    Text = "Pay phone test call.",
                    OnStartTriggers = new Il2CppReferenceArray<Il2CppScheduleOne.Quests.SystemTrigger>(0),
                    OnDoneTriggers = new Il2CppReferenceArray<Il2CppScheduleOne.Quests.SystemTrigger>(0),
                };
                _test.Stages = new Il2CppReferenceArray<PhoneCallData.Stage>(new[] { stage });
                _test.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                caller.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            }
            cm.QueueCall(_test);
            Core.Log.Msg($"[dev] mxpayphone ring: '{TestCallName}' queued; queued call is now '{(cm.QueuedCallData != null ? cm.QueuedCallData.name : "none")}'");
        }

        private static void Answer(bool expansionOnly)
        {
            var cam = PlayerSingleton<PlayerCamera>.Instance;
            if (cam == null)
            {
                Core.Log.Warning("[dev] mxpayphone answer: no player camera");
                return;
            }
            PayPhone best = null;
            float bestD = float.MaxValue;
            foreach (var p in Object.FindObjectsOfType<PayPhone>())
            {
                if (expansionOnly && MapExpansionsApi.ExpansionAt(p.transform.position) == null) continue;
                float d = Vector3.Distance(cam.transform.position, p.transform.position);
                if (d < bestD) { best = p; bestD = d; }
            }
            if (best == null)
            {
                Core.Log.Warning($"[dev] mxpayphone answer: no {(expansionOnly ? "expansion " : "")}pay phone");
                return;
            }
            best.Hovered();
            string message = best.IntObj != null ? best.IntObj.message : "no interactable";
            best.Interacted();
            var ci = Singleton<CallInterface>.Instance;
            Core.Log.Msg($"[dev] mxpayphone answer: '{World.PayPhones.PathOf(best.transform)}' {bestD:0.0} m away, hover message '{message}'; " +
                         $"call open {(ci != null && ci.IsOpen)}, active '{(ci?.ActiveCallData != null ? ci.ActiveCallData.name : "none")}', " +
                         $"phone active '{(best.ActiveCall != null ? best.ActiveCall.name : "none")}'");
        }

        private static void End()
        {
            var ci = Singleton<CallInterface>.Instance;
            if (ci != null && ci.IsOpen)
            {
                ci.CompleteCall();
                Core.Log.Msg("[dev] mxpayphone end: the open call was completed");
            }
            else Core.Log.Msg("[dev] mxpayphone end: no call is open");
            var cm = CallManager.Instance;
            if (cm != null && cm.QueuedCallData != null && cm.QueuedCallData.name == TestCallName)
            {
                cm.ClearQueuedCall();
                Core.Log.Msg("[dev] mxpayphone end: the test call was taken off the pay phones");
            }
        }
    }
}
#endif
