using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CHC.Core.Collection;

/// <summary>What one program, all its processes together, used since the previous reading.</summary>
/// <param name="MainPid">Its process using the most memory, to look up the program's name.</param>
/// <param name="CpuPercent">Share of the whole processor, like Task Manager.</param>
/// <param name="MemoryBytes">Memory only this program uses (private working set).</param>
/// <param name="ReadBytes">Data read, from disk mostly; Windows counts network and device traffic here too.</param>
/// <param name="Adapter">The graphics adapter it uses most, when it uses one.</param>
public sealed record ProgramReading(string Name, int MainPid, bool InFront, double CpuPercent, long MemoryBytes, long ReadBytes, long WriteBytes,
    double Gpu3D, double GpuVideo, double GpuCompute, long VramBytes, string? Adapter)
{
    public double GpuPercent => Math.Max(Gpu3D, Math.Max(GpuVideo, GpuCompute));
}

/// <summary>
/// Reads what every program uses, all at once and cheaply: Windows hands over the whole process list in one call, so
/// no process is opened or touched, and it works without administrator rights. 64-bit only, like CHC's installer.
/// </summary>
public sealed partial class ProcessSampler : IDisposable
{
    private const int SystemProcessInformation = 5;
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);

    private readonly record struct Raw(int Pid, string Name, long Created, long Cpu, long Memory, long Read, long Write);

    private readonly Dictionary<int, Raw> _last = [];
    private readonly Stopwatch _clock = new();
    private readonly GpuProcessCounters _gpu = new();
    private long _lastFileTime;
    private int _bufferSize = 512 * 1024;

    /// <summary>What each program used since the previous call, and how long that was; nothing on the first call.</summary>
    public (double Seconds, IReadOnlyList<ProgramReading> Programs) Read(int frontPid)
    {
        var now = DateTime.UtcNow.ToFileTimeUtc();
        var seconds = _clock.Elapsed.TotalSeconds;
        var first = !_clock.IsRunning;
        _clock.Restart();
        var raw = Enumerate();
        var gpu = _gpu.Read();
        var lastTime = _lastFileTime;
        _lastFileTime = now;

        var programs = new List<ProgramReading>();
        if (!first && seconds > 0)
        {
            var cores = Math.Max(1, Environment.ProcessorCount);
            foreach (var group in raw.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                long cpu = 0, read = 0, write = 0, vram = 0;
                double gpu3D = 0, video = 0, compute = 0;
                string? adapter = null;
                var bestGpu = 0.0;
                foreach (var p in group)
                {
                    // A process seen before, or one born since then: all of its counters fall inside this period.
                    if (_last.TryGetValue(p.Pid, out var before) && before.Created == p.Created)
                    {
                        cpu += Math.Max(0, p.Cpu - before.Cpu);
                        read += Math.Max(0, p.Read - before.Read);
                        write += Math.Max(0, p.Write - before.Write);
                    }
                    else if (p.Created >= lastTime)
                    {
                        cpu += p.Cpu;
                        read += p.Read;
                        write += p.Write;
                    }
                    if (gpu.TryGetValue(p.Pid, out var g))
                    {
                        gpu3D += g.Gpu3D;
                        video += g.Video;
                        compute += g.Compute;
                        vram += g.Vram;
                        if (g.Load > bestGpu)
                        {
                            bestGpu = g.Load;
                            adapter = g.Adapter;
                        }
                    }
                }
                var main = group.MaxBy(p => p.Memory);
                programs.Add(new ProgramReading(group.Key, main.Pid, group.Any(p => p.Pid == frontPid),
                    cpu / 10_000_000.0 / seconds / cores * 100, group.Sum(p => p.Memory), read, write,
                    Math.Min(100, gpu3D), Math.Min(100, video), Math.Min(100, compute), vram, adapter));
            }
        }

        _last.Clear();
        foreach (var p in raw) _last[p.Pid] = p;
        return (first ? 0 : seconds, programs);
    }

    /// <summary>Walks the SYSTEM_PROCESS_INFORMATION list; the offsets are those of 64-bit Windows.</summary>
    private unsafe List<Raw> Enumerate()
    {
        var list = new List<Raw>(400);
        if (!Environment.Is64BitProcess) return list;
        var buffer = Marshal.AllocHGlobal(_bufferSize);
        try
        {
            int status;
            while ((status = NtQuerySystemInformation(SystemProcessInformation, buffer, _bufferSize, out var needed)) == InfoLengthMismatch)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                _bufferSize = Math.Max(_bufferSize * 2, needed + 64 * 1024);
                buffer = Marshal.AllocHGlobal(_bufferSize);
            }
            if (status != 0) return list;

            var entry = (byte*)buffer;
            while (true)
            {
                var next = *(uint*)entry;
                var pid = (int)*(long*)(entry + 80);
                var nameLength = *(ushort*)(entry + 56);
                var namePointer = *(char**)(entry + 64);
                // Process 0 is the idle time, not a program.
                if (pid != 0 && namePointer != null && nameLength > 0)
                    list.Add(new Raw(
                        pid,
                        new string(namePointer, 0, nameLength / 2),
                        *(long*)(entry + 32),
                        *(long*)(entry + 40) + *(long*)(entry + 48),
                        *(long*)(entry + 8),
                        *(long*)(entry + 232),
                        *(long*)(entry + 240)));
                if (next == 0) break;
                entry += next;
            }
            return list;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

    public void Dispose() => _gpu.Dispose();
}

