using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.189 (Fire 11-08, "make sure ebm is safe for all mods and doesnt cause these problems again", after SanEggs' report of a Jewelcrafting
    // Projectile.Awake NullReferenceException raised from EBM's prewarm): every place EBM calls into another mod goes through here.
    //
    // ForeignCall: a call into another mod's code (Vedr, FTS, FAT, FAP, Core) that throws is reported ONCE as a Warning naming the mod and the
    // member, and that member is switched off for the rest of the session, so EBM falls back to its own / vanilla behaviour instead of retrying
    // a broken call every frame. EBM's own state is never left half-updated: callers use the fallback value they pass in.
    internal static class ForeignCall
    {
        private static readonly HashSet<string> s_off = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>True when "mod.member" has thrown before and is switched off for the session.</summary>
        internal static bool Off(string mod, string member) => s_off.Count > 0 && s_off.Contains(mod + "." + member);

        /// <summary>Report a throw once (Warning naming the mod) and switch that member off.</summary>
        internal static void Failed(string mod, string member, Exception ex)
        {
            if (!s_off.Add(mod + "." + member)) return;
            var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            EasyBakeLog.Warn($"[EBM] {mod}'s {member} threw ({inner?.GetType().Name}: {inner?.Message}). EBM stops calling it this session and uses its own behaviour instead. This is a problem in {mod} or between {mod} and EBM, not in your world.");
        }

        internal static T Get<T>(string mod, string member, Func<T> call, T fallback)
        {
            if (call == null || Off(mod, member)) return fallback;
            try { return call(); }
            catch (Exception ex) { Failed(mod, member, ex); return fallback; }
        }

        internal static void Do(string mod, string member, Action call)
        {
            if (call == null || Off(mod, member)) return;
            try { call(); }
            catch (Exception ex) { Failed(mod, member, ex); }
        }
    }

    // 1.2.189: optional-mod lookups that never log. HarmonyX's AccessTools.TypeByName / AccessTools.Method write a warning on a miss, and an
    // optional mod (FAP, FAT, Core) being absent is normal for a public EBM install, so a miss must stay silent.
    internal static class OptionalType
    {
        internal static Type Find(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(fullName, false); } catch { t = null; }
                if (t != null) return t;
            }
            return null;
        }

        internal static System.Reflection.MethodInfo Method(Type type, string name, Type[] args)
        {
            if (type == null) return null;
            try { return type.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static, null, args, null); }
            catch { return null; }
        }
    }

    // SafePrefab: ZNetScene.GetPrefab runs other mods' code (FAP's lazy bundle realize patches it, and so can anything else). EBM looks prefabs up
    // by hash in ~30 places; a throw there must not escape into EBM's bake, skip or draw paths. Null on failure, one Warning per prefab.
    internal static class SafePrefab
    {
        private static readonly HashSet<int> s_said = new HashSet<int>();

        internal static GameObject Get(int hash)
        {
            var scene = ZNetScene.instance;
            if (scene == null) return null;
            try { return scene.GetPrefab(hash); }
            catch (Exception ex)
            {
                if (s_said.Add(hash))
                    EasyBakeLog.Warn($"[EBM] looking up prefab {hash} threw inside ZNetScene.GetPrefab ({ex.GetType().Name}: {ex.Message}); a mod that patches GetPrefab (lazy bundle loading) failed on it. EBM leaves that prefab to the game.");
                return null;
            }
        }

        internal static GameObject Get(string name)
        {
            var scene = ZNetScene.instance;
            if (scene == null || string.IsNullOrEmpty(name)) return null;
            try { return scene.GetPrefab(name); }
            catch (Exception ex)
            {
                if (s_said.Add(name.GetStableHashCode()))
                    EasyBakeLog.Warn($"[EBM] looking up prefab '{name}' threw inside ZNetScene.GetPrefab ({ex.GetType().Name}: {ex.Message}); EBM leaves that prefab to the game.");
                return null;
            }
        }
    }
}
