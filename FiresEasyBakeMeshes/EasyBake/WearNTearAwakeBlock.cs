using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.257 [Optimize] ShareAwakePropertyBlock (default off until the A/B). WearNTear.Awake makes a new MaterialPropertyBlock for every piece and stores it in the
    // private m_propertyBlock, which nothing in WearNTear ever reads (vanilla WearNTear.cs: declared :165, assigned :335, no other use). Each one is a native object
    // ([ghost]'s READ_object_pooling_feasibility: Awake's renderer-side work 46.7 MB native in a log; this block's share of it is not measured yet).
    // One instruction changes: the `newobj MaterialPropertyBlock::.ctor` becomes a call that returns one shared block while the key is on, or a new one as before.
    // Every other line of Awake (RPCs, ZDO reads and writes, support, snow) is untouched and runs. Not found = warn once and leave Awake exactly as it is.
    [HarmonyPatch]
    internal static class WearNTearAwakeBlock
    {
        private static MaterialPropertyBlock s_shared;
        private static int s_shared_count, s_new_count;
        private static float s_nextLog;
        private static bool s_saidOn;

        private static bool On => FiresEasyBakeMeshesPlugin.ShareAwakePropertyBlock != null && FiresEasyBakeMeshesPlugin.ShareAwakePropertyBlock.Value;

        // Called in place of `new MaterialPropertyBlock()` inside WearNTear.Awake only.
        public static MaterialPropertyBlock Block()
        {
            if (!On) { s_new_count++; Report(); return new MaterialPropertyBlock(); }
            if (s_shared == null) s_shared = new MaterialPropertyBlock();
            if (!s_saidOn) { s_saidOn = true; EasyBakeLog.Info("[WearNTear] ShareAwakePropertyBlock on: pieces share the one property block Awake makes and never uses"); }
            s_shared_count++; Report();
            return s_shared;
        }

        private static void Report()
        {
            float now = Time.unscaledTime;
            if (now < s_nextLog) return;
            s_nextLog = now + 30f;
            if ((s_shared_count > 0 || s_new_count > 0) && EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"[WearNTear] Awake property blocks, last 30 s: {s_shared_count} not allocated (shared), {s_new_count} allocated (ShareAwakePropertyBlock {(On ? "on" : "off")})");
            s_shared_count = 0; s_new_count = 0;
        }

        [HarmonyPatch(typeof(WearNTear), "Awake"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var ctor = AccessTools.Constructor(typeof(MaterialPropertyBlock), System.Type.EmptyTypes);
            var block = AccessTools.Method(typeof(WearNTearAwakeBlock), nameof(Block));
            int replaced = 0;
            var original = new List<CodeInstruction>(instructions);
            var list = new List<CodeInstruction>(original);   // replacements go into this copy; original keeps the game's own instructions
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Newobj && list[i].operand is ConstructorInfo c && c == ctor)
                {
                    var call = new CodeInstruction(OpCodes.Call, block);
                    call.labels.AddRange(list[i].labels); call.blocks.AddRange(list[i].blocks);
                    list[i] = call;
                    replaced++;
                }
            }
            if (replaced != 1)
            {
                EasyBakeLog.Warn($"[WearNTear] ShareAwakePropertyBlock: expected one `new MaterialPropertyBlock()` in WearNTear.Awake, found {replaced}; Awake left exactly as the game has it");
                return original;
            }
            return list;
        }
    }
}
