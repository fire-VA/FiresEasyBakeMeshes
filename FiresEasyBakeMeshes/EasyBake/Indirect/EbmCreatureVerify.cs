using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // [wishbone] 11-11: [Optimize] IndirectCreatureVariantVerify. Goal: every covered Custom/Creature renderer is drawn exactly once
    // (its Unity renderer off AND exactly one live indirect instance). Any other count is logged (0 = invisible, 2+ = drawn twice),
    // capped at 10 lines per 30 s, plus one Debug census line (covered, refused by reason from EbmIndirectAssets.CreatureRefusals).
    //
    // Wired ([wishbone] 11-11): NoteCovered from CoveredObject.TryCoverPart after a part's handles are added and its Unity renderer is hidden;
    // NoteReleased from CoveredObject.ReleaseEntries before the renderer is shown again; Tick from EbmIndirect.EndFrame.
    // Skinned trophies need no refusal: coverage only collects MeshRenderers (CoveredObject.s_renderers is List<MeshRenderer>, TryCover takes
    // a MeshRenderer), so a SkinnedMeshRenderer never reaches a batch and stays vanilla.
    // Known limit: a P3 level-off (CoveredObject.LevelRelease) drops a part's slots without ReleaseEntries; that part still counts 1 slot here.
    // So the check proves "never drawn twice / never shown while covered", not every level window.
    // Not a Harmony patch class.
    internal static class EbmCreatureVerify
    {
        private static readonly Dictionary<Renderer, int> s_slots = new Dictionary<Renderer, int>();
        private static float s_nextCheck, s_windowStart;
        private static int s_linesThisWindow;
        private const float CheckEvery = 5f, Window = 30f;
        private const int MaxLines = 10;

        private static bool On => FiresEasyBakeMeshesPlugin.IndirectCreatureVariantVerify != null && FiresEasyBakeMeshesPlugin.IndirectCreatureVariantVerify.Value;

        private static bool IsCreature(Renderer r)
        {
            var m = r != null ? r.sharedMaterial : null;
            return m != null && m.shader != null && m.shader.name == "Custom/Creature";
        }

        internal static void NoteCovered(Renderer r)
        {
            if (!On || !IsCreature(r)) return;
            s_slots.TryGetValue(r, out int n); s_slots[r] = n + 1;
        }

        internal static void NoteReleased(Renderer r)
        {
            if (r == null || !s_slots.TryGetValue(r, out int n)) return;
            if (n <= 1) s_slots.Remove(r); else s_slots[r] = n - 1;
        }

        internal static void Tick()
        {
            if (!On) { if (s_slots.Count > 0) s_slots.Clear(); return; }
            float now = Time.unscaledTime;
            if (now < s_nextCheck) return;
            s_nextCheck = now + CheckEvery;
            if (now - s_windowStart >= Window) { s_windowStart = now; s_linesThisWindow = 0; }

            int ok = 0, bad = 0;
            List<Renderer> gone = null;
            foreach (var kv in s_slots)
            {
                var r = kv.Key;
                if (r == null) { if (gone == null) gone = new List<Renderer>(); gone.Add(r); continue; }
                bool unityDraws = r.enabled && r.gameObject.activeInHierarchy && r.forceRenderingOff == false;
                int drawn = kv.Value + (unityDraws ? 1 : 0);
                if (drawn == 1 && !unityDraws) { ok++; continue; }
                bad++;
                if (s_linesThisWindow < MaxLines)
                {
                    s_linesThisWindow++;
                    EasyBakeLog.Warn($"[CreatureVerify] '{r.name}' drawn {drawn} time(s) (GPU slots {kv.Value}, Unity renderer {(unityDraws ? "ON" : "off")}); expected exactly 1 GPU slot and Unity off");
                }
            }
            if (gone != null) foreach (var r in gone) s_slots.Remove(r);

            var parts = new List<string>();
            foreach (var kv in EbmIndirectAssets.CreatureRefusals) parts.Add($"{kv.Key} x{kv.Value}");
            EasyBakeLog.Debug($"[CreatureVerify] covered {s_slots.Count} (ok {ok}, wrong count {bad}); refused materials: {(parts.Count > 0 ? string.Join(", ", parts) : "none")}");
        }
    }
}
