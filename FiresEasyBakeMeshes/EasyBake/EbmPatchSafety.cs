using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// [ghost] EBM 1.2.212 ([lead] 11-08, [generator]'s 4-mod study item F2). EBM already patches one [HarmonyPatch] class at a time
    /// (Plugin.ApplyPatches). This adds what that loop lacked:
    ///  - TYPE LIST that cannot throw: Assembly.GetTypes() throws ReflectionTypeLoadException when ANY type references a missing assembly,
    ///    which would skip every EBM patch; AccessTools.GetTypesFromAssembly returns the loadable types instead.
    ///  - ROLL-BACK: a class that throws on a later target has already patched the earlier ones; those parts (our Harmony id, patch methods
    ///    declared in exactly that class) are removed again, so a feature never runs half-way.
    ///  - THE REASON: the innermost exception (Harmony wraps the real cause), and the failed class names in the summary line.
    /// EBM's own code; nothing shared with the other trinity mods.
    /// </summary>
    internal static class EbmPatchSafety
    {
        internal static Type[] PatchTypes(Assembly assembly)
        {
            try { return AccessTools.GetTypesFromAssembly(assembly); }
            catch (Exception e) { EasyBakeLog.Error("[EBM] could not list the patch classes: " + Innermost(e).Message); return new Type[0]; }
        }

        internal static Exception Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e;
        }

        /// <summary>After a class threw: removes what it had already applied; returns how many parts.</summary>
        internal static int RollBack(Harmony harmony, Type type)
        {
            int undone = 0;
            try
            {
                foreach (var original in new List<MethodBase>(harmony.GetPatchedMethods()))
                {
                    var info = Harmony.GetPatchInfo(original);
                    if (info == null) continue;
                    undone += Undo(harmony, original, info.Prefixes, type);
                    undone += Undo(harmony, original, info.Postfixes, type);
                    undone += Undo(harmony, original, info.Transpilers, type);
                    undone += Undo(harmony, original, info.Finalizers, type);
                }
            }
            catch (Exception e) { EasyBakeLog.Debug($"[EBM] roll-back of {type.Name} incomplete: {e.Message}"); }
            return undone;
        }

        private static int Undo(Harmony harmony, MethodBase original, IEnumerable<Patch> patches, Type type)
        {
            if (patches == null) return 0;
            var mine = new List<MethodInfo>();
            foreach (var p in patches)
                if (p != null && p.owner == harmony.Id && p.PatchMethod != null && p.PatchMethod.DeclaringType == type) mine.Add(p.PatchMethod);
            foreach (var m in mine) harmony.Unpatch(original, m);
            return mine.Count;
        }
    }
}
