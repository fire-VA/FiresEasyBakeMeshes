using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // ── THE LARGEST UPDATER LIST IN A BUILT WORLD, DOING NOTHING ──────────────────────────────────────────
    //
    // The 2026-09-26 va_updaters census on a town: EffectArea 848, ahead of LightFlicker 562 and
    // ZSyncTransform 354. In the same session MonoUpdaters.FixedUpdate(total) measured 68.5 ms/s - the single
    // biggest cost in the game - with every other EBM lever (ZSyncStaticSkip, ClutterLod, LightFlickerLod,
    // ZSFXLod, DestroyTimeSlice, CreateDestroySkip, SectorMirror) already ON.
    //
    // The cost is POPULATION, not work. MonoUpdaters does `container.AddRange(source)` before iterating, so
    // every registered instance costs a copy slot plus an interface dispatch 50x/second before its body runs.
    //
    // WHY MOST OF THEM CAN LEAVE THE LIST PERMANENTLY, read straight off EffectArea:
    //   - CustomFixedUpdate returns unless m_collisions > 0 AND m_collidedWithCharacter.Count > 0.
    //   - OnTriggerEnter only ever ADDS to m_collidedWithCharacter when (m_isHeatType || m_statusEffectHash != 0).
    // So an area that is neither a Heat type nor carries a status effect has a permanently empty list, and its
    // CustomFixedUpdate is dead code for the object's whole lifetime. That is the PlayerBase / NoMonsters /
    // PrivateProperty population: every workbench and base marker in a settlement.
    //
    // NO REFLECTION NEEDED. m_isHeatType is just m_type.HasFlag(Type.Heat), and m_statusEffectHash is set in
    // Awake only when m_statusEffect is non-empty - so `hash != 0` and `!IsNullOrEmpty(m_statusEffect)` are the
    // same test. Both m_type and m_statusEffect are public fields.
    //
    // NOTHING ELSE READS THE UPDATER LIST. The static area queries are populated in Awake (s_allAreas,
    // s_noMonsterAreas, s_noMonsterCloseToAreas, s_BurningAreas) and IsPointInsideArea / GetBaseValue /
    // IsPointPlus025InsideBurningArea run Physics.OverlapSphereNonAlloc against the collider or scan those
    // static lists. Updater membership is an input to none of them, so removing it changes no game behaviour.
    //
    // WHY enabled STAYS TRUE, unlike the piece ZSync skip. OnTriggerEnter and OnTriggerExit live on this SAME
    // component, and Unity does not deliver trigger callbacks to a disabled MonoBehaviour. Disabling an
    // EffectArea would stop m_collisions from ever being counted again and quietly break the NoMonsters and
    // Burning bookkeeping that spawners and fire damage read. So membership is removed from Instances directly
    // and the component is left enabled - Instances is the only part MonoUpdaters looks at.
    //
    // HEAT AND STATUS AREAS ARE LEFT REGISTERED. Gating those on an empty list needs a wake-up, and the only
    // wake-up signal is OnTriggerEnter on a component that must stay enabled anyway. The saving is real but the
    // correctness argument is not free, so measure what this tier removes before reaching for it.
    internal static class EffectAreaIdleSkip
    {
        private const float ReportSeconds = 30f;

        private static int s_deregistered;
        private static int s_kept;
        private static int s_reportedAt = -1;
        private static float s_nextReport;

        internal static void Reset()
        {
            s_deregistered = 0;
            s_kept = 0;
            s_reportedAt = -1;
            s_nextReport = 0f;
        }

        // True when CustomFixedUpdate can never do work for this area, for the reasons in the header.
        private static bool CanNeverTick(EffectArea area)
        {
            if ((area.m_type & EffectArea.Type.Heat) != 0) return false;
            return string.IsNullOrEmpty(area.m_statusEffect);
        }

        // 1.2.216 ([ghost], [lead]): the "inert area(s)" number above counts OnEnable calls (it never goes down), so a rising number is
        // areas being enabled, not proof of a leak. This names them: OnEnables per owning prefab in the last 30 s, how many were the same
        // object enabled again vs a new object, and vanilla's live list size (Debug only, bounded).
        private static readonly Dictionary<string, int> s_enablesByPrefab = new Dictionary<string, int>();
        private static readonly HashSet<int> s_seenIds = new HashSet<int>();
        private static int s_newObjects, s_reEnabled; private static float s_nextSource;
        private static readonly HashSet<string> s_stacked = new HashSet<string>();
        internal static string Stack()
        {
            var st = new System.Diagnostics.StackTrace(2, false);
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            for (int i = 0; i < st.FrameCount && shown < 8; i++)
            {
                var m = st.GetFrame(i)?.GetMethod();
                if (m == null) continue;
                string t = m.DeclaringType != null ? m.DeclaringType.Name : "?";
                if (t.StartsWith("EffectArea", System.StringComparison.Ordinal) || t.Contains("Harmony") || t.StartsWith("DMD", System.StringComparison.Ordinal)) continue;
                if (shown > 0) sb.Append(" <- ");
                sb.Append(t).Append('.').Append(m.Name);
                shown++;
            }
            return sb.ToString();
        }

        private static void NoteSource(EffectArea area)
        {
            if (!EasyBakeLog.DebugOn()) return;
            var view = area.GetComponentInParent<ZNetView>();
            string name = view != null ? Utils.GetPrefabName(view.gameObject) : area.transform.root.name;
            s_enablesByPrefab.TryGetValue(name, out int n);
            if (n > 0 || s_enablesByPrefab.Count < 64) s_enablesByPrefab[name] = n + 1;
            if (s_seenIds.Count < 20000 && s_seenIds.Add(area.GetInstanceID()))
            {
                s_newObjects++;
                // 1.2.218: who made it - the call stack of the first new area per prefab per 30 s (Debug only; at most 6 stacks a window)
                if (s_stacked.Count < 6 && s_stacked.Add(name))
                    EasyBakeLog.Debug($"[EffectAreaSource] new EffectArea under {name} (object '{area.gameObject.name}', parent '{(area.transform.parent != null ? area.transform.parent.name : "-")}'), created by: {Stack()}");
            }
            else s_reEnabled++;
            float now = Time.realtimeSinceStartup;
            if (now < s_nextSource) return;
            s_nextSource = now + 30f;
            var list = new List<KeyValuePair<string, int>>(s_enablesByPrefab);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder($"[EffectAreaSource] last 30 s: {s_newObjects + s_reEnabled} EffectArea OnEnable(s), {s_newObjects} new object(s), {s_reEnabled} the same object enabled again; areas still on the MonoUpdaters list {EffectArea.Instances.Count}; by prefab:");
            for (int i = 0; i < list.Count && i < 12; i++) sb.Append(' ').Append(list[i].Key).Append(' ').Append(list[i].Value).Append(';');
            EasyBakeLog.Debug(sb.ToString());
            s_enablesByPrefab.Clear(); s_newObjects = s_reEnabled = 0; s_stacked.Clear();
        }

        internal static void OnEnabled(EffectArea area)
        {
            if (area == null) return;
            NoteSource(area);
            if (!CanNeverTick(area)) { s_kept++; Report(); return; }

            // OnEnable has already added it; drop it back out. Remove is a no-op if it is somehow absent.
            EffectArea.Instances.Remove(area);
            s_deregistered++;
            Report();
        }

        // A rolling summary rather than one line per area: it prints only when the count has actually moved
        // since the last print, so it converges to silence once the area set is stable instead of flooding.
        private static void Report()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_nextReport) return;
            if (s_deregistered == s_reportedAt) return;
            s_nextReport = now + ReportSeconds;
            s_reportedAt = s_deregistered;
            EasyBakeLog.Debug(
                $"EffectArea idle skip: {s_deregistered} inert area(s) out of the MonoUpdaters list, {s_kept} left "
                + "registered (Heat or status-effect areas, which can tick).");
        }
    }

    // 1.2.222 ([ghost], 1.2.220 O census: lantern / torch pieces are created again and again while standing still, by ZNetScene.CreateObject
    // through FGN's StreamQueue): who DESTROYED them first? The call stack of the first EffectArea destroyed per prefab per 30 s (Debug only,
    // at most 6 a window), with how many were destroyed in the window, by prefab.
    [HarmonyPatch(typeof(EffectArea), "OnDestroy")]
    internal static class EffectArea_OnDestroy_Source
    {
        private static readonly System.Collections.Generic.Dictionary<string, int> s_byPrefab = new System.Collections.Generic.Dictionary<string, int>();
        private static readonly System.Collections.Generic.HashSet<string> s_stacked = new System.Collections.Generic.HashSet<string>();
        private static float s_next;

        private static void Prefix(EffectArea __instance)
        {
            if (__instance == null || !EasyBakeLog.DebugOn()) return;
            var view = __instance.GetComponentInParent<ZNetView>();
            string name = view != null ? Utils.GetPrefabName(view.gameObject) : __instance.transform.root.name;
            s_byPrefab.TryGetValue(name, out int n);
            if (n > 0 || s_byPrefab.Count < 64) s_byPrefab[name] = n + 1;
            if (s_stacked.Count < 6 && s_stacked.Add(name))
                EasyBakeLog.Debug($"[EffectAreaSource] EffectArea destroyed under {name} (object '{__instance.gameObject.name}'), destroyed by: {EffectAreaIdleSkip.Stack()}");
            float now = Time.realtimeSinceStartup;
            if (now < s_next) return;
            s_next = now + 30f;
            var sb = new System.Text.StringBuilder("[EffectAreaSource] EffectAreas destroyed in the last 30 s by prefab:");
            foreach (var kv in s_byPrefab) sb.Append(' ').Append(kv.Key).Append(' ').Append(kv.Value).Append(';');
            EasyBakeLog.Debug(sb.ToString());
            s_byPrefab.Clear(); s_stacked.Clear();
        }
    }
    [HarmonyPatch(typeof(EffectArea), "OnEnable")]
    internal static class EffectArea_OnEnable_IdleSkip
    {
        private static void Postfix(EffectArea __instance)
        {
            if (!FiresEasyBakeMeshesPlugin.EffectAreaIdleSkipEnabled.Value) return;
            EffectAreaIdleSkip.OnEnabled(__instance);
        }
    }
}
