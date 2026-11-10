using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.257 [Optimize] ShellDamageablePieces (default off; design Tools\DESIGN_skip_damageable_support_20261109.md, Fire OK on step 3).
    //
    // WHAT A SHELL IS HERE. Every building piece (a prefab with a WearNTear) the bake draws is created by vanilla's own ZNetScene.CreateObject,
    // untouched: ZNetView, Piece, WearNTear.Awake (all 7 RPC registrations, ZDO health/support/snow reads and writes, biome, colliders) and every
    // other mod's patches run as on any piece. Its drawing parts are then switched OFF by the existing silencing (MeshBaker.DisableForCacheHit ->
    // EasyBakeSuppressedVisuals: renderer.enabled / LODGroup.enabled = false), and the bake keeps drawing it. No stand-in collider is made for it.
    //
    // DISABLE, NOT REMOVE (decided): the create is already paid when the parts could be removed, so removing saves only the renderer components'
    // native memory, at the price of a Destroy per renderer at load, a full re-create for every hand-back and a temporary copy for fragments.
    // Disabled parts keep everything later code reads valid: m_renderers is the real list (never null, so UpdateBiome runs, WearNTear.cs:797),
    // the m_new/m_worn/m_broken nodes and snow caps are untouched (caps stay vanilla's, EasyBakeSuppressedVisuals), Highlight works, and
    // Destructible.CreateFragments tests activeInHierarchy only, never renderer.enabled (Destructible.cs:322), so a destroyed shell throws its own
    // fragments. Hand-back is the existing in-place restore (ZoneTracker.HandBack -> RestoreVisuals), which shows the same object vanilla made.
    //
    // ROUTE. With the key on, TrySkipCreation / WouldSkip never skip a prefab with a WearNTear, ConvertLivePieces never unloads one into a
    // stand-in and QueueStandIns builds none for one. Prefabs WITHOUT a WearNTear (trees, rocks, plain statics) keep skipping: their real object
    // has no WearNTear either, so a neighbour's UpdateSupport reads them the same way (full support, WearNTear.cs:1045-1056) whether stand-in or real.
    internal static class ShellPieces
    {
        // 1.2.257: the ShellDamageablePieces key was withdrawn (Fire: "If a build is built with infinite health by an admin then it is meant to be infinite health"):
        // skipping stays as before and every shell branch below is inert. Only the support census stays, Debug-only, so the frequency is known.
        internal static bool On => false;

        // True when a skip/stand-in path must leave this prefab to vanilla's create (a shell).
        internal static bool KeepsReal(SkipEligibility.PrefabInfo info) => info != null && info.HasWear && On;
        internal static bool KeepsReal(int prefabHash) => On && SkipEligibility.Get(prefabHash).HasWear;

        internal enum Reason { LookOrDamage = 0, Highlight, AshlandsBurn, Removed, StandInsHandedBackOnKey }
        private static readonly string[] ReasonNames = { "look change / damage / repair / fire", "hammer highlight", "Ashlands burning", "removed", "stand-ins handed back when the key turned on" };
        private static readonly int[] s_handedBack = new int[5];
        private static int s_made, s_madeTotal, s_declinedSkip, s_frames, s_lastFrame = -1;
        private static long s_makeTicks;
        private static bool s_saidOn;
        private static float s_nextReport;

        internal static void NoteDeclinedSkip() { s_declinedSkip++; SayOn(); }
        internal static void NoteHandBack(Reason r) { if (On) s_handedBack[(int)r]++; }
        internal static void NoteHandBack(Reason r, int n) { if (On) s_handedBack[(int)r] += n; }

        // Around the silencing of a created piece the bake draws (the step that turns it into a shell).
        internal static long Begin() => On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        internal static void End(long start)
        {
            if (start == 0L) return;
            s_makeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            s_made++; s_madeTotal++;
            SayOn();
        }

        private static void SayOn()
        {
            if (s_saidOn) return;
            s_saidOn = true;
            EasyBakeLog.Info("[Shell] ShellDamageablePieces on: building pieces the bake draws are created by the game as usual and drawn by the bake with their own renderers switched off; no stand-in collider replaces them, so wear and support run on the real piece");
        }

        // Called once a frame from ZoneTracker.Update (cheap: two compares while nothing is due).
        internal static void Tick()
        {
            int frame = Time.frameCount;
            if (frame != s_lastFrame) { s_lastFrame = frame; s_frames++; }
            float now = Time.unscaledTime;
            if (now < s_nextReport) return;
            bool first = s_nextReport == 0f;
            s_nextReport = now + 30f;
            if (first) { ResetPeriod(); return; }
            if (EasyBakeLog.DebugOn() && (On || s_made > 0 || SupportCensus.Any))
            {
                double ms = s_makeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                var sb = new System.Text.StringBuilder(256);
                sb.Append("[Shell] last 30 s: ").Append(s_made).Append(" shell(s) made (").Append(s_madeTotal).Append(" this session), ")
                  .Append(s_declinedSkip).Append(" stand-in skip(s) declined for a shell; silencing cost ").Append(ms.ToString("F2")).Append(" ms total, ")
                  .Append(s_frames > 0 ? (ms / s_frames).ToString("F4") : "0").Append(" ms/frame; handed back:");
                for (int i = 0; i < s_handedBack.Length; i++) sb.Append(' ').Append(ReasonNames[i]).Append(' ').Append(s_handedBack[i]).Append(i < s_handedBack.Length - 1 ? "," : "");
                sb.Append(" (key ").Append(On ? "on" : "off").Append(')');
                EasyBakeLog.Debug(sb.ToString());
            }
            SupportCensus.Report(On);
            ResetPeriod();
        }

        private static void ResetPeriod()
        {
            s_made = 0; s_declinedSkip = 0; s_makeTicks = 0; s_frames = 0;
            System.Array.Clear(s_handedBack, 0, s_handedBack.Length);
        }
    }

    // ═══ SUPPORT CENSUS: HOW OFTEN A SUPPORT READ STOPS ON A STAND-IN ═══════════════════════════════════════════════
    // Count only; never changes a result. Vanilla UpdateSupport (WearNTear.cs:924-1133) has exactly three self-calls of ClearCachedSupport:
    //   (1) the cached-support fast path failing (:996), only when the cache held entries at entry, and always the first self-call;
    //   (2) the "solid collider with no WearNTear" return (:1045-1056), right after m_support = maxSupport;
    //   (3) the end, only when !HaveSupport(), so m_support is then below the minimum and never equal to maxSupport.
    // So case (2) is: a self-call that is not case (1), and m_support == GetMaxSupport() when UpdateSupport returns. The collider that decided it is
    // the first entry of vanilla's shared overlap buffer (s_tempColliders, still holding the last bound's hits) that vanilla would not skip:
    // not a rigidbody, not a trigger, not one of the piece's own colliders, not terrain, no WearNTear in its parents. Entries before it were
    // skipped by vanilla by those same tests (a WearNTear hit with m_supports false is skipped by both), so it is exactly the one vanilla stopped on.
    // Prefix + postfix on UpdateSupport and a prefix on ClearCachedSupport: no call inside UpdateSupport is replaced, so FiresFixes' owner-cache
    // transpiler (WearNTearOwnerCache swaps the GetComponentInParent<WearNTear> calls) and any other mod's patches run as before.
    // If another mod replaces UpdateSupport whole (a prefix returning false), our postfix still runs but no self-call is seen: the count reads 0 for
    // that mod's pieces, and the line says so by showing 0 static hits overall.
    // Cost: three field stores per UpdateSupport; the buffer walk and the parent lookups run only on a case-(2) read, with the Debug log on.
    [HarmonyPatch]
    internal static class SupportCensus
    {
        private static readonly AccessTools.FieldRef<WearNTear, float> s_support = AccessTools.FieldRefAccess<WearNTear, float>("m_support");
        private static readonly AccessTools.FieldRef<WearNTear, System.Collections.Generic.List<Collider>> s_supportColliders =
            AccessTools.FieldRefAccess<WearNTear, System.Collections.Generic.List<Collider>>("m_supportColliders");
        private static readonly AccessTools.FieldRef<WearNTear, Collider[]> s_colliders = AccessTools.FieldRefAccess<WearNTear, Collider[]>("m_colliders");
        private static System.Func<WearNTear, float> s_maxSupport;
        private static Collider[] s_buffer;
        private static bool s_broken;
        private static int s_terrainLayer = -2;

        private static WearNTear s_in;
        private static bool s_cacheAtEntry;
        private static int s_selfClears;
        private static bool s_lateClear;

        private static int s_staticHits, s_standInHits, s_standInWearHits;

        internal static bool Any => s_staticHits > 0;

        private static bool Ready()
        {
            if (s_broken) return false;
            if (s_buffer != null) return true;
            try
            {
                s_buffer = AccessTools.Field(typeof(WearNTear), "s_tempColliders")?.GetValue(null) as Collider[];
                s_maxSupport = AccessTools.MethodDelegate<System.Func<WearNTear, float>>(AccessTools.Method(typeof(WearNTear), "GetMaxSupport"));
                s_terrainLayer = LayerMask.NameToLayer("terrain");
                if (s_buffer == null || s_maxSupport == null) throw new System.MissingMemberException("WearNTear.s_tempColliders / GetMaxSupport");
                return true;
            }
            catch (System.Exception ex)
            {
                s_broken = true;
                EasyBakeLog.Warn($"[Shell] support census off: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        [HarmonyPatch(typeof(WearNTear), "UpdateSupport"), HarmonyPrefix]
        private static void Before(WearNTear __instance)
        {
            if (!EasyBakeLog.DebugOn()) { s_in = null; return; }
            s_in = __instance;
            var list = s_supportColliders(__instance);
            s_cacheAtEntry = list != null && list.Count > 0;
            s_selfClears = 0;
            s_lateClear = false;
        }

        [HarmonyPatch(typeof(WearNTear), "ClearCachedSupport"), HarmonyPrefix]
        private static void OnClear(WearNTear __instance)
        {
            if (!ReferenceEquals(__instance, s_in)) return;
            s_selfClears++;
            if (s_selfClears == 1 && s_cacheAtEntry) return;   // case (1)
            s_lateClear = true;
        }

        [HarmonyPatch(typeof(WearNTear), "UpdateSupport"), HarmonyPostfix]
        private static void After(WearNTear __instance)
        {
            if (!ReferenceEquals(__instance, s_in)) return;
            s_in = null;
            if (!s_lateClear || !Ready()) return;
            try
            {
                if (!s_support(__instance).Equals(s_maxSupport(__instance))) return;   // case (3)
                var own = s_colliders(__instance);
                for (int i = 0; i < s_buffer.Length; i++)
                {
                    var c = s_buffer[i];
                    if (c == null || c.attachedRigidbody != null || c.isTrigger) continue;
                    if (own != null && System.Array.IndexOf(own, c) >= 0) continue;
                    if (c.gameObject.layer == s_terrainLayer) continue;
                    if (c.GetComponentInParent<WearNTear>() != null) continue;
                    s_staticHits++;
                    var mark = c.GetComponentInParent<EbmStandIn>();
                    if (mark != null)
                    {
                        s_standInHits++;
                        if (SkipEligibility.Get(mark.PrefabHash).HasWear) s_standInWearHits++;
                    }
                    return;
                }
            }
            catch (System.Exception ex)
            {
                s_broken = true;
                EasyBakeLog.Warn($"[Shell] support census off after an error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static void Report(bool shellsOn)
        {
            if (EasyBakeLog.DebugOn() && (s_staticHits > 0 || shellsOn))
                EasyBakeLog.Debug($"[Shell] support census, last 30 s: {s_staticHits} support read(s) took full support from a collider with no WearNTear; {s_standInHits} of them an EBM stand-in, " +
                                  $"{s_standInWearHits} a stand-in for a building piece (treated as solid ground, by design), {s_standInHits - s_standInWearHits} a stand-in for a tree/rock/static (same answer as its real object). Key {(shellsOn ? "on" : "off")}.");
            s_staticHits = s_standInHits = s_standInWearHits = 0;
        }
    }
}