/// <summary>
/// Per-process use of the graphics adapters, from the counters Task Manager's GPU column shows: each engine of each
/// adapter for each process, so it also tells which adapter a program runs on (a game on the wrong one on a laptop).
/// </summary>
internal sealed partial class GpuProcessCounters : IDisposable
{
    internal readonly record struct Usage(double Gpu3D, double Video, double Compute, long Vram, string? Adapter)
    {
        public double Load => Math.Max(Gpu3D, Math.Max(Video, Compute));
    }

    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap = 0x00008000;
    private const uint MoreData = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    private IntPtr _query;
    private IntPtr _engines;
    private IntPtr _memory;
    private bool _unavailable;
    private Dictionary<ulong, string>? _adapters;

    public Dictionary<int, Usage> Read()
    {
        var result = new Dictionary<int, Usage>();
        if (!Open() || PdhCollectQueryData(_query) != 0) return result;

        // Engines: for each process and adapter, the time of each kind of work; several engines of one kind add up.
        var work = new Dictionary<(int Pid, ulong Adapter), (double Gpu3D, double Video, double Compute)>();
        foreach (var (name, value) in Items(_engines))
        {
            if (!TryParse(name, out var pid, out var adapter, out var parts) || parts.Length < 11 || value <= 0) continue;
            var kind = string.Join('_', parts.Skip(10));
            work.TryGetValue((pid, adapter), out var w);
            if (kind.Equals("3D", StringComparison.OrdinalIgnoreCase)) w.Gpu3D += value;
            else if (kind.StartsWith("Video", StringComparison.OrdinalIgnoreCase)) w.Video += value;
            else if (kind.StartsWith("Compute", StringComparison.OrdinalIgnoreCase) || kind.StartsWith("Cuda", StringComparison.OrdinalIgnoreCase)) w.Compute += value;
            else continue;
            work[(pid, adapter)] = w;
        }

        var memory = new Dictionary<int, long>();
        foreach (var (name, value) in Items(_memory))
            if (TryParse(name, out var pid, out _, out _))
                memory[pid] = memory.GetValueOrDefault(pid) + (long)value;

        // A program on two adapters counts with the one it uses most.
        foreach (var (key, w) in work)
        {
            var load = Math.Max(w.Gpu3D, Math.Max(w.Video, w.Compute));
            if (result.TryGetValue(key.Pid, out var known) && known.Load >= load) continue;
            result[key.Pid] = new Usage(w.Gpu3D, w.Video, w.Compute, memory.GetValueOrDefault(key.Pid), AdapterName(key.Adapter));
        }
        foreach (var (pid, bytes) in memory)
            if (!result.ContainsKey(pid))
                result[pid] = new Usage(0, 0, 0, bytes, null);
        return result;
    }

