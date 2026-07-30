using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

// Attaches to a running .NET process (or opens a dump), finds the objects that
// actually hold the heap, and walks back from each one to the GC root that keeps
// it alive. A type histogram says WHAT is big; only the root path says WHY it is
// still there, which is the difference between a guess and a cause.

internal static class Program
{
    private const long BigObjectThreshold = 100L * 1024 * 1024;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: heapscan <pid|dumpfile> [--suspend]");
            return 2;
        }

        bool suspend = args.Contains("--suspend");
        DataTarget target;
        if (int.TryParse(args[0], out var pid))
        {
            Console.WriteLine($"Attaching to pid {pid} (suspend={suspend})...");
            target = DataTarget.AttachToProcess(pid, suspend);
        }
        else
        {
            Console.WriteLine($"Opening dump {args[0]}...");
            target = DataTarget.LoadDump(args[0]);
        }

        using (target)
        {
            var clrInfo = target.ClrVersions.FirstOrDefault();
            if (clrInfo is null)
            {
                Console.WriteLine("No CLR found in target.");
                return 3;
            }

            var runtime = clrInfo.CreateRuntime();
            var heap = runtime.Heap;
            if (!heap.CanWalkHeap)
            {
                Console.WriteLine("Heap is not walkable (target may be mid-GC). Retry.");
                return 4;
            }

            Console.WriteLine("Walking the heap...");
            var byType = new Dictionary<string, (long Size, long Count)>();
            var big = new List<(ulong Addr, string Type, ulong Size, ClrSegment Seg)>();
            long total = 0, objects = 0;

            foreach (var obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null) continue;
                var size = obj.Size;
                total += (long)size;
                objects++;

                var name = obj.Type.Name ?? "<unknown>";
                byType.TryGetValue(name, out var acc);
                byType[name] = (acc.Size + (long)size, acc.Count + 1);

                if ((long)size >= BigObjectThreshold)
                {
                    var seg = heap.GetSegmentByAddress(obj.Address);
                    big.Add((obj.Address, name, size, seg));
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Heap total : {total / 1024.0 / 1024 / 1024:F2} GB across {objects:N0} objects");
            Console.WriteLine();

            Console.WriteLine("=== TOP TYPES BY TOTAL SIZE ===");
            foreach (var kv in byType.OrderByDescending(k => k.Value.Size).Take(20))
                Console.WriteLine($"  {kv.Value.Size / 1024.0 / 1024,12:N1} MB  {kv.Value.Count,10:N0}  {Short(kv.Key)}");
            Console.WriteLine();

            Console.WriteLine($"=== OBJECTS >= {BigObjectThreshold / 1024 / 1024} MB : {big.Count} ===");
            foreach (var b in big.OrderByDescending(b => b.Size))
                Console.WriteLine($"  0x{b.Addr:x}  {b.Size / 1024.0 / 1024,10:N1} MB  {Short(b.Type)}  gen={GenOf(b.Seg, b.Addr)}");
            Console.WriteLine();

            // Live-versus-garbage census for named types. An object still present in
            // the heap is not necessarily still needed - dead objects linger until the
            // next gen2 collection, and gen2 is rare. Only reachability from a root
            // separates a real leak from ordinary collection lag.
            var censusTypes = args.Where(a => a.StartsWith("--census=", StringComparison.Ordinal))
                                  .Select(a => a.Substring("--census=".Length))
                                  .ToArray();
            var census = new Dictionary<string, List<(ulong Addr, ulong Size)>>();
            if (censusTypes.Length > 0)
            {
                foreach (var t in censusTypes) census[t] = new List<(ulong, ulong)>();
                foreach (var obj in heap.EnumerateObjects())
                {
                    var tn = obj.IsValid ? obj.Type?.Name : null;
                    if (tn is null) continue;
                    foreach (var t in censusTypes)
                        if (tn.Contains(t, StringComparison.OrdinalIgnoreCase))
                            census[t].Add((obj.Address, obj.Size));
                }
            }

            // Reverse-reference walk. Breadth-first from every GC root, remembering
            // how we arrived at each object, so the first time we touch a target we
            // already hold the shortest path that keeps it alive.
            Console.WriteLine("Building reference graph from GC roots...");
            var targets = new HashSet<ulong>(big.Select(b => b.Addr));
            var parent = new Dictionary<ulong, ulong>();
            var rootOf = new Dictionary<ulong, string>();
            var seen = new HashSet<ulong>();
            var queue = new Queue<ulong>();

            foreach (var root in heap.EnumerateRoots())
            {
                var ro = root.Object;
                if (ro.Address == 0 || !seen.Add(ro.Address)) continue;
                rootOf[ro.Address] = $"{root.RootKind} {Short(ro.Type?.Name ?? "?")}";
                queue.Enqueue(ro.Address);
            }
            Console.WriteLine($"  {queue.Count:N0} root objects");

            // Run the walk to exhaustion, not just until the targets are found: the
            // completed `seen` set IS the reachable set, which the census needs.
            var found = new Dictionary<ulong, List<ulong>>();
            while (queue.Count > 0)
            {
                var addr = queue.Dequeue();
                var obj = heap.GetObject(addr);
                if (!obj.IsValid || obj.Type is null) continue;

                foreach (var child in obj.EnumerateReferences(carefully: true, considerDependantHandles: true))
                {
                    if (child.Address == 0 || !seen.Add(child.Address)) continue;
                    parent[child.Address] = addr;
                    if (targets.Contains(child.Address) && !found.ContainsKey(child.Address))
                        found[child.Address] = PathTo(child.Address, parent);
                    queue.Enqueue(child.Address);
                }
            }

            if (census.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("=== LIVE VERSUS GARBAGE CENSUS ===");
                Console.WriteLine($"  {"type",-52} {"total",7} {"live",7} {"garbage",8} {"live MB",9}");
                foreach (var kv in census)
                {
                    var live = kv.Value.Where(o => seen.Contains(o.Addr)).ToList();
                    var liveMb = live.Sum(o => (double)o.Size) / 1024 / 1024;
                    Console.WriteLine($"  {Short(kv.Key),-52} {kv.Value.Count,7:N0} {live.Count,7:N0} " +
                                      $"{kv.Value.Count - live.Count,8:N0} {liveMb,9:N1}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== WHAT KEEPS EACH BIG OBJECT ALIVE ===");
            foreach (var b in big.OrderByDescending(b => b.Size))
            {
                Console.WriteLine();
                Console.WriteLine($"0x{b.Addr:x}  {b.Size / 1024.0 / 1024:N1} MB  {Short(b.Type)}");
                if (!found.TryGetValue(b.Addr, out var path))
                {
                    Console.WriteLine("  (no path found from a GC root - object may be unrooted and awaiting collection)");
                    continue;
                }
                path.Reverse();
                for (int i = 0; i < path.Count; i++)
                {
                    var o = heap.GetObject(path[i]);
                    var tn = Short(o.Type?.Name ?? "?");
                    var prefix = i == 0 && rootOf.TryGetValue(path[i], out var rk) ? $"[root:{rk}] " : "";
                    Console.WriteLine($"  {new string(' ', i * 2)}-> {prefix}{tn}  (0x{path[i]:x})");
                }
            }
        }

        return 0;
    }

    private static List<ulong> PathTo(ulong addr, Dictionary<ulong, ulong> parent)
    {
        var path = new List<ulong>();
        var cur = addr;
        var guard = 0;
        while (guard++ < 200)
        {
            path.Add(cur);
            if (!parent.TryGetValue(cur, out var p)) break;
            cur = p;
        }
        return path;
    }

    private static int GenOf(ClrSegment seg, ulong addr)
    {
        if (seg is null) return -1;
        try { return (int)seg.GetGeneration(addr); }
        catch { return -1; }
    }

    private static string Short(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return name.Length <= 110 ? name : name.Substring(0, 107) + "...";
    }
}
