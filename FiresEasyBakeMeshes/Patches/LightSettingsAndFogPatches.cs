using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace FiresEasyBakeMeshes.Patches
{
    // 1.2.172 [Batching] LightSettingsEvent. Vanilla LightFlicker subscribes every light to GraphicsSettingsManager.GraphicsSettingsChanged in
    // OnEnable and unsubscribes in OnDisable; each is a copy / scan of one multicast list, a spike when many lights unload together. Here
    // enabled lights sit in one HashSet behind one static hook. A light not in the set was subscribed by vanilla (key off, or enabled before a
    // toggle), so its OnDisable runs vanilla as it is: any toggle order stays correct.
    internal static class LightSettingsEvent
    {
        private static readonly HashSet<LightFlicker> s_lights = new HashSet<LightFlicker>();
        private static readonly List<LightFlicker> s_dead = new List<LightFlicker>();
        private static readonly Action<LightFlicker> s_apply = AccessTools.MethodDelegate<Action<LightFlicker>>(AccessTools.Method(typeof(LightFlicker), "ApplySettings"));
        private static bool s_hooked, s_announced;

        internal static void Add(LightFlicker light)
        {
            if (!s_hooked) { s_hooked = true; GraphicsSettingsManager.GraphicsSettingsChanged += OnChanged; }
            s_lights.Add(light);
            if (!s_announced) { s_announced = true; EasyBakeLog.Info("[LightSettingsEvent] lights share one graphics-settings hook instead of one subscription each"); }
        }

        internal static bool Remove(LightFlicker light) => s_lights.Remove(light);

        private static void OnChanged()
        {
            s_dead.Clear();
            foreach (var light in s_lights)
            {
                if (light == null) { s_dead.Add(light); continue; }
                s_apply(light);   // vanilla's ApplySettings returns by itself on a disabled light
            }
            foreach (var dead in s_dead) s_lights.Remove(dead);
            EasyBakeLog.Debug($"[LightSettingsEvent] graphics settings changed: {s_lights.Count} light(s) re-applied, {s_dead.Count} destroyed dropped");
        }
    }

    [HarmonyPatch(typeof(LightFlicker), "OnEnable")]
    public static class LightFlicker_OnEnable_SettingsPatch
    {
        private static readonly AccessTools.FieldRef<LightFlicker, Light> LightRef = AccessTools.FieldRefAccess<LightFlicker, Light>("m_light");
        private static readonly AccessTools.FieldRef<LightFlicker, float> TimeRef = AccessTools.FieldRefAccess<LightFlicker, float>("m_time");
        private static readonly Action<LightFlicker> Apply = AccessTools.MethodDelegate<Action<LightFlicker>>(AccessTools.Method(typeof(LightFlicker), "ApplySettings"));

        [HarmonyPrefix]
        public static bool Prefix(LightFlicker __instance)
        {
            if (!FiresEasyBakeMeshesPlugin.LightSettingsEvent.Value) return true;
            TimeRef(__instance) = 0f;
            if (LightRef(__instance) == null) return false;
            LightSettingsEvent.Add(__instance);
            Apply(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(LightFlicker), "OnDisable")]
    public static class LightFlicker_OnDisable_SettingsPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(LightFlicker __instance)
        {
            if (!LightSettingsEvent.Remove(__instance)) return true;
            LightFlicker.Instances.Remove(__instance);
            return false;
        }
    }

    // 1.2.172 [Batching] MinimapFogPartialUpload. Vanilla Minimap.Explore(Vector3, float) uploads the WHOLE fog texture on any newly explored
    // pixel. This runs vanilla's own loop, then uploads only the changed rectangle through a small staging texture. The CPU copy stays current
    // (Explore(int,int) still writes it), so any later full Apply is correct. No copy support or a mipmapped fog = vanilla's full Apply.
    [HarmonyPatch(typeof(Minimap), "Explore", new[] { typeof(Vector3), typeof(float) })]
    public static class Minimap_Explore_FogPatch
    {
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> FogRef = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_fogTexture");
        private static readonly Func<Minimap, int, int, bool> ExplorePixel = AccessTools.MethodDelegate<Func<Minimap, int, int, bool>>(AccessTools.Method(typeof(Minimap), "Explore", new[] { typeof(int), typeof(int) }));
        private delegate void WorldToPixelFn(Minimap map, Vector3 p, out int px, out int py);
        private static readonly WorldToPixelFn WorldToPixel = AccessTools.MethodDelegate<WorldToPixelFn>(AccessTools.Method(typeof(Minimap), "WorldToPixel", new[] { typeof(Vector3), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() }));
        private static Texture2D s_staging;
        private static bool s_announced;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]   // another mod's radius prefix has already run
        public static bool Prefix(Minimap __instance, Vector3 p, float radius, bool __runOriginal)
        {
            if (!__runOriginal || !FiresEasyBakeMeshesPlugin.MinimapFogPartialUpload.Value) return true;
            var fog = FogRef(__instance);
            if (fog == null || fog.mipmapCount > 1 || (SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) == 0) return true;

            int size = __instance.m_textureSize, num = (int)Mathf.Ceil(radius / __instance.m_pixelSize);
            WorldToPixel(__instance, p, out int px, out int py);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = py - num; y <= py + num; y++)
            {
                if (y < 0 || y >= size) continue;
                for (int x = px - num; x <= px + num; x++)
                {
                    if (x < 0 || x >= size) continue;
                    if (new Vector2(x - px, y - py).magnitude > num || !ExplorePixel(__instance, x, y)) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0) return false;

            int w = maxX - minX + 1, h = maxY - minY + 1;
            if (!Upload(fog, minX, minY, w, h)) { fog.Apply(); return false; }
            if (!s_announced) { s_announced = true; EasyBakeLog.Info("[MinimapFog] explored map pixels upload as a small rectangle instead of the whole fog texture"); }
            EasyBakeLog.Debug($"[MinimapFog] uploaded {w}x{h} of {size}x{size}");
            return false;
        }

        private static bool Upload(Texture2D fog, int x, int y, int w, int h)
        {
            try
            {
                if (s_staging == null || s_staging.width < w || s_staging.height < h || s_staging.graphicsFormat != fog.graphicsFormat)
                {
                    if (s_staging != null) UnityEngine.Object.Destroy(s_staging);
                    int side = Mathf.Max(64, Mathf.Max(w, h));
                    s_staging = new Texture2D(side, side, fog.graphicsFormat, TextureCreationFlags.None) { name = "_EBM fog staging" };
                }
                s_staging.SetPixels(0, 0, w, h, fog.GetPixels(x, y, w, h));
                s_staging.Apply(false);
                Graphics.CopyTexture(s_staging, 0, 0, 0, 0, w, h, fog, 0, 0, x, y);
                return true;
            }
            catch (Exception e)
            {
                EasyBakeLog.Debug("[MinimapFog] rectangle upload failed, whole texture this time: " + e.Message);
                return false;
            }
        }
    }
}
