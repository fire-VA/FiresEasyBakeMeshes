using System.Collections.Generic;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.213 ([visual], [saddles]' study #5: "s_high never shrinks", and every GPU cull dispatch covers s_high threads): the free instance slots
    // as a MIN-heap. AddInstance reuses the LOWEST free slot, so churn (coverage handing objects back and taking new ones) fills holes from the
    // bottom and the top slots empty out; RemoveInstance then lowers s_high past the free top (IndirectCompactSlots). Entries at or past a
    // lowered s_high stay in the heap and are dropped when they surface (PopBelow). No allocation per call once grown.
    internal sealed class FreeSlots
    {
        private readonly List<int> _heap = new List<int>(1024);
        internal int Count => _heap.Count;
        internal void Clear() => _heap.Clear();

        internal void Push(int slot)
        {
            _heap.Add(slot);
            int i = _heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_heap[p] <= _heap[i]) break;
                (_heap[p], _heap[i]) = (_heap[i], _heap[p]);
                i = p;
            }
        }

        /// <summary>The lowest free slot below limit, or -1 when none is left (stale entries at or past limit are dropped on the way).</summary>
        /// A popped slot that is live again (inUse[slot] != 0: s_high grew back over it and handed it out) is a stale duplicate and dropped too.
        internal int PopBelow(int limit, uint[] inUse)
        {
            while (_heap.Count > 0)
            {
                int top = Pop();
                if (top < limit && (top >= inUse.Length || inUse[top] == 0)) return top;
            }
            return -1;
        }

        private int Pop()
        {
            int top = _heap[0], last = _heap.Count - 1;
            _heap[0] = _heap[last];
            _heap.RemoveAt(last);
            int i = 0, n = _heap.Count;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < n && _heap[l] < _heap[m]) m = l;
                if (r < n && _heap[r] < _heap[m]) m = r;
                if (m == i) break;
                (_heap[m], _heap[i]) = (_heap[i], _heap[m]);
                i = m;
            }
            return top;
        }
    }

    internal static partial class EbmIndirect
    {
        private static bool GpuGrowOn => FiresEasyBakeMeshesPlugin.IndirectGpuGrow == null || FiresEasyBakeMeshesPlugin.IndirectGpuGrow.Value;
        private static bool CompactOn => FiresEasyBakeMeshesPlugin.IndirectCompactSlots == null || FiresEasyBakeMeshesPlugin.IndirectCompactSlots.Value;
        internal static int s_gpuGrows;   // Debug: instance buffer growths done on the GPU
    }
}
