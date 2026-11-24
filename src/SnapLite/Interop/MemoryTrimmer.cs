using System.Runtime;

namespace SnapLite.Interop;

/// <summary>Returns memory to the OS after a capture so the idle footprint stays small.</summary>
internal static class MemoryTrimmer
{
    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1);
    }
}
