using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using AvatarChangeLog;

static class Benchmarks
{
    static void Main()
    {
        const int count = 20000;
        var before = new Snapshot { avatarId = "benchmark" };
        for (int i = 0; i < count; i++)
            before.entries.Add(new HistoryEntry { key = "key" + i, category = i % 200 == 0 ? "参照アセット" : "Transform",
                path = i % 200 == 0 ? "Assets/Item" + i + ".asset" : "./Body[0]", item = "Transform / value" + i, value = "1" });
        var after = new Snapshot { avatarId = before.avatarId,
            entries = before.entries.Select(e => new HistoryEntry { key = e.key, category = e.category, path = e.path, item = e.item, value = e.value }).ToList() };
        after.entries[1].value = "2";
        Measure("unchanged_diff", () => HistoryDiff.Compare(before, before).Count, 0);
        Measure("one_change_diff", () => HistoryDiff.Compare(before, after).Count, 1);
        Measure("one_change_log", () => HistoryText.Build(before, after).Count, 1);
    }

    static void Measure(string name, Func<int> action, int expected)
    {
        const int runs = 100;
        for (int i = 0; i < 10; i++) if (action() != expected) throw new Exception("Wrong benchmark result");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < runs; i++) if (action() != expected) throw new Exception("Wrong benchmark result");
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine(name + "," + (watch.Elapsed.TotalMilliseconds / runs).ToString("F4", CultureInfo.InvariantCulture) + "," + allocated / runs);
    }
}
