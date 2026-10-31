using System.Text.Json;

namespace UORegionEditor;

// Multi-user-safe undo: each operation snapshots ONLY the regions it touches (keyed by their
// stable Uid), so Ctrl+Z restores exactly those regions and never reverts a teammate's
// concurrent edits elsewhere in the list. Restores return what changed so the caller can
// push just those regions to the server.
public class UndoResult
{
    public Guid Uid;
    public string DefNameBefore;   // the region's defname before the restore (null = it did not exist)
    public RegionDef Now;          // the region object now in the project (null = removed by the restore)
}

public class UndoManager
{
    class Op
    {
        public string Desc;
        public DateTime At;
        public Dictionary<Guid, RegionDef> Before = new();   // uid -> clone, or null if it did not exist
        public List<string> ExtraSectionsBefore;             // null = untouched by this op
        public List<Guid> OrderBefore;                       // null = this op did not change the list order
    }

    readonly List<Op> undo = new();
    readonly List<Op> redo = new();
    string lastCoalesceKey;
    DateTime lastCoalesceAt;
    const int MaxDepth = 100;

    public event Action Changed;

    static RegionDef Clone(RegionDef r) =>
        r == null ? null : JsonSerializer.Deserialize<RegionDef>(JsonSerializer.Serialize(r));

    // Call BEFORE mutating. affected = the region objects the operation will touch (for a region
    // about to be created, pass the new object BEFORE adding it to the project - its "before"
    // state is recorded as absent). coalesceKey groups rapid same-kind edits into one step.
    public void Snapshot(string desc, Project p, IEnumerable<RegionDef> affected,
        bool includeExtraSections = false, string coalesceKey = null, bool includeOrder = false)
    {
        if (coalesceKey != null && coalesceKey == lastCoalesceKey &&
            (DateTime.Now - lastCoalesceAt).TotalSeconds < 2.0)
        {
            lastCoalesceAt = DateTime.Now;
            return;
        }
        lastCoalesceKey = coalesceKey;
        lastCoalesceAt = DateTime.Now;

        var op = new Op { Desc = desc, At = DateTime.Now };
        foreach (var r in affected)
        {
            if (r == null || op.Before.ContainsKey(r.Uid)) continue;
            op.Before[r.Uid] = p.Regions.Any(x => x.Uid == r.Uid) ? Clone(r) : null;
        }
        if (includeExtraSections) op.ExtraSectionsBefore = new List<string>(p.ExtraSections);
        if (includeOrder) op.OrderBefore = p.Regions.Select(r => r.Uid).ToList();
        undo.Add(op);
        if (undo.Count > MaxDepth) undo.RemoveAt(0);
        redo.Clear();
        Changed?.Invoke();
    }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string UndoDesc => undo.Count > 0 ? undo[^1].Desc : "";
    public string RedoDesc => redo.Count > 0 ? redo[^1].Desc : "";

    public IEnumerable<string> History =>
        Enumerable.Range(0, undo.Count).Select(i => $"{undo[undo.Count - 1 - i].At:HH:mm:ss}  {undo[undo.Count - 1 - i].Desc}");

    // true after an Undo/Redo whose operation restored the region list ORDER (drag-reorder):
    // the caller then pushes the full order to the server, not individual regions.
    public bool LastRestoredOrder { get; private set; }

    public List<UndoResult> Undo(Project p)
    {
        var r = Apply(p, undo, redo, out var ord);
        LastRestoredOrder = ord;
        if (r != null) { lastCoalesceKey = null; Changed?.Invoke(); }
        return r;
    }

    public List<UndoResult> Redo(Project p)
    {
        var r = Apply(p, redo, undo, out var ord);
        LastRestoredOrder = ord;
        if (r != null) { lastCoalesceKey = null; Changed?.Invoke(); }
        return r;
    }

    static List<UndoResult> Apply(Project p, List<Op> from, List<Op> to, out bool orderRestored)
    {
        orderRestored = false;
        if (from.Count == 0) return null;
        var op = from[^1];
        from.RemoveAt(from.Count - 1);

        // capture the inverse (current state of the same uids) for the opposite stack
        var inverse = new Op { Desc = op.Desc, At = DateTime.Now };
        foreach (var uid in op.Before.Keys)
        {
            var cur = p.Regions.FirstOrDefault(r => r.Uid == uid);
            inverse.Before[uid] = Clone(cur);
        }
        if (op.ExtraSectionsBefore != null) inverse.ExtraSectionsBefore = new List<string>(p.ExtraSections);
        if (op.OrderBefore != null) inverse.OrderBefore = p.Regions.Select(r => r.Uid).ToList();
        to.Add(inverse);

        var results = new List<UndoResult>();
        foreach (var (uid, before) in op.Before)
        {
            int i = p.Regions.FindIndex(r => r.Uid == uid);
            var res = new UndoResult { Uid = uid, DefNameBefore = i >= 0 ? p.Regions[i].DefName : null };
            if (before == null)
            {
                if (i >= 0) p.Regions.RemoveAt(i);
                res.Now = null;
            }
            else
            {
                var restored = Clone(before);
                if (i >= 0) p.Regions[i] = restored;
                else p.Regions.Add(restored);
                res.Now = restored;
            }
            results.Add(res);
        }
        if (op.ExtraSectionsBefore != null) p.ExtraSections = new List<string>(op.ExtraSectionsBefore);
        if (op.OrderBefore != null)
        {
            // restore the list order for the uids the snapshot knows; regions created since
            // (e.g. by a teammate) keep their relative order at the end. OrderBy is stable.
            var pos = new Dictionary<Guid, int>();
            for (int k = 0; k < op.OrderBefore.Count; k++) pos[op.OrderBefore[k]] = k;
            var sorted = p.Regions.OrderBy(r => pos.TryGetValue(r.Uid, out var k) ? k : int.MaxValue).ToList();
            p.Regions.Clear();
            p.Regions.AddRange(sorted);
            orderRestored = true;
        }
        return results;
    }

    public void ResetCoalesce() => lastCoalesceKey = null;

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        lastCoalesceKey = null;
        Changed?.Invoke();
    }
}
