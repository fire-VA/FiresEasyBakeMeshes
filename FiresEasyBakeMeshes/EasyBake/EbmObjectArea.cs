using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.250 (moved from FGN's ClientCleanupThrottle, Fire's SP split, plan row #3). Vanilla gathers every near + distant ZDO into two lists 30 times a second so
    // that CreateObjects and RemoveObjects can walk them. The pacer already learns new near ZDOs from events, so on a client or local host:
    //  - [Optimize] IdleObjectAreaSkip: with nothing to create or remove, same zone, the whole pass is skipped between the 10 Hz removal scans;
    //  - [Optimize] EventDrivenObjectArea (off until its A/B, as it was in FGN): between full gathers (at most EventAreaFullPassSeconds apart, and at once on any
    //    change) a LIGHT pass calls vanilla's CreateObjects with an empty near list and the distant ZDOs the last full gather left uncreated. RemoveObjects is not
    //    called on a light pass, so an object leaving the area goes at the next full gather.
    // Called from EBM's CreateDestroyObjects prefix, after its dedicated-server, no-player and teleport returns. ebm_eventarea flips the event area for one session.
    [HarmonyPatch]
    internal static class EbmObjectArea
    {
        private static long s_skipped, s_light, s_run, s_full;
        private static float s_nextFull, s_nextReport;
        private static bool s_idleSaid, s_eventSaid, s_eventBroken, s_cmd, s_haveSd;
        private static int s_override = -1;   // -1 = the cfg decides
        private static SimulationDistance s_fullSd;
        private static readonly List<ZDO> s_distantPending = new List<ZDO>();
        private static readonly List<ZDO> s_noNear = new List<ZDO>();
        private static System.Action<ZNetScene, List<ZDO>, List<ZDO>> s_createObjects;
        private static AccessTools.FieldRef<ZNetScene, List<ZDO>> s_distantRef;

        private static bool IdleOn => FiresEasyBakeMeshesPlugin.IdleObjectAreaSkip != null && FiresEasyBakeMeshesPlugin.IdleObjectAreaSkip.Value;
        private static bool EventOn => !s_eventBroken && (s_override >= 0 ? s_override == 1 : (FiresEasyBakeMeshesPlugin.EventDrivenObjectArea != null && FiresEasyBakeMeshesPlugin.EventDrivenObjectArea.Value));
        private static float FullGap => FiresEasyBakeMeshesPlugin.EventAreaFullPassSeconds != null ? FiresEasyBakeMeshesPlugin.EventAreaFullPassSeconds.Value : 1f;

        private static bool SameSimDistance() => s_haveSd && ZNet.instance.GetSyncedSimulationDistance().Equals(s_fullSd);

        // True = the vanilla pass is not needed this tick (a light pass may have run instead).
        internal static bool TrySkip(ZNetScene scene)
        {
            if (!IdleOn || !DestroyTimeSlicer.RemovalPassOn) return false;
            bool eventMode = EventOn;
            if (!StreamPacer.Active || (!eventMode && StreamPacer.HasWork) || DestroyTimeSlicer.Pending) { s_run++; return false; }
            if (!DestroyTimeSlicer.ScannedThisZone || !StreamPacer.SeededFor(ZoneSystem.GetZone(ZNet.instance.GetReferencePosition()))) { s_run++; return false; }
            float now = Time.unscaledTime;
            if (eventMode)
            {
                if (now >= s_nextFull || !SameSimDistance()) { s_run++; return false; }
                if (StreamPacer.HasWork || s_distantPending.Count > 0) { if (!LightPass(scene)) { s_run++; return false; } s_light++; }
                else s_skipped++;
                Report(now);
                return true;
            }
            if (DestroyTimeSlicer.IdleScanDue) { s_run++; return false; }
            s_skipped++;
            if (!s_idleSaid) { s_idleSaid = true; EasyBakeLog.Info("[Stream] idle object-area skip on: with nothing to create or remove, the full pass runs 10 times a second and on any change"); }
            Report(now);
            return true;
        }

        private static bool LightPass(ZNetScene scene)
        {
            try
            {
                if (s_createObjects == null) s_createObjects = AccessTools.MethodDelegate<System.Action<ZNetScene, List<ZDO>, List<ZDO>>>(AccessTools.Method(typeof(ZNetScene), "CreateObjects", new[] { typeof(List<ZDO>), typeof(List<ZDO>) }));
                for (int i = s_distantPending.Count - 1; i >= 0; i--)
                {
                    ZDO z = s_distantPending[i];
                    if (z == null || z.Created || !z.IsValid()) { s_distantPending[i] = s_distantPending[s_distantPending.Count - 1]; s_distantPending.RemoveAt(s_distantPending.Count - 1); }
                }
                s_noNear.Clear();
                s_createObjects(scene, s_noNear, s_distantPending);
                if (!s_eventSaid) { s_eventSaid = true; EasyBakeLog.Info($"[Stream] event driven object area on: between full gathers creates come from the event queue; a full gather at least every {FullGap:0.0} s and on any change"); }
                return true;
            }
            catch (System.Exception e)
            {
                s_eventBroken = true;   // never writes the cfg
                EasyBakeLog.Warn("[Stream] event driven object area stood down for this session after an error; the full pass runs as before: " + e.Message);
                return false;
            }
        }

        // After a full vanilla pass: remember when, at which simulation distance, and the distant objects it left uncreated.
        [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void AfterFullPass(ZNetScene __instance, bool __runOriginal)
        {
            if (!__runOriginal || !EventOn || ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            try
            {
                if (s_distantRef == null) s_distantRef = AccessTools.FieldRefAccess<ZNetScene, List<ZDO>>("m_tempCurrentDistantObjects");
                List<ZDO> distant = s_distantRef(__instance);
                s_distantPending.Clear();
                if (distant != null) for (int i = 0; i < distant.Count; i++) { ZDO z = distant[i]; if (z != null && !z.Created) s_distantPending.Add(z); }
                s_fullSd = ZNet.instance.GetSyncedSimulationDistance(); s_haveSd = true;
                s_nextFull = Time.unscaledTime + FullGap;
                s_full++;
            }
            catch { s_haveSd = false; }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown)), HarmonyPostfix]
        private static void OnShutdown() { s_distantPending.Clear(); s_haveSd = false; s_nextFull = 0f; }

        private static void Report(float now)
        {
            if (now < s_nextReport) return;
            if (s_nextReport > 0f && EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"[Stream] object area, last 30 s: {s_skipped} idle pass(es) skipped, {s_light} light, {s_run} full ({s_full} full gather(s) under the event area); {s_distantPending.Count} distant object(s) waiting");
            s_skipped = s_light = s_run = s_full = 0; s_nextReport = now + 30f;
        }

        internal static void RegisterCommand()
        {
            if (s_cmd) return;
            s_cmd = true;
            new Terminal.ConsoleCommand("ebm_eventarea", "[on|off|cfg] - FiresEasyBakeMeshes: event-driven object area for this session only (the cfg is not changed); no argument = show.", new Terminal.ConsoleEvent(OnCommand), isCheat: true);
        }

        private static void OnCommand(Terminal.ConsoleEventArgs args)
        {
            string w = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (w == "on") s_override = 1; else if (w == "off") s_override = 0; else if (w == "cfg") s_override = -1;
            else if (w.Length > 0) { args.Context?.AddString("ebm_eventarea: use on, off or cfg"); return; }
            if (w.Length > 0) { s_nextFull = 0f; s_haveSd = false; EasyBakeLog.Info($"[Stream] ebm_eventarea {w}: event driven object area is now {(EventOn ? "ON" : "OFF")} for this session (cfg untouched)"); }
            args.Context?.AddString($"ebm_eventarea: {(EventOn ? "ON" : "OFF")} ({(s_override < 0 ? "from the cfg" : "session override")}{(s_eventBroken ? ", stood down after an error" : "")})");
        }
    }
}
