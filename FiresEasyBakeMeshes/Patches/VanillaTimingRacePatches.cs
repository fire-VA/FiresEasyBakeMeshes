using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    // ShieldGenerator.UpdateShield (private, called every 0.22s via InvokeRepeating
    // scheduled in ShieldGenerator.Start) reaches into a pile of inspector-assigned
    // references — m_energyParticles, m_energyParticlesFlare, m_coloredLights,
    // m_meshRenderers (derived from m_enabledObject children in Start), m_propertyBlock,
    // m_shieldDome, m_nview — without null-guarding any of them. Vanilla assumes the
    // prefab editor populated every field. Modded or otherwise non-standard prefab
    // variants that leave one of these fields null hit a NullReferenceException on
    // the very first tick of UpdateShield, and InvokeRepeating happily keeps firing
    // every 0.22s, NRE'ing every single time. With Fires' zone-load pipeline
    // (FiresGhettoNetworkMod) streaming many ZDOs during the loading screen, a
    // single bad ShieldGenerator instance produces hundreds of NRE lines before the
    // player even spawns.
    //
    // First-pass theory was a vanilla race on ShieldDomeImageEffect.s_staticGradient.
    // Empirically wrong — the deployed prefix with that guard never logged its
    // suppression line, meaning the gradient WAS set when UpdateShield ran. The NRE
    // is from a different null reference; without seeing the prefab data we can't
    // tell which one a priori.
    //
    // The finalizer here is the smallest correct fix:
    //   1. Vanilla UpdateShield runs as usual.
    //   2. If it throws, the finalizer catches the exception.
    //   3. For each ShieldGenerator instance that throws, log ONCE which fields are
    //      null (so we know what data is bad), CancelInvoke("UpdateShield") so the
    //      InvokeRepeating stops re-firing on this instance, and swallow the
    //      exception so it doesn't reach the log.
    //   4. Healthy instances are unaffected — finalizer runs with __exception=null
    //      and returns null immediately.
    //
    // Why a finalizer and not a prefix-with-null-checks: prefix-side null-checking
    // would require us to enumerate every field vanilla touches and re-check the
    // exact predicate vanilla uses. A finalizer lets vanilla be the source of truth
    // for what's required; we only react when its assumption fails.
    //
    // CancelInvoke is critical — without it, the NRE fires every 0.22s forever.
    [HarmonyPatch(typeof(ShieldGenerator), "UpdateShield")]
    public static class ShieldGenerator_UpdateShield_NREGuard
    {
        private static readonly HashSet<int> _silenced = new HashSet<int>();

        // Cached FieldInfos for the public/private inspector fields most likely to
        // be the offender. Used only when we've already caught an exception, so the
        // reflection cost is bounded by "once per malformed prefab".
        private static readonly FieldInfo f_nview          = AccessTools.Field(typeof(ShieldGenerator), "m_nview");
        private static readonly FieldInfo f_enabledObject  = AccessTools.Field(typeof(ShieldGenerator), "m_enabledObject");
        private static readonly FieldInfo f_disabledObject = AccessTools.Field(typeof(ShieldGenerator), "m_disabledObject");
        private static readonly FieldInfo f_shieldDome     = AccessTools.Field(typeof(ShieldGenerator), "m_shieldDome");
        private static readonly FieldInfo f_energyParticles      = AccessTools.Field(typeof(ShieldGenerator), "m_energyParticles");
        private static readonly FieldInfo f_energyParticlesFlare = AccessTools.Field(typeof(ShieldGenerator), "m_energyParticlesFlare");
        private static readonly FieldInfo f_coloredLights        = AccessTools.Field(typeof(ShieldGenerator), "m_coloredLights");
        private static readonly FieldInfo f_meshRenderers        = AccessTools.Field(typeof(ShieldGenerator), "m_meshRenderers");
        private static readonly FieldInfo f_propertyBlock        = AccessTools.Field(typeof(ShieldGenerator), "m_propertyBlock");

        [HarmonyFinalizer]
        public static Exception Finalizer(ShieldGenerator __instance, Exception __exception)
        {
            if (__exception == null) return null;            // healthy tick — leave alone
            if (__instance == null) return __exception;       // can't act on a destroyed instance — let it bubble

            int id = __instance.GetInstanceID();
            if (_silenced.Contains(id)) return null;          // already handled this instance, just swallow
            _silenced.Add(id);

            // Best-effort: stop the recurring tick so we don't NRE again every 0.22s.
            try { __instance.CancelInvoke("UpdateShield"); }
            catch (Exception cancelEx)
            {
                EasyBakeLog.Warn($"[NREGuard] CancelInvoke failed on '{InstanceLabel(__instance)}': {cancelEx.Message}");
            }

            EasyBakeLog.Warn(BuildDiagnostic(__instance, __exception));
            return null; // swallow — vanilla NRE no longer reaches BepInEx log
        }

        private static string BuildDiagnostic(ShieldGenerator inst, Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append("[NREGuard] ShieldGenerator.UpdateShield NRE on '").Append(InstanceLabel(inst)).Append("'. ");
            sb.Append("Cancelled future ticks for this instance. ");
            sb.Append("Field state: ");

            AppendFieldState<object>(sb, "m_nview",          f_nview,          inst);
            AppendFieldState<object>(sb, "m_enabledObject",  f_enabledObject,  inst);
            AppendFieldState<object>(sb, "m_disabledObject", f_disabledObject, inst);
            AppendFieldState<object>(sb, "m_shieldDome",     f_shieldDome,     inst);
            AppendArrayState(sb, "m_energyParticles",        f_energyParticles, inst);
            AppendFieldState<object>(sb, "m_energyParticlesFlare", f_energyParticlesFlare, inst);
            AppendArrayState(sb, "m_coloredLights",          f_coloredLights, inst);
            AppendArrayState(sb, "m_meshRenderers",          f_meshRenderers, inst);
            AppendFieldState<object>(sb, "m_propertyBlock",  f_propertyBlock, inst);

            sb.Append("Original: ").Append(ex.GetType().Name).Append(" — ").Append(ex.Message);
            return sb.ToString();
        }

        private static void AppendFieldState<T>(StringBuilder sb, string label, FieldInfo field, ShieldGenerator inst)
        {
            sb.Append(label).Append('=');
            if (field == null) { sb.Append("?fieldMissing? "); return; }
            try
            {
                var val = field.GetValue(inst);
                if (val == null)                                  sb.Append("null");
                else if (val is UnityEngine.Object uo && !uo)     sb.Append("Unity-null");
                else                                              sb.Append("ok");
            }
            catch (Exception e) { sb.Append("?readFailed:").Append(e.GetType().Name).Append("?"); }
            sb.Append(' ');
        }

        private static void AppendArrayState(StringBuilder sb, string label, FieldInfo field, ShieldGenerator inst)
        {
            sb.Append(label).Append('=');
            if (field == null) { sb.Append("?fieldMissing? "); return; }
            try
            {
                var val = field.GetValue(inst);
                if (val == null)
                    sb.Append("null");
                else if (val is Array arr)
                {
                    sb.Append("len=").Append(arr.Length);
                    int nulls = 0;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var entry = arr.GetValue(i);
                        if (entry == null) { nulls++; continue; }
                        if (entry is UnityEngine.Object uo && !uo) nulls++;
                    }
                    if (nulls > 0) sb.Append(",nulls=").Append(nulls);
                }
                else sb.Append("?notArray?");
            }
            catch (Exception e) { sb.Append("?readFailed:").Append(e.GetType().Name).Append("?"); }
            sb.Append(' ');
        }

        private static string InstanceLabel(ShieldGenerator inst)
        {
            try { return inst.gameObject != null ? inst.gameObject.name : "<destroyed>"; }
            catch { return "<unreadable>"; }
        }
    }
}
