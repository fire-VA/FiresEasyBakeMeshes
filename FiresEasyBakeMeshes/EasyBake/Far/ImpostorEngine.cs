using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// EBM impostor engine #1 (was Core's) ([visual], Fire 11-08: FarLands moved up; Tools\SPEC_Core_ImpostorEngine.md draft 2). Our own code; ideas
    /// from FarLands (closed, decompiled for understanding only, Tools\RESEARCH_FarLands_20261108.md).
    ///
    /// Any owner mod (FAT's far forest, FAP's pieces) registers KINDS (a prefab to draw far away) and fills CELLS (any square) with 32-byte
    /// instances. The engine bakes each kind once (64 views of its unlit G-buffer into a slice of a DXT5 atlas array, cached on disk),
    /// keeps every instance in one GPU pool, culls them on the GPU each frame (reach, frustum, the zone hand-off with the game's real
    /// trees, the optional mid-LOD band) and draws all impostors with ONE indirect call, lit live in the deferred pass.
    ///
    /// EBM port (Core 0.2.1216 Far moved into EBM): no feature gate; keys "[Far Impostors]" are bound by EBM itself through
    /// <see cref="Initialize"/> from its own Awake; every key defaults OFF / to the spec value until then.
    /// API v1, add-only. Main thread unless noted.
    /// </summary>
    public static class ImpostorEngine
    {
        public const int ApiVersion = 1;
        internal const string Tag = "[FarImpostors] ";

        public enum Sharpness { Low, Medium, High, Ultra }

        // ── config (owner-bound) ──
        internal static ConfigEntry<bool> CfgEnabled, CfgSmoothViews, CfgMidLod, CfgCacheOnDisk;
        internal static ConfigEntry<Sharpness> CfgSharpness;
        internal static ConfigEntry<float> CfgMidDistance, CfgDistance, CfgBakeBudget, CfgUploadBudget;
        private static bool s_bound;
        private static bool? s_sessionOverride;   // the dev proof command ("fires_impostors on/off"), never saved

        /// <summary>Binds "[Far Impostors]" in EBM's own config file (EBM's Awake calls this once). Idempotent.</summary>
        internal static void Initialize(ConfigFile config)
        {
            if (config == null || s_bound) return;
            s_bound = true;
            const string s = "Far Impostors";
            CfgEnabled = config.Bind(s, "Enabled", false /* 1.2.212: back to OFF - Fire parked impostors; opt-in until he says */, "Draw far trees (and other registered far objects) as baked impostors out to the impostor distance. Off = nothing is baked or drawn.");
            CfgSharpness = config.Bind(s, "Sharpness", Sharpness.Medium, "How sharp far trees look: Low 64, Medium 128, High 192, Ultra 256 pixels per view. Higher costs video memory (about 0.7 / 2.7 / 6 / 11 MB a kind). A change re-bakes in the background; the old look stays until the new one is ready.");
            CfgSmoothViews = config.Bind(s, "Smooth views", true, "Blend the 3 nearest baked views (smooth turning). Off = 1 view, a little cheaper.");
            CfgMidLod = config.Bind(s, "Mid-LOD trees", false, "Draw real far-LOD tree meshes between the game's own trees and the impostors (closer far trees look solid). Costs GPU time.");
            CfgMidDistance = config.Bind(s, "Mid-LOD distance", 1200f, new ConfigDescription("Outer edge of the mid-LOD band in metres.", new AcceptableValueRange<float>(300f, 3500f)));
            CfgDistance = config.Bind(s, "Impostor distance", 2500f, new ConfigDescription("How far impostors are drawn, in metres.", new AcceptableValueRange<float>(600f, 6000f)));
            CfgCacheOnDisk = config.Bind(s, "Cache atlases on disk", true, "Keep baked atlases on disk so a relaunch does not bake again.");
            CfgBakeBudget = config.Bind(s, "Bake budget ms", 2f, new ConfigDescription("Main-thread milliseconds a frame for baking (one view at most a frame).", new AcceptableValueRange<float>(0.5f, 6f)));
            CfgUploadBudget = config.Bind(s, "Upload budget ms", 0.5f, new ConfigDescription("Main-thread milliseconds a frame for uploading far instances.", new AcceptableValueRange<float>(0.1f, 2f)));
            EasyBakeLog.Debug(Tag + "keys bound by EBM");
        }

        internal static bool Enabled => s_sessionOverride ?? (CfgEnabled != null && CfgEnabled.Value);
        internal static Sharpness SharpnessNow => CfgSharpness != null ? CfgSharpness.Value : Sharpness.Medium;
        internal static bool SmoothViews => CfgSmoothViews == null || CfgSmoothViews.Value;
        internal static bool MidLodOn => CfgMidLod != null && CfgMidLod.Value;
        internal static float MidDistance => CfgMidDistance != null ? CfgMidDistance.Value : 1200f;
        internal static float Distance => CfgDistance != null ? CfgDistance.Value : 2500f;
        internal static bool CacheOnDisk => CfgCacheOnDisk == null || CfgCacheOnDisk.Value;
        internal static float BakeBudgetMs => CfgBakeBudget != null ? CfgBakeBudget.Value : 2f;
        internal static float UploadBudgetMs => CfgUploadBudget != null ? CfgUploadBudget.Value : 0.5f;
        internal static void SessionOverride(bool? on) { s_sessionOverride = on; }

        /// <summary>Live flag: true while the engine draws on this client (feature live, on, supported, shaders loaded). Re-read it.</summary>
        public static bool Active => ImpostorDraw.Running;

        /// <summary>Goes up when any kind is re-baked for a new look (season, sharpness, another mod's material change).</summary>
        public static int LookRevision => ImpostorKinds.LookRevision;

        public static event Action OnReady;
        public static event Action OnInstancesChanged;
        /// <summary>Raised when EBM stops drawing a zone's impostors because its real objects stand (diagnostics).</summary>
        public static event Action<Vector2s> ZoneHandedOff;
        internal static void RaiseReady() { try { OnReady?.Invoke(); } catch (Exception e) { EasyBakeLog.Debug(Tag + "OnReady listener threw: " + e.Message); } }
        internal static void RaiseInstancesChanged() { try { OnInstancesChanged?.Invoke(); } catch (Exception e) { EasyBakeLog.Debug(Tag + "OnInstancesChanged listener threw: " + e.Message); } }
        internal static void RaiseHandedOff(Vector2s z) { try { ZoneHandedOff?.Invoke(z); } catch (Exception e) { EasyBakeLog.Debug(Tag + "ZoneHandedOff listener threw: " + e.Message); } }

        // ── kinds ──
        /// <summary>A prefab to draw far away. Returns a kind id, or -1 (Debug WHY line). Idempotent per prefab.</summary>
        public static int RegisterKind(string owner, GameObject prefab, KindOptions options)
        {
            if (!Live()) return -1;
            ImpostorDraw.Ensure();
            ImpostorAssets.Ready();   // the mid-LOD material needs the shader at registration
            return ImpostorKinds.Register(owner, prefab, options);
        }

        public static void ForgetKind(int kind) => ImpostorKinds.Forget(kind);

        public static KindInfo GetKindInfo(int kind) => ImpostorKinds.Info(kind);

        // ── instances ──
        /// <summary>Replaces this owner's cell (any square of the owner's choosing). Upload is time-sliced; the old instances keep drawing
        /// until the new ones are up (no flicker). The array is copied; build it on any thread.</summary>
        public static object SetCell(string owner, long cellKey, Bounds bounds, ImpostorInstance[] instances)
        {
            if (!Live()) return null;
            ImpostorDraw.Ensure();
            return ImpostorPool.SetCell(owner, cellKey, bounds, instances, instances?.Length ?? 0);
        }

        public static object SetCell(string owner, long cellKey, Bounds bounds, ImpostorInstance[] instances, int count)
        {
            if (!Live()) return null;
            ImpostorDraw.Ensure();
            return ImpostorPool.SetCell(owner, cellKey, bounds, instances, count);
        }

        public static void ClearCell(string owner, long cellKey) => ImpostorPool.ClearCell(owner, cellKey);
        public static void ClearOwner(string owner) => ImpostorPool.ClearOwner(owner);

        // ── hand-off ──
        /// <summary>Optional: the owner says this zone's real objects stand (it can only make a zone stand SOONER than EBM's own check).</summary>
        public static void MarkZoneStanding(Vector2s zone, bool standing) => ImpostorHandOff.Mark(zone, standing);

        // ── read access (FAT #10 canopy shade): valid after OnReady ──
        public static GraphicsBuffer InstanceBuffer => ImpostorPool.Buffer;
        public static int InstanceHighWater => ImpostorPool.HighWater;

        internal static bool Live() => FarRunner.Client;   // EBM port: Core had CoreFeatures.IsLive(FarImpostors) (an owner installed) && Client; EBM is the owner, so only Client remains. Enabled is still read where Core read it (ImpostorDraw).

        /// <summary>One status line (console and logs).</summary>
        public static string Status()
        {
            if (!FarRunner.Client) return "far impostors: dedicated server (draws nothing)";
            return $"far impostors: {(Enabled ? "on" : "off")}{(s_sessionOverride.HasValue ? " (session override)" : "")}; {ImpostorDraw.StatusText()}; {ImpostorKinds.StatusText()}; {ImpostorPool.StatusText()}";
        }
    }
}
