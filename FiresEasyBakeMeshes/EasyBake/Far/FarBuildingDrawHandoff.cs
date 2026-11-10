using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    // [worldgen] 11-09, round 2 rows E17 "far copy leaves when the real one is proven" and E18 "far rebuild discipline" (both PARTLY). Our own methods.
    // Every key here defaults off and is read live. Look rule: no double draw and no gap frame that today's code does not already have.
    internal static partial class FarBuildingDraw
    {
        // ======== E17 ========
        // Today (1.2.256): a loaded zone's far copy goes when every far piece there has an instance, or is skipped (ZoneTracker.IsSkipped), or after 10 s.
        //  (1) [Optimize] FarHandoffProvenDrawn: a skipped piece counts only while its group is DRAWN this frame (ZoneTracker.SkippedAndDrawn = DrawInstances'
        //      own gate). IsSkipped alone is true while that zone's bake is not drawing yet, so the copy could go a frame or more early (a gap).
        //      Every hand-off is counted by its proof, every timeout by what was still missing.
        //  (2) [Optimize] FarKeepHandedOff: the handed-off copy is HIDDEN and kept (RetainSeconds, and at most a quarter of the vertex cap), not destroyed.
        //      When the zone unloads again (walking back out, the edge of the loaded area), it shows at once instead of after a fresh gather, merge and
        //      upload, which today is a gap of seconds where neither the real pieces nor the far copy draw. A copy whose zone changed meanwhile still shows
        //      at once and is rebuilt behind it (the normal swap), as any changed far target is today.
        internal static bool ProvenOn => FiresEasyBakeMeshesPlugin.FarHandoffProvenDrawn != null && FiresEasyBakeMeshesPlugin.FarHandoffProvenDrawn.Value;
        internal static bool KeepOn => FiresEasyBakeMeshesPlugin.FarKeepHandedOff != null && FiresEasyBakeMeshesPlugin.FarKeepHandedOff.Value;
        private const float RetainSeconds = 120f;

        private static int s_byInstances, s_bySkippedDrawn, s_timeoutMissing, s_timeoutSkippedNotDrawn, s_retained, s_reshown, s_retainExpired, s_retainEvicted;
        private static int s_blockWhy;   // the last AllInstanced: 0 none, 1 an instance missing, 2 a skipped piece whose group is not drawn yet
        private static bool s_anySkippedProof;   // the last AllInstanced proved at least one piece by its drawn group

        // AllInstanced's per-piece question
        private static bool PieceStands(ZDO zdo, ZNetScene scene)
        {
            if (scene.FindInstance(zdo) != null) return true;
            if (!CountsSkipped) { s_blockWhy = 1; return false; }
            if (ProvenOn)
            {
                if (ZoneTracker.SkippedAndDrawn(zdo)) { s_anySkippedProof = true; return true; }
                s_blockWhy = ZoneTracker.IsSkipped(zdo) ? 2 : 1;
                return false;
            }
            if (ZoneTracker.IsSkipped(zdo)) return true;   // 1.2.256: a skipped piece has no instance but its group draws it
            s_blockWhy = 1;
            return false;
        }

        private static void NoteHandoff(bool proven, Target t)
        {
            if (proven) { if (s_anySkippedProof) s_bySkippedDrawn++; else s_byInstances++; }
            else if (s_blockWhy == 2) s_timeoutSkippedNotDrawn++; else s_timeoutMissing++;
        }

        // the copy hidden and kept; false = destroy as before (key off, or no room)
        private static bool Retain(Target t)
        {
            if (!KeepOn || t.Chunks.Count == 0) return false;
            int kept = 0; foreach (var o in s_targets.Values) if (o.Retained) kept += o.Verts;
            if (kept + t.Verts > FarBuildings.MaxVertices / 4) { s_retainEvicted++; return false; }
            SetVisible(t, false);
            t.Retained = true; t.RetainedAt = Time.unscaledTime; t.HoldUntil = -1f;
            s_retained++;
            return true;
        }

        // from HandOff for a retained target: true = handled (skip the rest of the loop)
        private static bool TickRetained(Target t, bool loaded, ref List<long> expire)
        {
            if (!t.Retained) return false;
            if (!loaded) { t.Retained = false; SetVisible(t, true); s_reshown++; s_planDirty = true; return true; }   // walked back out: shown at once, no rebuild wait
            if (Time.unscaledTime - t.RetainedAt > RetainSeconds || !KeepOn) (expire ?? (expire = new List<long>())).Add(t.Key);
            return true;
        }

        private static void Expire(Target t)
        {
            DestroyChunks(t); t.Drawn = false; t.Dirty = true;
            s_retainExpired++; s_planDirty = true;
        }

        // ======== E18 ========
        // Today: atomic swap, sliced upload under the ms budget, nearest first, changed-only invalidation. Missing, each on its own key:
        //  (a) census, always on: what retiring chunks costs (DestroyChunks / DestroyBuilt ran in one go), so (b) is judged on a number;
        //  (b) [Optimize] FarRetireSliced: retired chunks are hidden at once (SetActive false: on screen the same as Destroy, which also lands before render)
        //      and destroyed a few a frame inside the far budget (at least one a frame);
        //  (c) [Optimize] FarUploadByteCap: the sliced upload also stops at UploadByteCap bytes a frame (at least one chunk). The GPU copy of an uploaded
        //      mesh happens on the render thread, which the main-thread ms budget never sees;
        //  (d) [Optimize] FarBuildViewFirst: targets in front of the camera are gathered first (a target behind counts as twice as far). Order only: the
        //      vertex-cap eviction keeps its plain distance order, and every target is still built.
        internal static bool RetireSlicedOn => FiresEasyBakeMeshesPlugin.FarRetireSliced != null && FiresEasyBakeMeshesPlugin.FarRetireSliced.Value;
        internal static bool ByteCapOn => FiresEasyBakeMeshesPlugin.FarUploadByteCap != null && FiresEasyBakeMeshesPlugin.FarUploadByteCap.Value;
        internal static bool ViewFirstOn => FiresEasyBakeMeshesPlugin.FarBuildViewFirst != null && FiresEasyBakeMeshesPlugin.FarBuildViewFirst.Value;
        private const long UploadByteCap = 8L * 1024 * 1024;

        private static readonly List<GameObject> s_retire = new List<GameObject>();
        private static long s_retireTicks, s_retireWorst, s_retireFrameTicks; private static int s_retireFrame = -1, s_retireChunks, s_retireDestroyed;
        private static int s_byteCapped, s_viewReordered; private static long s_uploadBytesFrame; private static int s_uploadFrame = -1;

        // DestroyChunks / DestroyBuilt's per-chunk step
        private static void RetireChunk(GameObject go)
        {
            if (go == null) return;
            if (PoolChunkObject(go)) { s_retireChunks++; return; }   // [Optimize] FarMergePooled: hidden, mesh released, object kept for the next chunk
            if (RetireSlicedOn) { if (go.activeSelf) go.SetActive(false); s_retire.Add(go); }
            else DestroyNow(go);
            s_retireChunks++;
        }

        private static void DestroyNow(GameObject go)
        {
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) Object.Destroy(mf.sharedMesh);
            Object.Destroy(go);
        }

        private static void NoteRetireTicks(long ticks)
        {
            int f = Time.frameCount;
            if (f != s_retireFrame) { s_retireFrame = f; s_retireFrameTicks = 0L; }
            s_retireFrameTicks += ticks; s_retireTicks += ticks;
            if (s_retireFrameTicks > s_retireWorst) s_retireWorst = s_retireFrameTicks;
        }

        // from Update after uploads: the queued chunks, under the deadline, at least one a frame
        private static void RetireStep(long deadline)
        {
            if (s_retire.Count == 0) return;
            long t0 = Stopwatch.GetTimestamp();
            int n = 0;
            while (s_retire.Count > 0 && (n == 0 || Stopwatch.GetTimestamp() < deadline))
            {
                var go = s_retire[s_retire.Count - 1]; s_retire.RemoveAt(s_retire.Count - 1);
                if (go != null) DestroyNow(go);
                n++;
            }
            s_retireDestroyed += n;
            NoteRetireTicks(Stopwatch.GetTimestamp() - t0);
        }

        private static void RetireAllNow() { foreach (var go in s_retire) if (go != null) DestroyNow(go); s_retire.Clear(); }

        // StepUploads: may another chunk go up this frame
        private static bool UnderByteCap()
        {
            if (!ByteCapOn) return true;
            int f = Time.frameCount;
            if (f != s_uploadFrame) { s_uploadFrame = f; s_uploadBytesFrame = 0L; }
            if (s_uploadBytesFrame < UploadByteCap) return true;
            s_byteCapped++;
            return false;
        }

        private static void NoteUploadBytes(long bytes)
        {
            int f = Time.frameCount;
            if (f != s_uploadFrame) { s_uploadFrame = f; s_uploadBytesFrame = 0L; }
            s_uploadBytesFrame += bytes;
        }

        // Plan, after the distance-ordered prep queue is built
        private static readonly List<Target> s_viewTmp = new List<Target>();
        private static void ViewFirst(Vector3 center)
        {
            if (!ViewFirstOn || s_prepQueue.Count < 2) return;
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            Vector3 f = cam.transform.forward; var fwd = new Vector2(f.x, f.z);
            if (fwd.sqrMagnitude < 1e-4f) return;
            fwd.Normalize();
            float cos = Mathf.Cos(Mathf.Min(89f, cam.fieldOfView * cam.aspect * 0.5f + 20f) * Mathf.Deg2Rad);
            Vector3 cp = cam.transform.position;
            s_viewTmp.Clear(); s_viewTmp.AddRange(s_prepQueue);
            System.Comparison<Target> score = (a, b) => Score(a, cp, fwd, cos).CompareTo(Score(b, cp, fwd, cos));
            // stable: an insertion sort keeps equal scores in their distance order (the queue is short: dirty targets only)
            for (int i = 1; i < s_viewTmp.Count; i++)
            {
                var x = s_viewTmp[i]; int j = i - 1;
                while (j >= 0 && score(s_viewTmp[j], x) > 0) { s_viewTmp[j + 1] = s_viewTmp[j]; j--; }
                s_viewTmp[j + 1] = x;
            }
            for (int i = 0; i < s_viewTmp.Count; i++) if (!ReferenceEquals(s_viewTmp[i], s_prepQueue[i])) { s_viewReordered++; break; }
            s_prepQueue.Clear(); s_prepQueue.AddRange(s_viewTmp);
        }

        private static float Score(Target t, Vector3 cp, Vector2 fwd, float cos)
        {
            var d = new Vector2(t.Origin.x - cp.x, t.Origin.z - cp.z);
            float len = d.magnitude;
            if (len < 128f) return len;
            bool inFront = Vector2.Dot(d / len, fwd) >= cos;
            return inFront ? len : len * 2f;
        }

        // ======== census (Debug, 30 s) ========
        private static float s_nextDisciplineLog;
        private static void LogHandoffAndDiscipline()
        {
            if (Time.unscaledTime < s_nextDisciplineLog) return;
            s_nextDisciplineLog = Time.unscaledTime + 30f;
            if (EasyBakeLog.DebugOn())
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                int keptNow = 0; foreach (var t in s_targets.Values) if (t.Retained) keptNow++;
                if (s_byInstances + s_bySkippedDrawn + s_timeoutMissing + s_timeoutSkippedNotDrawn + s_retained + s_reshown + s_retainExpired > 0)
                    EasyBakeLog.Debug($"{FarBuildings.Log} hand-off 30 s (proven-drawn {(ProvenOn ? "on" : "off")}, keep {(KeepOn ? "on" : "off")}): proven by instances {s_byInstances}, by skipped pieces whose group draws {s_bySkippedDrawn}; " +
                        $"{HoldSeconds:0} s timeouts: an instance missing {s_timeoutMissing}, a skipped piece's group not drawn yet {s_timeoutSkippedNotDrawn}; copies kept hidden {s_retained} (no room {s_retainEvicted}), shown again at once on unload {s_reshown}, expired {s_retainExpired}, kept now {keptNow}");
                if (s_retireChunks + s_byteCapped + s_viewReordered > 0)
                    EasyBakeLog.Debug($"{FarBuildings.Log} rebuild discipline 30 s: retired {s_retireChunks} chunk(s) costing {s_retireTicks * ms:0.00} ms on the main thread (worst frame {s_retireWorst * ms:0.00} ms; sliced {(RetireSlicedOn ? "on" : "off")}, {s_retireDestroyed} destroyed from the queue, {s_retire.Count} waiting); " +
                        $"upload byte cap {(ByteCapOn ? "on" : "off")} ({UploadByteCap >> 20} MB a frame): {s_byteCapped} stop(s); view-first {(ViewFirstOn ? "on" : "off")}: {s_viewReordered} plan(s) reordered");
            }
            s_byInstances = s_bySkippedDrawn = s_timeoutMissing = s_timeoutSkippedNotDrawn = s_retained = s_reshown = s_retainExpired = s_retainEvicted = 0;
            s_retireChunks = s_retireDestroyed = s_byteCapped = s_viewReordered = 0; s_retireTicks = s_retireWorst = 0L;
        }
    }
}
