using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// 1.2.281 [Optimize] DestroyHeightmapBulkDetach (lead 11-11; town -> swamp flight: 134k destroys, learned cost per SOLID destroy 1,284 us vs loose 239 us).
    /// Vanilla WearNTear.OnDestroy (WearNTear.cs:395-407) unsubscribes the piece from its terrain: m_connectedHeightMap.m_clearConnectedWearNTearCache -=
    /// ClearCachedSupport. Every piece standing on a zone's terrain is on that ONE multicast delegate, and each -= copies the whole invocation list, so
    /// destroying a town zone's N pieces one by one is O(N^2). Here, before a drain destroys its batch, each affected Heightmap's delegate is rebuilt ONCE
    /// by REMOVING exactly the dying pieces' ClearCachedSupport entries from the CURRENT invocation list (every other subscriber, any mod's, stays as it
    /// was), and each dying piece's m_connectedHeightMap is set to null, so its own OnDestroy skips the -=. End state = vanilla's; work O(N).
    /// Census (Debug, with the [DTS] drain lines): heightmaps touched, the largest subscriber list seen (the O(N^2) proof), entries removed, and a verify
    /// count (new list length == old length - removed). Private fields via FieldRefAccess (never publicized).
    /// </summary>
    internal static class HeightmapBulkDetach
    {
        internal static bool On => FiresEasyBakeMeshesPlugin.DestroyHeightmapBulkDetach != null && FiresEasyBakeMeshesPlugin.DestroyHeightmapBulkDetach.Value;

        private static AccessTools.FieldRef<WearNTear, Heightmap> s_hm;
        private static AccessTools.FieldRef<Heightmap, Action> s_ev;
        private static bool s_resolved, s_ok;

        private static readonly Dictionary<Heightmap, HashSet<WearNTear>> s_dying = new Dictionary<Heightmap, HashSet<WearNTear>>();
        private static readonly Stack<HashSet<WearNTear>> s_pool = new Stack<HashSet<WearNTear>>();
        private static readonly List<Delegate> s_keep = new List<Delegate>(1024);
        private static readonly List<WearNTear> s_scratch = new List<WearNTear>(4);
        private static readonly List<WearNTear> s_gone = new List<WearNTear>(512);

        internal static int c_heightmaps, c_maxSubscribers, c_removed, c_verifyBad, c_batches;

        private static bool Resolve()
        {
            if (s_resolved) return s_ok;
            s_resolved = true;
            try
            {
                s_hm = AccessTools.FieldRefAccess<WearNTear, Heightmap>("m_connectedHeightMap");
                s_ev = AccessTools.FieldRefAccess<Heightmap, Action>("m_clearConnectedWearNTearCache");   // the event's compiler-made backing field (same name)
                s_ok = s_hm != null && s_ev != null;
            }
            catch (Exception e) { s_ok = false; EasyBakeLog.Warn("[DTS] heightmap bulk detach unavailable (vanilla fields changed): " + e.Message + "; destroys run as vanilla does"); }
            return s_ok;
        }

        /// <summary>Before the batch is destroyed: one rebuild per affected Heightmap; each dying piece's link cleared.</summary>
        internal static void Detach(Dictionary<ZDO, ZNetView> instances, List<ZDO> batch)
        {
            if (!On || batch.Count == 0 || !Resolve()) return;
            c_batches++;
            for (int i = 0; i < batch.Count; i++)
            {
                if (!instances.TryGetValue(batch[i], out var nv) || nv == null) continue;
                s_scratch.Clear();
                nv.GetComponentsInChildren(true, s_scratch);   // a piece is usually one WearNTear on its root; children counted too
                for (int k = 0; k < s_scratch.Count; k++)
                {
                    var w = s_scratch[k];
                    var hm = w != null ? s_hm(w) : null;
                    if (hm == null) continue;
                    if (!s_dying.TryGetValue(hm, out var set)) { set = s_pool.Count > 0 ? s_pool.Pop() : new HashSet<WearNTear>(); s_dying[hm] = set; }
                    set.Add(w);
                }
            }
            foreach (var kv in s_dying)
            {
                var hm = kv.Key; var set = kv.Value;
                try
                {
                    if (hm != null)
                    {
                        Action d = s_ev(hm);
                        if (d != null)
                        {
                            var list = d.GetInvocationList();
                            if (list.Length > c_maxSubscribers) c_maxSubscribers = list.Length;
                            s_keep.Clear(); s_gone.Clear();
                            int removed = 0;
                            for (int j = 0; j < list.Length; j++)
                            {
                                var x = list[j];
                                if (x.Target is WearNTear w && set.Contains(w) && x.Method.Name == "ClearCachedSupport") { removed++; s_gone.Add(w); continue; }
                                s_keep.Add(x);
                            }
                            if (removed > 0)
                            {
                                var nd = s_keep.Count > 0 ? (Action)Delegate.Combine(s_keep.ToArray()) : null;
                                s_ev(hm) = nd;
                                // verify re-reads the new list only with Debug on ([generator]: GetInvocationList allocates)
                                int after = EasyBakeLog.DebugOn() ? (nd != null ? nd.GetInvocationList().Length : 0) : list.Length - removed;
                                if (after != list.Length - removed) c_verifyBad++;
                                c_removed += removed; c_heightmaps++;
                                // only pieces whose OWN entry was removed lose the link, so their OnDestroy skips the -=. A piece subscribed some other way (VCP's
                                // WearCacheEventPatch: one forwarder per heightmap, and its OnDestroy hook reads m_connectedHeightMap) keeps its link untouched.
                                foreach (var w in s_gone) if (w != null) s_hm(w) = null;
                            }
                        }
                    }
                }
                catch (Exception e) { EasyBakeLog.Debug("[DTS] heightmap bulk detach skipped one terrain: " + e.Message); }
                set.Clear(); if (s_pool.Count < 64) s_pool.Push(set);
            }
            s_dying.Clear();
        }

        internal static string TakeLine()
        {
            if (c_batches == 0) return On ? "heightmap bulk detach: no batch" : "heightmap bulk detach off";
            string s = $"heightmap bulk detach over {c_batches} batch(es): {c_heightmaps} terrain list rebuild(s), {c_removed} piece subscription(s) removed, largest list seen {c_maxSubscribers} (the O(N^2) case), verify mismatches {c_verifyBad}";
            c_batches = c_heightmaps = c_removed = c_maxSubscribers = 0;   // verify mismatches kept for the session (never expected)
            return s;
        }
    }
}
