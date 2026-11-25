using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// 1.2.287 [Optimize] PiecesGroupedPerTile, EBM's own copy (lead 11-11: the moving-circle capture put 37% of allocated bytes in the multicast Combine /
    /// Remove under WearNTear.Start / OnDestroy; FiresFixes owns the same fix, and EBM must work without FiresFixes). Vanilla subscribes every piece to its
    /// terrain's m_clearConnectedWearNTearCache (WearNTear.cs:376) and unsubscribes it on destroy (:405): each += / -= copies the whole invocation list.
    /// Here a TRANSPILER on WearNTear.Start and OnDestroy swaps only that event add / remove call for Subscribe / Unsubscribe(Heightmap, Action):
    /// a piece's own ClearCachedSupport joins one set per heightmap, and the heightmap gets ONE forwarder that calls ClearCachedSupport on every
    /// piece in the set (copied to a scratch list first: a piece can be destroyed during the call). Any other delegate keeps vanilla's += / -=.
    /// One owner: whichever mod transpiles second finds vanilla's call already replaced and stays out; and at run time, while FiresFixes' own hub is
    /// active (FixesApi.PiecesGroupedPerTileActive, read by name each call), Subscribe / Unsubscribe pass straight through to vanilla's += / -=.
    /// Unsubscribe falls back to vanilla's -= for a piece not in a set, so turning the key off mid-session is safe. A destroyed heightmap's set goes with it
    /// (ConditionalWeakTable). Same behaviour as vanilla: Heightmap.Regenerate invokes the event, every piece's cached support is cleared.
    /// </summary>
    // 1.2.288: NOT an attribute patch any more; Install() applies it once at the first ZNetScene.Awake, only when FiresFixes does not own it
    internal static class PieceHeightmapHub
    {
        private static bool KeyOn => FiresEasyBakeMeshesPlugin.PiecesGroupedPerTile != null && FiresEasyBakeMeshesPlugin.PiecesGroupedPerTile.Value;

        private sealed class Group
        {
            public readonly HashSet<WearNTear> Pieces = new HashSet<WearNTear>();
            public readonly Action Forward;
            public Group() { Forward = Fire; }
            private void Fire()
            {
                s_scratch.Clear(); s_scratch.AddRange(Pieces);
                for (int i = 0; i < s_scratch.Count; i++) { var w = s_scratch[i]; if (w != null) { try { s_clear(w); } catch (Exception e) { EasyBakeLog.Debug("[PieceHub] ClearCachedSupport threw: " + e.Message); } } }
                s_scratch.Clear();
            }
        }

        private static readonly ConditionalWeakTable<Heightmap, Group> s_groups = new ConditionalWeakTable<Heightmap, Group>();
        private static readonly List<WearNTear> s_scratch = new List<WearNTear>(256);
        private static readonly Action<WearNTear> s_clear = AccessTools.MethodDelegate<Action<WearNTear>>(AccessTools.Method(typeof(WearNTear), "ClearCachedSupport"));
        private static readonly MethodInfo s_add = AccessTools.Method(typeof(Heightmap), "add_m_clearConnectedWearNTearCache");
        private static readonly MethodInfo s_remove = AccessTools.Method(typeof(Heightmap), "remove_m_clearConnectedWearNTearCache");
        // open-instance delegates made once: no reflection Invoke / object[] per call (the point of this fix is fewer allocations)
        private static readonly Action<Heightmap, Action> s_addD = AccessTools.MethodDelegate<Action<Heightmap, Action>>(s_add);
        private static readonly Action<Heightmap, Action> s_removeD = AccessTools.MethodDelegate<Action<Heightmap, Action>>(s_remove);
        private static Func<bool> s_ffActive; private static bool s_ffLooked;
        private static int c_grouped, c_passed, c_swapsStart, c_swapsDestroy; private static bool s_said;

        // FiresFixes' own hub, by name (EBM never relies on FiresFixes; absent = false)
        private static bool FfActive()
        {
            if (!s_ffLooked)
            {
                s_ffLooked = true;
                var t = Type.GetType("FiresFixes.Api.FixesApi, FiresFixes");
                var p = t?.GetProperty("PiecesGroupedPerTileActive", BindingFlags.Public | BindingFlags.Static);
                if (p != null) s_ffActive = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), p.GetGetMethod());
            }
            try { return s_ffActive != null && s_ffActive(); } catch { return false; }
        }

        public static void Subscribe(Heightmap hm, Action a)
        {
            if (hm == null) return;
            if (!KeyOn || FfActive() || !(a?.Target is WearNTear w) || a.Method.Name != "ClearCachedSupport") { s_addD(hm, a); c_passed++; return; }
            if (!s_groups.TryGetValue(hm, out var g)) { g = new Group(); s_groups.Add(hm, g); s_addD(hm, g.Forward); }
            g.Pieces.Add(w); c_grouped++;
            if (!s_said) { s_said = true; EasyBakeLog.Info("[EBM] pieces grouped per terrain tile: on (one terrain callback per tile instead of one per piece; FiresFixes' own copy takes over when it is active)"); }
        }

        public static void Unsubscribe(Heightmap hm, Action a)
        {
            if (hm == null) return;
            if (a?.Target is WearNTear w && s_groups.TryGetValue(hm, out var g) && g.Pieces.Remove(w)) return;
            s_removeD(hm, a);   // not in a set (key flipped, FiresFixes' hub, another delegate): vanilla's -=
        }

        // ---- transpilers: swap only the event add / remove call
        // transpiler for WearNTear.Start (applied by Install)
        private static IEnumerable<CodeInstruction> StartT(IEnumerable<CodeInstruction> code) => Swap(code, s_add, nameof(Subscribe), ref c_swapsStart);

        // transpiler for WearNTear.OnDestroy (applied by Install)
        private static IEnumerable<CodeInstruction> DestroyT(IEnumerable<CodeInstruction> code) => Swap(code, s_remove, nameof(Unsubscribe), ref c_swapsDestroy);

        private static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> code, MethodInfo vanilla, string ours, ref int swaps)
        {
            var target = AccessTools.Method(typeof(PieceHeightmapHub), ours);
            var list = new List<CodeInstruction>(code);
            foreach (var ci in list)
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo m && m == vanilla) { ci.opcode = OpCodes.Call; ci.operand = target; swaps++; }
            if (swaps == 0) EasyBakeLog.Info($"[EBM] pieces grouped per terrain tile: {vanilla.Name} was already replaced by another mod (FiresFixes?), EBM's copy stays out of WearNTear");
            return list;
        }


        // ---- 1.2.288 (lead / [fgn] 11-11: EBM transpiled first and FiresFixes logged 3 Warnings): ONE owner decided before any piece starts. At the first
        // ZNetScene.Awake every plugin has loaded: when FiresFixes is installed and wants its own grouping (FixesApi.PiecesGroupedPerTileWanted, or its
        // runtime PiecesGroupedPerTileActive; an FF without either flag counts as the owner too), EBM applies nothing; otherwise EBM's copy is patched in.
        private static bool s_installed;
        internal static void Install()
        {
            if (s_installed) return;
            s_installed = true;
            if (!KeyOn) { EasyBakeLog.Debug("[EBM] pieces grouped per terrain tile: key off, EBM's copy not applied"); return; }
            var ff = Type.GetType("FiresFixes.Api.FixesApi, FiresFixes");
            if (ff != null)
            {
                bool owner = true;
                var wanted = ff.GetProperty("PiecesGroupedPerTileWanted", BindingFlags.Public | BindingFlags.Static);
                var active = ff.GetProperty("PiecesGroupedPerTileActive", BindingFlags.Public | BindingFlags.Static);
                try { if (wanted != null || active != null) owner = (wanted != null && (bool)wanted.GetValue(null, null)) || (active != null && (bool)active.GetValue(null, null)); } catch { owner = true; }
                if (owner) { EasyBakeLog.Info("[EBM] pieces grouped per terrain tile: FiresFixes owns pieces grouping (EBM's copy not applied)"); return; }
            }
            try
            {
                var h = new Harmony("com.Fire.FiresEasyBakeMeshes.PieceHeightmapHub");
                h.Patch(AccessTools.Method(typeof(WearNTear), "Start"), transpiler: new HarmonyMethod(typeof(PieceHeightmapHub), nameof(StartT)));
                h.Patch(AccessTools.Method(typeof(WearNTear), "OnDestroy"), transpiler: new HarmonyMethod(typeof(PieceHeightmapHub), nameof(DestroyT)));
                EasyBakeLog.Info("[EBM] pieces grouped per terrain tile: EBM's own copy on (FiresFixes absent or its grouping off)");
            }
            catch (Exception e) { EasyBakeLog.Warn("[EBM] pieces grouped per terrain tile: EBM's copy could not be applied, vanilla subscriptions stay: " + e.Message); }
        }
        internal static string Status() => $"pieces grouped per tile: {c_grouped} grouped, {c_passed} passed to vanilla, transpile swaps Start {c_swapsStart} / OnDestroy {c_swapsDestroy}, FiresFixes hub {(FfActive() ? "active" : "absent/off")}";
    }
}
