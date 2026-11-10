using System.Collections;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.96 (the lead 10-13: the native crash at 18:56 came 3 s after a cds destroy burst). ebm_unload_test [rounds]: teleports the
    // local player 1500 m away and back, rounds times (default 10), so 20+ zones unload through the time-sliced destroy and the
    // instanced draw each time. Every leg is traced (fat_native_trace.log). A crash here reproduces it; a clean END is the pass.
    // Vanilla teleport only; the player ends where they started.
    internal static class UnloadStressTest
    {
        private const string Tag = "[ebm_unload_test]";
        private const float Away = 1500f, LegTimeout = 40f, Settle = 6f;
        private static bool s_running;

        internal static void Register()
        {
            new Terminal.ConsoleCommand("ebm_unload_test",
                "[rounds] - FiresEasyBakeMeshes: teleport 1500 m away and back (default 10 rounds) to unload 20+ zones each leg; traced",
                new Terminal.ConsoleEvent(args =>
                {
                    int rounds = 10;
                    if (args.Length > 1 && int.TryParse(args[1], out int r)) rounds = Mathf.Clamp(r, 1, 100);
                    var p = Player.m_localPlayer;
                    if (p == null || s_running) { args.Context?.AddString(Tag + " SKIP: no local player, or already running"); return; }
                    p.StartCoroutine(Run(p, rounds));
                }), isCheat: true);   // 1.2.111 (the lead's EBM audit 10-20): it teleports you, so devcommands only
        }

        private static IEnumerator Run(Player me, int rounds)
        {
            s_running = true;
            Vector3 home = me.transform.position;
            Quaternion rot = me.transform.rotation;
            float started = Time.realtimeSinceStartup;
            EasyBakeLog.Debug($"{Tag} BEGIN {FiresEasyBakeMeshesPlugin.PluginVersion}: {rounds} rounds of {Away:0} m away and back from ({home.x:0},{home.z:0})");
            int done = 0;
            for (int i = 0; i < rounds; i++)
            {
                Vector3 away = home + new Vector3(i % 2 == 0 ? Away : -Away, 0f, Away * 0.5f);
                float y = WorldGenerator.instance != null ? Mathf.Max(WorldGenerator.instance.GetHeight(away.x, away.z), 31f) + 2f : home.y;
                EbmTrace.Step("unload test leg away", i, $"({away.x:0},{away.z:0})");
                me.TeleportTo(new Vector3(away.x, y, away.z), rot, true);
                yield return WaitLeg(me);
                EbmTrace.Step("unload test leg home", i, $"({home.x:0},{home.z:0})");
                me.TeleportTo(home, rot, true);
                yield return WaitLeg(me);
                done++;
                EasyBakeLog.Debug($"{Tag} round {i + 1}/{rounds} done");
            }
            EasyBakeLog.Debug($"{Tag} END PASS: {done} rounds, no crash, {Time.realtimeSinceStartup - started:0} s");
            s_running = false;
        }

        private static IEnumerator WaitLeg(Player me)
        {
            float until = Time.time + LegTimeout;
            yield return new WaitForSeconds(0.5f);
            while (Time.time < until && me != null && me.IsTeleporting()) yield return null;
            yield return new WaitForSeconds(Settle);
        }
    }
}
