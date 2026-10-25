using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Dialogue;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Adds expansion properties to Ray's list. His handler matches a choice's label against the property codes, so a
    /// choice copied from the bungalow's, with the new code as its label, leads to the same purchase confirmation.
    /// The conversation is an asset that outlives the main world; the choices come out again when it is left.
    /// </summary>
    internal static class RealtorListings
    {
        private const string ConversationName = "Realtor_SellProperty";
        private const string ChoiceNode = "PROPERTY_CHOICE";
        private const string TemplateLabel = "bungalow";

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
            var node = conv.GetDialogueNodeByLabel(ChoiceNode);
            if (node == null || node.choices == null)
            {
                Core.Log.Error($"realtor: '{ConversationName}' has no node '{ChoiceNode}'; Ray does not offer '{d.Code}'");
                return;
            }
            var choices = node.choices;
            DialogueChoiceData template = null;
            for (int i = 0; i < choices.Length; i++)
            {
                if (choices[i].ChoiceLabel == d.Code)
                {
                    Core.Log.Warning($"realtor: Ray already offers '{d.Code}'");
                    return;
                }
                if (choices[i].ChoiceLabel == TemplateLabel) template = choices[i];
            }
            var link = template != null ? conv.GetLink(template.Guid) : null;
            if (link == null)
            {
                Core.Log.Error($"realtor: the '{TemplateLabel}' choice or its link is missing; Ray does not offer '{d.Code}'");
                return;
            }

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
            Core.Log.Msg($"realtor: Ray offers '{d.Code}' as \"{choice.ChoiceText}\" (choice {at + 1} of {arr.Length})");
        }

        public static void Clear()
        {
            foreach (var (conv, label, guid) in Added)
            {
                if (conv == null) continue;
                var node = conv.GetDialogueNodeByLabel(ChoiceNode);
                if (node?.choices != null)
                {
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
    }
}