    /// <summary>"pid_1234_luid_0x00000000_0x0000C8D7_phys_0_eng_0_engtype_3D": the process and the adapter.</summary>
    private static bool TryParse(string name, out int pid, out ulong adapter, out string[] parts)
    {
        pid = 0;
        adapter = 0;
        parts = name.Split('_');
        if (parts.Length < 5 || parts[0] != "pid" || parts[2] != "luid" || !int.TryParse(parts[1], out pid)) return false;
        try
        {
            adapter = (Convert.ToUInt64(parts[3], 16) << 32) | Convert.ToUInt64(parts[4], 16);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return false;
        }
    }

    private bool Open()
    {
        if (_unavailable) return false;
        if (_query != IntPtr.Zero) return true;
        // Counters can be switched off or missing on old Windows; the rest of the reading goes on without them.
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0
            || PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engines) != 0
            || PdhAddEnglishCounter(_query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _memory) != 0)
        {
            _unavailable = true;
            Dispose();
            return false;
        }
        return true;
    }

    private static List<(string Name, double Value)> Items(IntPtr counter)
    {
        var list = new List<(string, double)>();
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArray(counter, FormatDouble | FormatNoCap, ref size, ref count, IntPtr.Zero) != MoreData || size == 0) return list;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, FormatDouble | FormatNoCap, ref size, ref count, buffer) != 0) return list;
            var item = Marshal.SizeOf<CounterItem>();
            for (var i = 0; i < count; i++)
            {
                var it = Marshal.PtrToStructure<CounterItem>(buffer + i * item);
                // 0 and 1 are valid data; a counter's first reading has nothing to compare with yet.
                if (it.Status <= 1 && it.Name != IntPtr.Zero && Marshal.PtrToStringUni(it.Name) is { } name)
                    list.Add((name, it.Value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    private string? AdapterName(ulong id)
    {
        // The id changes with every restart of Windows, but not while CHC runs; an adapter added since gets looked up.
        if (_adapters is null || !_adapters.ContainsKey(id)) _adapters = GraphicsAdapters.List();
        return _adapters.GetValueOrDefault(id);
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(IntPtr query);

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}

/// <summary>The names of the graphics adapters by their id (LUID), from DXGI.</summary>
internal static unsafe partial class GraphicsAdapters
{
    private const int EnumAdapters1Slot = 12;
    private const int GetDesc1Slot = 10;
    private const int ReleaseSlot = 2;
    private const int NotFound = unchecked((int)0x887A0002);
    private const uint SoftwareFlag = 2;
    private static readonly Guid Factory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public static Dictionary<ulong, string> List()
    {
        var result = new Dictionary<ulong, string>();
        try
        {
            var iid = Factory1;
            if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == IntPtr.Zero) return result;
            var desc = stackalloc byte[320];
            for (uint i = 0; ; i++)
            {
                var enumAdapter = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, EnumAdapters1Slot);
                IntPtr adapter;
                var hr = enumAdapter(factory, i, &adapter);
                if (hr == NotFound || hr != 0) break;
                var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Slot(adapter, GetDesc1Slot);
                if (getDesc(adapter, desc) == 0 && (*(uint*)(desc + 304) & SoftwareFlag) == 0)
                {
                    // DXGI_ADAPTER_DESC1: the name in 128 characters, then the id's low and high parts after the sizes.
                    var name = new string((char*)desc);
                    var id = ((ulong)*(uint*)(desc + 300) << 32) | *(uint*)(desc + 296);
                    result[id] = name;
                }
                Release(adapter);
            }
            Release(factory);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No DXGI: a Windows without graphics. The programs are still listed, without an adapter name.
        }
        return result;
    }

    private static void* Slot(IntPtr instance, int slot) => (*(void***)instance)[slot];

    private static void Release(IntPtr instance) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, ReleaseSlot))(instance);

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
}
