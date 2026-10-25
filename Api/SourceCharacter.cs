using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Framework;
using UnityEngine;

namespace MapExpansions
{
    /// <summary>
    /// A character of an expansion's source scene, as the scene defines it: name, look and voice. The character
    /// itself does not come along, because a source scene's NPCs are network objects of their own world. A mod that
    /// brings such a person to life creates its own NPC and gives it this look with <see cref="ApplyAppearance"/>.
    /// </summary>
    public sealed class SourceCharacter
    {
        /// <summary>Name of the character's object in the source scene, such as "Pam".</summary>
        public string ObjectName { get; internal set; }

        /// <summary>The character's id in the source scene. Not unique; the prologue's two officers share one.</summary>
        public string Id { get; internal set; }

        public string FirstName { get; internal set; }

        public string LastName { get; internal set; }

        /// <summary>Contact picture, or null.</summary>
        public Sprite Mugshot { get; internal set; }

        /// <summary>The character's data as the source scene defines it. Kept loaded for the whole session.</summary>
        public BaseNPCDataObject Data { get; internal set; }

        /// <summary>
        /// Gives a living NPC this character's body, clothes and distant-view picture. Call it on every peer once the NPC
        /// exists; the look is local to each peer. The NPC keeps the look when the game re-applies its data.
        /// </summary>
        /// <returns>False when the NPC or the character's look is missing; the log names the reason.</returns>
        public bool ApplyAppearance(NPC npc)
        {
            if (npc == null)
            {
                Core.Log.Error($"characters: ApplyAppearance('{ObjectName}') got no NPC");
                return false;
            }
            var look = Data?.GetOriginalData()?.Appearance;
            if (look?.DefaultAppearance == null)
            {
                Core.Log.Error($"characters: '{ObjectName}' has no appearance in its source data; '{npc.name}' keeps its own");
                return false;
            }
            var voice = Data.GetOriginalData().Voice;
            foreach (var data in new[] { npc._defaultNPCData?.GetOriginalData(), npc.NPCData })
            {
                if (data?.Appearance == null) continue;
                data.Appearance.DefaultAppearance = look.DefaultAppearance;
                data.Appearance.DefaultOutfit = look.DefaultOutfit;
                data.Appearance.Impostor = look.Impostor;
                if (look.Mugshot != null) data.Appearance.Mugshot = look.Mugshot;
                if (voice?.VoiceDatabase != null && data.Voice != null)
                {
                    data.Voice.VoiceDatabase = voice.VoiceDatabase;
                    data.Voice.VoicePitch = voice.VoicePitch;
                }
            }
            if (voice?.VoiceDatabase != null && npc.VoiceOverEmitter != null)
            {
                npc.VoiceOverEmitter.SetDatabase(voice.VoiceDatabase);
                npc.VoiceOverEmitter.SetDefaultPitch(voice.VoicePitch);
            }
            else Core.Log.Warning($"characters: '{npc.name}' keeps its own voice ('{ObjectName}' voice {(voice?.VoiceDatabase != null ? "set" : "missing")}, emitter {(npc.VoiceOverEmitter != null ? "found" : "missing")})");
            var avatar = npc.Avatar;
            if (avatar?.Appearance == null)
            {
                Core.Log.Warning($"characters: '{npc.name}' has no avatar yet; '{ObjectName}' applies with its next data load");
                return true;
            }
            avatar.Appearance.ApplyNakedAppearance(look.DefaultAppearance.Appearance);
            if (look.DefaultOutfit != null) avatar.Appearance.ApplyOutfit(look.DefaultOutfit);
            if (look.Impostor != null) avatar.SetImpostorTexture(look.Impostor);
            Core.Log.Msg($"characters: '{npc.name}' looks like '{ObjectName}' now (outfit {(look.DefaultOutfit != null ? look.DefaultOutfit.name : "none")})");
            return true;
        }
    }
}
