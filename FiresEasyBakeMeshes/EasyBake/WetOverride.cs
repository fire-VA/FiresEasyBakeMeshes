using System;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.131 ([visual], Fire 10-26 shot 525: "stop the inside of pieces from looking wet in the rain ... without needing a custom
    // shader"). Vanilla Custom/Piece (1.0 rip, Shaders/Piece.shader: "float _Wet;" read into the fragment's wet gloss, gated by the
    // _AddRain toggle) and TossinShade FiresPiece (smoothness lerp by skyMask * _Wet) both take the wet look from the GLOBAL _Wet
    // that EnvMan sets every frame, with no roof test. A MaterialPropertyBlock value overrides a global for that one renderer, so:
    //   live pieces with vanilla's own roof flag (WearNTear.m_haveRoof) get _Wet 0 in their block while it rains; a piece that
    //   loses its roof follows the global again. Baked groups draw their roofed range with _Wet 0 (ZoneInstanceGroup).
    // Other mods' block values are kept: the block is read back before _Wet is written.
    internal static class WetOverride
    {
        internal static readonly int WetId = Shader.PropertyToID("_Wet");
        private const float LiveRange = 80f;
        private static readonly AccessTools.FieldRef<WearNTear, bool> s_haveRoof = AccessTools.FieldRefAccess<WearNTear, bool>("m_haveRoof");
        private static readonly MaterialPropertyBlock s_block = new MaterialPropertyBlock();
        private static readonly Dictionary<WearNTear, Renderer[]> s_renderers = new Dictionary<WearNTear, Renderer[]>();
        private static readonly HashSet<Renderer> s_dried = new HashSet<Renderer>();
        private static readonly List<Renderer> s_scratch = new List<Renderer>();
        private static float s_next, s_nextLog;
        private static bool s_ran, s_wasRaining;
        private static int s_bakedDry, s_bakedOpen, s_liveDry, s_liveOpen, s_liveVedr;   // 1.2.188: s_liveVedr = left to Vedr
        private static string s_idle = "";
        // 1.2.254 [Optimize] TickGroupEventDriven: what the last walk saw
        private const float WalkMove = 16f, WalkFallbackSeconds = 5f;
        // 1.2.254: the roof flag's own event. Vanilla re-tests a roof only through UpdateCover (every 4 s while wet) -> HaveRoof, and assigns the answer after it returns, so in a postfix the field still holds the old value.
        private static bool s_roofChanged; private static int s_roofEvents;
        [HarmonyLib.HarmonyPatch]
        internal static class RoofEvent
        {
            [HarmonyLib.HarmonyPatch(typeof(WearNTear), "HaveRoof"), HarmonyLib.HarmonyPostfix]
            private static void Postfix(bool __result, bool ___m_haveRoof) { if (__result != ___m_haveRoof) { s_roofChanged = true; s_roofEvents++; } }
        }
        private static int s_walkGen = -1, s_walkPieces = -1; private static bool s_walkVedr, s_followValid; private static float s_walkGlobal = -1f, s_walkFallback, s_followGlobal; private static Vector3 s_walkAt;

        internal static bool Raining => EnvMan.instance != null && EnvMan.IsWet();

        // 1.2.188 (Fire 11-08: "indoor wet from ebm steps aside to vedr when both installed"; [seasons]: VedrApi.IndoorWetActive, ApiVersion 17,
        // Vedr 0.8.183, covers materials drawn with Vedr/Piece): while Vedr's live flag is true (re-read every Tick, never ApiVersion alone), EBM's
        // indoor dry stands down for every renderer on Vedr/Piece (roofed-dry and roof undersides) and for the baked groups' roofed _Wet 0 split
        // (EBM's instanced pieces draw Vedr's own Vedr/Piece copies under Vedr: ApplyPieceShader). Renderers on any other shader keep EBM's dry.
        // ShelterRain (falling-weather particles) is unchanged: Vedr's ShelterWeather already cedes to it. Without Vedr, everything works as before.
        private const string VedrPieceShader = "Vedr/Piece";
        private static bool s_vedrBound;
        internal static bool VedrIndoorWet { get; private set; }
        private static readonly List<Material> s_mats = new List<Material>();

        // Baked groups dry their roofed range only while EBM owns indoor wet. Cached once a second by Tick (read every frame per group).
        internal static bool BakedDries => Raining && !VedrIndoorWet;

        // 1.2.210 ([ghost], lead: "EBM relies on no other mod"): the season mod PUSHES its indoor-wet ownership (EbmApi.SetIndoorWetOwner) instead
        // of EBM reading VedrApi.IndoorWetActive by name. Never pushed = false = EBM owns indoor wet, as without Vedr.
        private static bool s_pushedIndoorWet;
        internal static void PushIndoorWetOwner(bool seasonModOwns)
        {
            if (!s_vedrBound) { s_vedrBound = true; EasyBakeLog.Debug("[EBM] wet override: indoor-wet ownership is pushed by the season mod (EbmApi.SetIndoorWetOwner)"); }
            s_pushedIndoorWet = seasonModOwns;
        }

        private static void ReadVedr()
        {
            bool on = s_pushedIndoorWet;
            if (on == VedrIndoorWet) return;
            VedrIndoorWet = on;
            InsideGeneration++;   // baked groups re-split with or without the dry range
            EasyBakeLog.Debug(on ? "[EBM] wet override: stands down to Vedr (VedrApi.IndoorWetActive true): Vedr/Piece renderers and baked roofed ranges follow Vedr's indoor wet"
                                 : "[EBM] wet override: resumes (VedrApi.IndoorWetActive false): EBM dries roofed pieces again");
        }

        private static bool OnVedrPiece(Renderer r)
        {
            r.GetSharedMaterials(s_mats);
            foreach (var m in s_mats)
                if (m != null && m.shader != null && m.shader.name == VedrPieceShader) return true;
            return false;
        }

        internal static void NoteBaked(int dry, int open) { s_bakedDry = dry; s_bakedOpen = open; }

        // 1.2.132 (Fire shot 535: "the undersides of roofs still render wet"): vanilla's wet gloss is gated only by the sky-depth
        // camera (Piece.shader: _SkyAlphaTexture depth vs the fragment, not its facing), so a roof's underside reads as under open sky like
        // its top. While the local player is in shelter (1 s in, 2 s out), every piece over their head within the shelter's reach draws
        // dry too: from inside only undersides are seen. Generation bumps re-split the baked groups.
        private const float OverheadReach = 20f, OverheadAbove = 1.6f;
        internal static int InsideGeneration { get; private set; }
        internal static bool Inside { get; private set; }
        private static Vector3 s_insideAt;
        private static float s_shelterSince = -1f, s_openSince = -1f;
        private static int s_overheadDried;

        internal static bool Overhead(Vector3 lowestPoint)
        {
            if (!Inside) return false;
            float dx = lowestPoint.x - s_insideAt.x, dz = lowestPoint.z - s_insideAt.z;
            return dx * dx + dz * dz <= OverheadReach * OverheadReach && lowestPoint.y >= s_insideAt.y + OverheadAbove;
        }

        private static void UpdateInside(Player player)
        {
            bool shelter = player.InShelter();
            float now = Time.time;
            if (shelter) { s_openSince = -1f; if (s_shelterSince < 0f) s_shelterSince = now; }
            else { s_shelterSince = -1f; if (s_openSince < 0f) s_openSince = now; }
            bool want = Inside ? !(s_openSince >= 0f && now - s_openSince >= 2f) : (s_shelterSince >= 0f && now - s_shelterSince >= 1f);
            Vector3 at = player.transform.position;
            if (want != Inside || (want && (at - s_insideAt).sqrMagnitude > 16f))
            {
                if (want != Inside) EasyBakeLog.Debug(want ? $"[EBM] wet override: player inside (InShelter) at {at}: roof undersides overhead within {OverheadReach:0} m draw dry" : "[EBM] wet override: player outside: roof undersides follow the rain again");
                Inside = want; s_insideAt = at; InsideGeneration++;
            }
        }

        // 1.2.134 ([visual], overnight #6 RAM census): destroyed pieces / renderers leave keys behind; drop them once a minute
        // instead of holding their managed wrappers until the 20k cap or the end of the rain.
        private static float s_nextPrune;
        private static readonly List<WearNTear> s_deadKeys = new List<WearNTear>();

        private static void Prune()
        {
            if (Time.time < s_nextPrune) return;
            s_nextPrune = Time.time + 60f;
            s_deadKeys.Clear();
            foreach (var k in s_renderers.Keys) if (k == null) s_deadKeys.Add(k);
            foreach (var k in s_deadKeys) s_renderers.Remove(k);
            int dried = s_dried.RemoveWhere(r => r == null);
            if (s_deadKeys.Count > 0 || dried > 0)
                EasyBakeLog.Debug($"[EBM] wet override: pruned {s_deadKeys.Count} destroyed piece(s), {dried} destroyed renderer(s); holding {s_renderers.Count} piece(s), {s_dried.Count} renderer(s)");
        }

        // 1.2.169: PluginEnabled off. A value in a block cannot be taken out one key at a time and other mods keep theirs there, so renderers already written keep following the
        // global, or they would stay frozen at the value of the moment. Nothing new is written.
        internal static void FollowWhileOff()
        {
            Inside = false;
            s_wasRaining = false;
            if (s_dried.Count == 0 || Time.time < s_next) return;
            s_next = Time.time + 1f;
            FollowGlobal(false);
        }

        internal static void Tick()
        {
            if (!s_ran) { s_ran = true; EasyBakeLog.Debug("[EBM] wet override executing (first tick): roofed pieces draw dry (_Wet 0 per renderer) while it rains"); }
            Prune();
            if (Time.time < s_next) return;
            s_next = Time.time + 1f;
            ReadVedr();   // 1.2.188
            var player = Player.m_localPlayer;
            bool raining = Raining;
            string why = player == null ? "no local player" : !raining ? "not raining (EnvMan.IsWet false)" : null;
            if (why != s_idle) { s_idle = why; if (why != null) EasyBakeLog.Debug("[EBM] wet override idle: " + why); }
            bool eventDriven = EbmSelfCost.EventDriven;   // 1.2.254
            if (!raining)
            {
                // 1.2.254 [Optimize] TickGroupEventDriven: once the rain is over, renderers only need writing while the global _Wet is still moving (EnvMan fades it over 15 s)
                if (eventDriven && !s_wasRaining)
                {
                    float wetNow = Shader.GetGlobalFloat(WetId);
                    if (s_dried.Count == 0 || (s_followValid && Mathf.Approximately(wetNow, s_followGlobal))) { EbmSelfCost.Sub(EbmSelfCost.SubWetFollow, false); return; }
                    s_followGlobal = wetNow; s_followValid = true;
                }
                if (eventDriven) EbmSelfCost.Sub(EbmSelfCost.SubWetFollow, true);
                FollowGlobal(s_wasRaining); s_wasRaining = false; return;
            }
            bool rainOnset = !s_wasRaining;   // 1.2.254
            s_wasRaining = true;
            s_followValid = false;
            if (player == null) return;
            UpdateInside(player);
            s_overheadDried = 0;

            float global = Shader.GetGlobalFloat(WetId);
            Vector3 at = player.transform.position;
            // 1.2.254 [Optimize] TickGroupEventDriven: the walk over every piece runs when something it reads changed. A roof flag (vanilla's WearNTear.UpdateWear) has no event: 5 s fallback.
            float range = LiveRange;
            if (eventDriven)
            {
                range = LiveRange + WalkMove;   // a piece within LiveRange of the player is always within this of the last walk's spot
                int pieces = WearNTear.GetAllInstances().Count;
                bool walk = rainOnset || s_roofChanged || InsideGeneration != s_walkGen || VedrIndoorWet != s_walkVedr || pieces != s_walkPieces || Mathf.Abs(global - s_walkGlobal) > 0.02f
                            || (at - s_walkAt).sqrMagnitude > WalkMove * WalkMove || Time.time >= s_walkFallback;
                if (!walk) { EbmSelfCost.Sub(EbmSelfCost.SubWetWalk, false); return; }
                s_roofChanged = false; s_walkGen = InsideGeneration; s_walkVedr = VedrIndoorWet; s_walkPieces = pieces; s_walkGlobal = global; s_walkAt = at; s_walkFallback = Time.time + WalkFallbackSeconds;
                EbmSelfCost.Sub(EbmSelfCost.SubWetWalk, true);
            }
            s_liveDry = s_liveOpen = s_liveVedr = 0;
            foreach (var wnt in WearNTear.GetAllInstances())
            {
                if (wnt == null || (wnt.transform.position - at).sqrMagnitude > range * range) continue;
                bool roof = s_haveRoof(wnt);
                foreach (var r in RenderersOf(wnt))
                {
                    if (r == null) continue;
                    if (VedrIndoorWet && OnVedrPiece(r))   // 1.2.188: Vedr owns this renderer's indoor wet; hand back anything EBM dried
                    {
                        if (s_dried.Remove(r)) Write(r, global);
                        s_liveVedr++;
                        continue;
                    }
                    bool overhead = !roof && Overhead(r.bounds.min);
                    if (overhead) { Write(r, 0f); s_dried.Add(r); s_liveDry++; s_overheadDried++; continue; }
                    if (roof) { Write(r, 0f); s_dried.Add(r); s_liveDry++; }
                    else
                    {
                        s_liveOpen++;
                        if (s_dried.Contains(r)) Write(r, global);   // lost its roof: wet like the rest (follows the global)
                    }
                }
            }
            if (Time.time >= s_nextLog)
            {
                s_nextLog = Time.time + 10f;
                EasyBakeLog.Debug($"[EBM] wet override: {s_liveDry} live renderer(s) dry under roof, {s_liveOpen} wet in the open, {s_liveVedr} left to Vedr (within {LiveRange:0} m); " +
                                  $"baked: {s_bakedDry} instance(s) dry under roof, {s_bakedOpen} open (last re-split); property _Wet (global {global:0.00}); roof flag events {s_roofEvents}; roof undersides dried {s_overheadDried} (player {(Inside ? "inside" : "outside")})");
            }
        }

        private static Renderer[] RenderersOf(WearNTear wnt)
        {
            if (s_renderers.TryGetValue(wnt, out var rs) && rs != null) return rs;
            rs = wnt.GetComponentsInChildren<Renderer>(true);
            s_renderers[wnt] = rs;
            if (s_renderers.Count > 20000) s_renderers.Clear();   // destroyed pieces leave keys behind; a cheap reset
            return rs;
        }

        private static void Write(Renderer r, float wet)
        {
            r.GetPropertyBlock(s_block);
            if (s_block.HasFloat(WetId) && Mathf.Approximately(s_block.GetFloat(WetId), wet)) return;
            s_block.SetFloat(WetId, wet);
            r.SetPropertyBlock(s_block);
        }

        // Out of the rain every renderer EBM ever wrote follows the global (a block value cannot be removed one key at a time, so it
        // is kept equal to the global instead; the global decays to dry after rain).
        private static void FollowGlobal(bool announce)
        {
            float global = Shader.GetGlobalFloat(WetId);
            s_scratch.Clear();
            foreach (var r in s_dried) if (r != null) s_scratch.Add(r);
            foreach (var r in s_scratch) Write(r, global);
            if (s_dried.Count != s_scratch.Count) { s_dried.Clear(); foreach (var r in s_scratch) s_dried.Add(r); }
            if (announce) EasyBakeLog.Debug($"[EBM] wet override: rain over, {s_scratch.Count} renderer(s) follow the global _Wet again");
        }
    }
}
