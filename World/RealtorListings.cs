using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Dialogue;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Adds expansion properties to Ray's lists. His handler matches a choice's label against the property and business
    /// codes, so a choice copied from the template's, with the new code as its label, leads to the same purchase
    /// confirmation. Properties go to his property list, businesses to his business list, next to their template.
    /// The conversation is an asset that outlives the main world; the choices come out again when it is left.
    /// </summary>
    internal static class RealtorListings
    {
        private const string ConversationName = "Realtor_SellProperty";
        private static readonly string[] ChoiceNodes = { "PROPERTY_CHOICE", "BUSINESS_CHOICE" };

        private static readonly List<(Conversation Conversation, string Label, string Guid)> Added = new();

        private static Conversation Find()
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Conversation>()))
            {
                var c = o.TryCast<Conversation>();
                if (c != null && c.name == ConversationName) return c;
            }
            return null;
        }

        public static void Add(string expansionId, PropertyDefinition d)
        {
            var conv = Find();
            if (conv == null)
            {
                Core.Log.Error($"realtor: conversation '{ConversationName}' is not loaded; Ray does not offer '{d.Code}'");
                return;
            }
            DialogueNodeData node = null;
            DialogueChoiceData template = null;
            foreach (var label in ChoiceNodes)
            {
                var n = conv.GetDialogueNodeByLabel(label);
                if (n?.choices == null)
                {
                    Core.Log.Warning($"realtor: '{ConversationName}' has no node '{label}'");
                    continue;
                }
                for (int i = 0; i < n.choices.Length; i++)
                {
                    if (n.choices[i].ChoiceLabel == d.Code)
                    {
                        Core.Log.Warning($"realtor: Ray already offers '{d.Code}'");
                        return;
                    }
                    if (template == null && string.Equals(n.choices[i].ChoiceLabel, d.Template, StringComparison.OrdinalIgnoreCase))
                    {
                        node = n;
                        template = n.choices[i];
                    }
                }
            }
            var link = template != null ? conv.GetLink(template.Guid) : null;
            if (link == null)
            {
                Core.Log.Error($"realtor: Ray has no '{d.Template}' choice with a link; he does not offer '{d.Code}'");
                return;
            }
            var choices = node.choices;

            var choice = template.GetCopy();
            choice.Guid = Places.StableGuid(expansionId, "realtor-choice", d.Id).ToString();
            choice.ChoiceLabel = d.Code;
            choice.ChoiceText = string.IsNullOrEmpty(d.RealtorText) ? $"{d.Name} (<PRICE>)" : d.RealtorText;

            // The new property goes before the last choice, which closes the list.
            int at = Math.Max(0, choices.Length - 1);
            var arr = new Il2CppReferenceArray<DialogueChoiceData>(choices.Length + 1);
            for (int i = 0, j = 0; i < arr.Length; i++) arr[i] = i == at ? choice : choices[j++];
            node.choices = arr;
            conv.NodeLinks.Add(new NodeLinkData
            {
                BaseDialogueOrBranchNodeGuid = link.BaseDialogueOrBranchNodeGuid,
                BaseChoiceOrOptionGUID = choice.Guid,
                TargetNodeGuid = link.TargetNodeGuid,
            });
            Added.Add((conv, d.Code, choice.Guid));
            Core.Log.Msg($"realtor: Ray offers '{d.Code}' as \"{choice.ChoiceText}\" in '{node.DialogueNodeLabel}' (choice {at + 1} of {arr.Length})");
        }

        public static void Clear()
        {
            foreach (var (conv, label, guid) in Added)
            {
                if (conv == null) continue;
                foreach (var nodeLabel in ChoiceNodes)
                {
                    var node = conv.GetDialogueNodeByLabel(nodeLabel);
                    if (node?.choices == null) continue;
                    var keep = new List<DialogueChoiceData>();
                    for (int i = 0; i < node.choices.Length; i++)
                        if (node.choices[i].ChoiceLabel != label) keep.Add(node.choices[i]);
                    var arr = new Il2CppReferenceArray<DialogueChoiceData>(keep.Count);
                    for (int i = 0; i < keep.Count; i++) arr[i] = keep[i];
                    node.choices = arr;
                }
                for (int i = conv.NodeLinks.Count - 1; i >= 0; i--)
                    if (conv.NodeLinks[i].BaseChoiceOrOptionGUID == guid) conv.NodeLinks.RemoveAt(i);
            }
            if (Added.Count > 0) Core.Log.Msg($"realtor: {Added.Count} expansion properties taken off Ray's list");
            Added.Clear();
        }

#if DEBUG
        /// <summary>
        /// Walks Ray's sale of one listing the way his dialogue does: the node the choice leads to, whether he shows and
        /// allows it, the choice itself and the confirmation of the purchase.
        /// </summary>
        public static void Buy(string code)
        {
            var conv = Find();
            if (conv == null)
            {
                Core.Log.Error($"realtor: conversation '{ConversationName}' is not loaded");
                return;
            }
            bool listed = false;
            foreach (var label in ChoiceNodes)
            {
                var n = conv.GetDialogueNodeByLabel(label);
                for (int i = 0; n?.choices != null && i < n.choices.Length; i++)
                {
                    if (!string.Equals(n.choices[i].ChoiceLabel, code, StringComparison.OrdinalIgnoreCase)) continue;
                    listed = true;
                    var link = conv.GetLink(n.choices[i].Guid);
                    var target = link != null ? conv.GetDialogueNodeByGUID(link.TargetNodeGuid) : null;
                    Core.Log.Msg($"realtor: '{code}' is choice {i + 1} of {n.choices.Length} in '{label}', \"{n.choices[i].ChoiceText}\", leads to '{target?.DialogueNodeLabel ?? "nothing"}'");
                }
            }
            if (!listed) Core.Log.Warning($"realtor: '{code}' is on none of Ray's lists");

            var handler = UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.Dialogue.DialogueHandler_EstateAgent>().FirstOrDefault();
            if (handler == null)
            {
                Core.Log.Error("realtor: Ray's dialogue handler is not in the scene");
                return;
            }
            bool shown = handler.ShouldChoiceBeShown(code);
            bool allowed = handler.CheckChoice(code, out var reason);
            Core.Log.Msg($"realtor: Ray shows '{code}': {shown}, allows it: {allowed}{(allowed ? "" : $" ({reason})")}");
            if (!shown || !allowed) return;

            bool business = false;
            var businesses = Il2CppScheduleOne.Property.Business.UnownedBusinesses;
            for (int i = 0; businesses != null && i < businesses.Count; i++)
                if (businesses[i] != null && string.Equals(businesses[i].PropertyCode, code, StringComparison.OrdinalIgnoreCase)) business = true;
            handler.ChoiceCallback(code);
            handler.DialogueCallback(business ? "CONFIRM_BUY_BUSINESS" : "CONFIRM_BUY");

            var all = Il2CppScheduleOne.Property.Property.Properties;
            for (int i = 0; all != null && i < all.Count; i++)
                if (all[i] != null && string.Equals(all[i].PropertyCode, code, StringComparison.OrdinalIgnoreCase))
                    Core.Log.Msg($"realtor: '{code}' ({(business ? "business" : "property")}) owned after the purchase: {all[i].IsOwned}");
        }
#endif
    }
}
