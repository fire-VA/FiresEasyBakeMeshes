using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.437 ([visual]; Tools\DESIGN_fat_water_handoff_20261117.md 3.1 / 3.1b): EBM as a provider for FAT's water look host (FAT 0.2.1455+,
    // VerdantsAscent.FatApi.RegisterWaterLook, WaterLookApiVersion 1). FAT's own water renderers (inland sheets, placed sheets, combiner bodies) then
    // wear EBM/Water while EBM draws the water. FAT is bound BY PRESENCE (reflection, no compile-time reference; the same shape as EbmFtsGlobals'
    // Vedr.VedrApi lookup): the callbacks are our own methods taking object, wrapped into delegates of FAT's generic types through Thunk<TReq>
    // (MakeGenericType over the request type found at run time). FAT absent / another API version = nothing happens (one Debug line).
    // [Water] FAT Water Look (default OFF) gates registering at all; off = Unregister (FAT puts its own materials back).
    internal static class EbmFatWaterLook
    {
        internal const string Owner = "FiresEasyBakeMeshes";
        internal const int Priority = 300;
        internal static ConfigEntry<bool> On;

        // FAT's names (FatApi.cs 0.2.1455)
        private const string T_Api = "VerdantsAscent.FatApi", T_Req = "VerdantsAscent.FatWaterLookRequest";

        private static bool s_bindTried, s_bound, s_registered, s_saidInfo, s_lastActive;
        private static MethodInfo s_register, s_unregister, s_refresh;
        private static FieldInfo f_original, f_kind, f_bounds, f_bodySize;
        private static Delegate d_active, d_build, d_apply, d_release;

        // (original, kind) -> our copy; every copy we built, for release + the stencil key
        private static readonly Dictionary<long, Material> s_cache = new Dictionary<long, Material>();
        private static readonly Dictionary<Material, long> s_keyOf = new Dictionary<Material, long>();
        private static bool s_waitingTemplate;
        private static MaterialPropertyBlock s_mpb;

        private static readonly int P_IsLod = Shader.PropertyToID("_IsLod"), P_VolumeSize = Shader.PropertyToID("_VolumeSize");

        internal static void Bind(ConfigFile config)
        {
            On = config.Bind("Water", "FAT Water Look", false /* 1.2.437: OFF until Fire's look check */,
                "With FiresAdminTerrain installed: its inland water (rivers, lakes, ponds, placed water) is drawn with EBM's water shader while EBM draws the water. Off = FiresAdminTerrain draws its own water. Applies live.");
            On.SettingChanged += (_, __) => Sync("FAT Water Look key");
            EbmApi.ShaderOwnershipChanged += OnOwnership;
        }

        private static void OnOwnership() => Sync("shader ownership changed");

        internal static IEnumerable<Material> Built => s_keyOf.Keys;

        // EbmWater.Tick, once a second (before its key gate): bind once a world exists (every plugin is loaded by then), keep the registration
        // matching the key, and tell FAT when EBM's drawing state changes.
        internal static void Tick()
        {
            if (!s_bindTried) { if (ZNetScene.instance == null) return; TryBind(); }
            if (!s_bound) return;
            bool want = On != null && On.Value;
            if (want != s_registered) { Sync("key"); return; }
            if (s_registered && EbmWater.Active != s_lastActive) Sync(EbmWater.Active ? "EBM water drawing" : "EBM water stopped");
        }

        private static void Sync(string why)
        {
            if (!s_bindTried) return;   // the first Tick with a world binds and syncs
            if (!s_bound) return;
            bool want = On != null && On.Value;
            try
            {
                if (want && !s_registered)
                {
                    s_register.Invoke(null, new object[] { Owner, Priority, d_active, d_build, d_apply, d_release });
                    s_registered = true; s_lastActive = EbmWater.Active;
                    if (!s_saidInfo) { s_saidInfo = true; EasyBakeLog.Info($"[Water] FAT water look: EBM provides FAT's inland sheets and water bodies with EBM/Water while EBM draws the water (priority {Priority})"); }
                }
                else if (!want && s_registered)
                {
                    s_unregister.Invoke(null, new object[] { Owner });
                    s_registered = false;
                    EasyBakeLog.Info($"[Water] FAT water look: EBM withdrawn ({why}); FAT draws its own water");
                }
                else if (s_registered)
                {
                    s_lastActive = EbmWater.Active;
                    s_refresh.Invoke(null, new object[] { Owner });
                    EasyBakeLog.Debug($"[Water] FAT water look refreshed ({why}; EBM water {(s_lastActive ? "drawing" : "not drawing")})");
                }
            }
            catch (Exception e) { EasyBakeLog.Warn($"[Water] FAT water look: {why}: {(e.InnerException ?? e).Message}"); }
        }

        // EbmWater.Swap, on a successful zone swap: an inland sheet asked before a template existed is asked again
        internal static void ZoneSwapped()
        {
            if (!s_waitingTemplate || !s_registered) return;
            s_waitingTemplate = false;
            try { s_refresh.Invoke(null, new object[] { Owner }); } catch { }
        }

        private static void TryBind()
        {
            s_bindTried = true;
            var api = OptionalType.Find(T_Api);
            var req = OptionalType.Find(T_Req);
            if (api == null || req == null) { EasyBakeLog.Debug("[Water] FAT water look: FiresAdminTerrain's water look host not found (FAT absent or older than 0.2.1455); nothing to provide"); return; }
            try
            {
                var ver = api.GetField("WaterLookApiVersion", BindingFlags.Public | BindingFlags.Static);
                int v = ver != null ? Convert.ToInt32(ver.GetValue(null)) : -1;
                if (v != 1) { EasyBakeLog.Debug($"[Water] FAT water look: FAT's WaterLookApiVersion is {v}, EBM speaks 1; nothing provided"); return; }
                var tFuncBuild = typeof(Func<,>).MakeGenericType(req, typeof(Material));
                var tApply = typeof(Action<,>).MakeGenericType(typeof(Renderer), req);
                s_register = api.GetMethod("RegisterWaterLook", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(int), typeof(Func<bool>), tFuncBuild, tApply, typeof(Action<Material>) }, null);
                s_unregister = api.GetMethod("UnregisterWaterLook", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                s_refresh = api.GetMethod("RefreshWaterLook", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                const BindingFlags fi = BindingFlags.Public | BindingFlags.Instance;
                f_original = req.GetField("Original", fi); f_kind = req.GetField("Kind", fi);
                f_bounds = req.GetField("RendererBounds", fi); f_bodySize = req.GetField("BodySize", fi);
                if (s_register == null || s_unregister == null || s_refresh == null || f_original == null || f_kind == null || f_bounds == null || f_bodySize == null)
                { EasyBakeLog.Debug("[Water] FAT water look: FAT's API v1 members not all found; nothing provided"); return; }
                var thunk = typeof(Thunk<>).MakeGenericType(req);
                d_active = new Func<bool>(ActiveNow);
                d_build = Delegate.CreateDelegate(tFuncBuild, thunk.GetMethod("Build", BindingFlags.Public | BindingFlags.Static));
                d_apply = Delegate.CreateDelegate(tApply, thunk.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static));
                d_release = new Action<Material>(Release);
                s_bound = true;
                EasyBakeLog.Debug("[Water] FAT water look: FiresAdminTerrain's water look host found (API v1)");
            }
            catch (Exception e) { s_bound = false; EasyBakeLog.Debug("[Water] FAT water look: binding failed: " + (e.InnerException ?? e).Message); }
        }

        // FAT's generic delegate types over a request type known only at run time; boxes the struct into our object-taking methods
        private static class Thunk<TReq>
        {
            public static Material Build(TReq r) => BuildBoxed(r);
            public static void Apply(Renderer rend, TReq r) => ApplyBoxed(rend, r);
        }

        private static bool ActiveNow() => EbmWater.Active;

        private static Material BuildBoxed(object req)
        {
            var original = f_original.GetValue(req) as Material;
            int kind = Convert.ToInt32(f_kind.GetValue(req));   // FatWaterKind: 0 InlandSheet, 1 Body, 2 PlacedSheet
            var ours = Indirect.EbmIndirectAssets.WaterShader;
            if (original == null || ours == null || !EbmWater.Active) return null;
            long key = ((long)original.GetInstanceID() << 8) | (uint)(kind & 0xFF);
            if (s_cache.TryGetValue(key, out var hit) && hit != null) return hit;
            Material copy;
            if (EbmWater.TakeableShader(original.shader) || original.shader == ours)
            {
                copy = new Material(original) { name = original.name + " (EBM/Water, FAT)" };
                copy.shader = ours;   // value-preserving, as EbmWater.Swap: EBM/Water declares every vanilla / FiresWater property name
            }
            else
            {
                var template = EbmWater.ZoneTemplate();
                if (template == null) { s_waitingTemplate = true; return null; }   // "not yet": the first zone swap refreshes
                copy = new Material(template) { name = original.name + " (EBM/Water from sea, FAT)" };
                MapInland(original, copy);
            }
            if (copy.HasProperty(P_IsLod)) copy.SetFloat(P_IsLod, 0f);
            EbmWater.ForProvidedMaterial(copy);   // seam stencil + the owner's per-material writer, as EbmWater.PerVolume
            s_cache[key] = copy; s_keyOf[copy] = key;
            return copy;
        }

        // FAT's Custom/FiresInlandWater colours (FiresInlandWaterConfig.Apply: _ShallowColor, _DeepColor, _FoamColor, _ReflTint, _UwTint) onto EBM/Water's
        // (EbmWater.shader Properties: _ColorTop, _ColorBottom, _ColorBottomShallow, _SurfaceColor, _FoamColor, _UnderwaterTint). Our own mapping.
        private static void MapInland(Material from, Material to)
        {
            bool hasS = from.HasProperty("_ShallowColor"), hasD = from.HasProperty("_DeepColor");
            if (hasS) Set(to, "_ColorBottomShallow", from.GetColor("_ShallowColor"));
            if (hasD) Set(to, "_ColorBottom", from.GetColor("_DeepColor"));
            if (hasS && hasD) Set(to, "_ColorTop", Color.Lerp(from.GetColor("_ShallowColor"), from.GetColor("_DeepColor"), 0.5f));
            if (from.HasProperty("_FoamColor")) Set(to, "_FoamColor", from.GetColor("_FoamColor"));
            if (from.HasProperty("_ReflTint")) Set(to, "_SurfaceColor", from.GetColor("_ReflTint"));
            if (from.HasProperty("_UwTint")) Set(to, "_UnderwaterTint", from.GetColor("_UwTint"));
        }
        private static void Set(Material m, string p, Color c) { if (m.HasProperty(p)) m.SetColor(p, c); }

        private static void ApplyBoxed(Renderer r, object req)
        {
            if (r == null) return;
            float size = Convert.ToSingle(f_bodySize.GetValue(req));
            if (size <= 0.001f) size = ((Bounds)f_bounds.GetValue(req)).size.x;
            if (size <= 0.001f) return;
            if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(s_mpb);   // merge: FAT keeps its own values in the block
            s_mpb.SetFloat(P_VolumeSize, size);
            r.SetPropertyBlock(s_mpb);
        }

        private static void Release(Material m)
        {
            if (m == null) return;
            if (s_keyOf.TryGetValue(m, out long key)) { s_keyOf.Remove(m); s_cache.Remove(key); }
            else return;   // never ours: never destroyed
            UnityEngine.Object.Destroy(m);
        }
    }
}
