using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CHC.Core.Past;

/// <summary>What one program did in one stretch of time, as Windows noted it.</summary>
public sealed record SrumBlock(DateTime EndUtc, double RunSeconds, double SpanSeconds, string App, double FocusSeconds, double InputSeconds, long Cycles, long DiskKb, long NetBytes);

/// <summary>What Windows' hourly table says about a program: reliably only how long it ran. Its cycles are often booked under other programs.</summary>
public sealed record SrumHour(DateTime EndUtc, string App, double RunSeconds, long Cycles);

/// <summary>What was read, passed from the process that read it with administrator rights to CHC itself.</summary>
public sealed class SrumRecords
{
    public List<SrumBlock> Blocks { get; set; } = [];

    /// <summary>The time before the timeline starts, which Windows keeps for only about a week.</summary>
    public List<SrumHour> Hours { get; set; } = [];

    /// <summary>Program file name → where it is.</summary>
    public Dictionary<string, string> Paths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Processor cycles a second with every core busy at its base speed, to turn cycles into a share of the processor.</summary>
    public double CyclesPerSecond { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// Windows' own usage database, System Resource Usage Monitor, the source of Task Manager's "App history". Its timeline says for
/// each program, for about a week, when it ran, how long it was in front, and what processor, disk and network it used; an hourly table
/// goes back about two months, but holds little more than how long it ran. Neither knows frame rates or temperatures. Windows keeps the file
/// open, so it is read from a moment's snapshot of the drive, copied to a work folder and opened there. Windows' own files are not changed.
/// This needs administrator rights.
/// </summary>
public static partial class Srum
{
    private const string TimelineTable = "{5C8CF1C7-7257-4F13-B223-970EF5939312}";
    private const string HourlyTable = "{D10CA2FE-6FCF-4F6D-848E-B2E99266FA89}";

    /// <summary>The table's running time counts in ticks of 100 nanoseconds.</summary>
    private const double TicksPerSecond = 1e7;
    private const string IdTable = "SruDbIdMapTable";
    private const string DatabaseName = "SRUDB.dat";

    /// <summary>What this process was started for: reads the records and leaves them in a file. Returns whether it worked.</summary>
    public static bool ReadToFile(string outputPath, TimeSpan period)
    {
        var folder = Path.GetDirectoryName(outputPath) ?? Path.GetTempPath();
        var records = new SrumRecords();
        try
        {
            var work = Path.Combine(Path.GetTempPath(), "chc-records-" + Guid.NewGuid().ToString("N"));
            records = Read(DateTime.UtcNow - period, work, Path.Combine(folder, "records-snapshot.txt"));
        }
        catch (Exception ex)
        {
            records.Error = ex.Message;
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(records));
        return records.Error is null;
    }

    /// <summary>Reads everything since a time. Throws with a plain reason if a step fails, and always cleans up.</summary>
    public static SrumRecords Read(DateTime sinceUtc, string workFolder, string snapshotNote)
    {
        Directory.CreateDirectory(workFolder);
        try
        {
            CopyFromSnapshot(workFolder, snapshotNote);
            Recover(workFolder);
            return ReadDatabase(Path.Combine(workFolder, DatabaseName), workFolder, sinceUtc);
        }
        finally
        {
            try { Directory.Delete(workFolder, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temporary folder: Windows clears those */ }
        }
    }

    /// <summary>The snapshot lives only for the copy. Its id is noted first, so that a crash can't leave one behind for good.</summary>
    private static void CopyFromSnapshot(string target, string note)
    {
        RemoveLeftover(note);
        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var relative = Path.GetRelativePath(drive, Path.Combine(Environment.SystemDirectory, "sru"));
        using var shadows = new ManagementClass(@"root\cimv2", "Win32_ShadowCopy", null);
        using var input = shadows.GetMethodParameters("Create");
        input["Volume"] = drive;
        input["Context"] = "ClientAccessible";
        using var output = shadows.InvokeMethod("Create", input, null);
        var code = Convert.ToUInt32(output["ReturnValue"]);
        if (code != 0) throw new InvalidOperationException($"The snapshot of the drive failed (code {code}).");
        var id = (string)output["ShadowID"];
        File.WriteAllText(note, id);
        using var shadow = new ManagementObject($"Win32_ShadowCopy.ID='{id}'");
        try
        {
            shadow.Get();
            // The snapshot's own name for the drive, like \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3.
            var source = (string)shadow["DeviceObject"] + @"\" + relative;
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
        finally
        {
            shadow.Delete();
            File.Delete(note);
        }
        if (!File.Exists(Path.Combine(target, DatabaseName))) throw new FileNotFoundException("The snapshot holds no usage database.");
    }

    private static void RemoveLeftover(string note)
    {
        if (!File.Exists(note)) return;
        try
        {
            using var old = new ManagementObject($"Win32_ShadowCopy.ID='{File.ReadAllText(note).Trim()}'");
            old.Delete();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or IOException) { /* it is gone already */ }
        File.Delete(note);
    }

    /// <summary>A snapshot catches the database in mid-work: Windows' own tool finishes that from the log files beside it, on the copy.</summary>
    private static void Recover(string folder)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "esentutl.exe"))
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/r", "SRU", "/l", folder, "/s", folder, "/d", folder, "/i", "/o" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows' database tool didn't start.");
        // Read what it prints, or it stops once the pipe is full.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill();
            throw new TimeoutException("Windows' database tool took too long.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Windows' database tool failed (code {process.ExitCode}).");
    }

    private static SrumRecords ReadDatabase(string path, string folder, DateTime sinceUtc)
    {
        Span<byte> header = stackalloc byte[240];
        using (var file = File.OpenRead(path)) file.ReadExactly(header);
        using var db = new EsentReader(path, folder, BitConverter.ToInt32(header[236..]));

        var records = new SrumRecords { CyclesPerSecond = CyclesPerSecond() };
        var names = ReadIds(db, records);
        ReadTimeline(db, records, names, sinceUtc);
        var timelineStart = records.Blocks.Count > 0 ? records.Blocks.Min(b => b.EndUtc) : DateTime.MaxValue;
        ReadHours(db, records, names, sinceUtc, timelineStart);
        return records;
    }

    private static void ReadTimeline(EsentReader db, SrumRecords records, Dictionary<int, string> names, DateTime sinceUtc)
    {
        using var table = db.Open(TimelineTable);
        var app = table.Column("AppId");
        var end = table.Column("EndTime");
        var duration = table.Column("DurationMS");
        var span = table.Column("SpanMS");
        var focus = table.Column("InFocusS");
        var input = table.Column("UserInputS");
        var cycles = table.Column("Cycles");
        var disk = table.Column("DiskRaw");
        var network = table.Column("NetworkBytesRaw");
        foreach (var _ in table.Rows())
        {
            var fileTime = table.Integer(end);
            if (fileTime <= 0 || !names.TryGetValue((int)table.Integer(app), out var name)) continue;
            var when = DateTime.FromFileTimeUtc(fileTime);
            if (when < sinceUtc) continue;
            records.Blocks.Add(new SrumBlock(when, table.Integer(duration) / 1000.0, table.Integer(span) / 1000.0, name, table.Integer(focus), table.Integer(input),
                table.Integer(cycles), table.Integer(disk), table.Integer(network)));
        }
    }

    private static void ReadHours(EsentReader db, SrumRecords records, Dictionary<int, string> names, DateTime sinceUtc, DateTime before)
    {
        using var table = db.Open(HourlyTable);
        var app = table.Column("AppId");
        var time = table.Column("TimeStamp");
        var run = table.Column("FaceTime");
        var foreground = table.Column("ForegroundCycleTime");
        var background = table.Column("BackgroundCycleTime");
        foreach (var _ in table.Rows())
        {
            var when = table.Time(time);
            if (when < sinceUtc || when > before || !names.TryGetValue((int)table.Integer(app), out var name)) continue;
            var seconds = table.Integer(run) / TicksPerSecond;
            if (seconds < 1) continue;
            records.Hours.Add(new SrumHour(when, name, seconds, table.Integer(foreground) + table.Integer(background)));
        }
    }

    private static double CyclesPerSecond()
    {
        var mhz = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", 0) as int? ?? 0;
        return mhz * 1e6 * Environment.ProcessorCount;
    }

    private static Dictionary<int, string> ReadIds(EsentReader db, SrumRecords records)
    {
        var devices = DriveNames();
        var names = new Dictionary<int, string>();
        using var table = db.Open(IdTable);
        var type = table.Column("IdType");
        var index = table.Column("IdIndex");
        var blob = table.Column("IdBlob");
        foreach (var _ in table.Rows())
        {
            // 3 is a user's id, which isn't text and isn't wanted.
            if (table.Integer(type) == 3) continue;
            var text = table.UnicodeBlob(blob);
            if (text.Length == 0) continue;
            names[(int)table.Integer(index)] = text;
            if (text.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) && DrivePath(text, devices) is { } path)
                records.Paths[ExeOf(text)] = path;
        }
        return names;
    }

    private static List<(string Device, string Drive)> DriveNames()
    {
        var list = new List<(string, string)>();
        var buffer = new char[512];
        for (var letter = 'C'; letter <= 'Z'; letter++)
        {
            var count = QueryDosDevice(letter + ":", buffer, (uint)buffer.Length);
            if (count > 0) list.Add((new string(buffer, 0, (int)count).TrimEnd('\0').Split('\0')[0], letter + ":"));
        }
        return list;
    }

    private static string? DrivePath(string path, List<(string Device, string Drive)> devices)
    {
        foreach (var (device, drive) in devices)
            if (path.StartsWith(device + @"\", StringComparison.OrdinalIgnoreCase))
                return drive + path[device.Length..];
        return null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint QueryDosDevice(string name, [Out] char[] target, uint size);

    /// <summary>
    /// The plain name of what the database calls "\Device\HarddiskVolume3\Games\RDR2.exe", "!!RDR2.exe!2026/10/01:12:00:00!0!",
    /// "Package_1.2.3.0_x64__abc!App!Game.exe!2026/10/01:12:00:00!0!", "svchost.exe [netsvcs]" or "Spotify.Music_1.2.3.0_x64__abc123":
    /// the same program under all its versions.
    /// </summary>
    public static string ExeOf(string app)
    {
        var name = app;
        if (name.Contains('!'))
        {
            var parts = name.Split('!', StringSplitOptions.RemoveEmptyEntries);
            name = parts.FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ?? (parts.Length > 0 ? parts[0] : name);
        }
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        var bracket = name.IndexOf(" [", StringComparison.Ordinal);
        if (bracket > 0) name = name[..bracket];
        var version = PackageVersion().Match(name);
        return version.Success ? version.Groups[1].Value : name;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(.+?)_\d+(?:\.\d+)+_\w*__\w+$")]
    private static partial System.Text.RegularExpressions.Regex PackageVersion();
}

/// <summary>Opens a database in Windows' own format read-only, through the engine Windows has built in.</summary>
internal sealed partial class EsentReader : IDisposable
{
    private const string Dll = "esent.dll";
    private const uint ParamSystemPath = 0;
    private const uint ParamTempPath = 1;
    private const uint ParamLogPath = 2;
    private const uint ParamBaseName = 3;
    private const uint ParamRecovery = 34;
    private const uint ParamPageSize = 64;
    private const uint ReadOnly = 1;
    private const int ErrNoCurrentRecord = -1603;

    private readonly string _path;
    private nuint _instance;
    private nint _session;
    private uint _database;
    private bool _attached;

    [LibraryImport(Dll, EntryPoint = "JetSetSystemParameterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetParameter(ref nuint instance, nint session, uint parameter, nuint value, string? text);

    [LibraryImport(Dll, EntryPoint = "JetCreateInstance2W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateInstance(out nuint instance, string name, string display, uint flags);

    [LibraryImport(Dll, EntryPoint = "JetInit")]
    private static partial int Init(ref nuint instance);

    [LibraryImport(Dll, EntryPoint = "JetTerm2")]
    private static partial int Term(nuint instance, uint flags);

    [LibraryImport(Dll, EntryPoint = "JetBeginSessionW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int BeginSession(nuint instance, out nint session, string? user, string? password);

    [LibraryImport(Dll, EntryPoint = "JetEndSession")]
    private static partial int EndSession(nint session, uint flags);

    [LibraryImport(Dll, EntryPoint = "JetAttachDatabaseW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Attach(nint session, string path, uint flags);

    [LibraryImport(Dll, EntryPoint = "JetDetachDatabaseW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Detach(nint session, string path);

    [LibraryImport(Dll, EntryPoint = "JetOpenDatabaseW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int OpenDatabase(nint session, string path, string? connect, out uint database, uint flags);

    [LibraryImport(Dll, EntryPoint = "JetCloseDatabase")]
    private static partial int CloseDatabase(nint session, uint database, uint flags);

    [LibraryImport(Dll, EntryPoint = "JetOpenTableW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int OpenTable(nint session, uint database, string name, nint parameters, uint size, uint flags, out nint table);

    [LibraryImport(Dll, EntryPoint = "JetCloseTable")]
    private static partial int CloseTable(nint session, nint table);

    [LibraryImport(Dll, EntryPoint = "JetGetTableColumnInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetColumnInfo(nint session, nint table, string name, void* result, uint size, uint level);

    [LibraryImport(Dll, EntryPoint = "JetMove")]
    private static partial int Move(nint session, nint table, int rows, uint flags);

    [LibraryImport(Dll, EntryPoint = "JetRetrieveColumn")]
    private static unsafe partial int RetrieveColumn(nint session, nint table, uint column, void* data, uint size, out uint actual, uint flags, nint info);

    public EsentReader(string path, string folder, int pageSize)
    {
        _path = path;
        var dir = folder.EndsWith('\\') ? folder : folder + @"\";
        nuint global = 0;
        Check(SetParameter(ref global, 0, ParamPageSize, (nuint)pageSize, null), "page size");
        Check(CreateInstance(out _instance, "CHC-Records", "CHC", 0), "instance");
        Check(SetParameter(ref _instance, 0, ParamRecovery, 0, "Off"), "recovery");
        Check(SetParameter(ref _instance, 0, ParamSystemPath, 0, dir), "system path");
        Check(SetParameter(ref _instance, 0, ParamTempPath, 0, dir), "temp path");
        Check(SetParameter(ref _instance, 0, ParamLogPath, 0, dir), "log path");
        Check(SetParameter(ref _instance, 0, ParamBaseName, 0, "CHC"), "base name");
        Check(Init(ref _instance), "init");
        Check(BeginSession(_instance, out _session, null, null), "session");
        Check(Attach(_session, path, ReadOnly), "attach");
        _attached = true;
        Check(OpenDatabase(_session, path, null, out _database, ReadOnly), "open");
    }

    public EsentTable Open(string name)
    {
        Check(OpenTable(_session, _database, name, 0, 0, ReadOnly, out var table), name);
        return new EsentTable(_session, table);
    }

    public void Dispose()
    {
        if (_database != 0) CloseDatabase(_session, _database, 0);
        if (_attached) Detach(_session, _path);
        if (_session != 0) EndSession(_session, 0);
        if (_instance != 0) Term(_instance, 1);
        _session = 0;
        _instance = 0;
    }

    private static void Check(int error, string what)
    {
        if (error < 0) throw new InvalidOperationException($"The usage database couldn't be read ({what}: {error}).");
    }

    internal sealed unsafe class EsentTable(nint session, nint table) : IDisposable
    {
        private const int MoveFirst = int.MinValue;
        private const int MoveNext = 1;
        private const uint InfoDefault = 0;
        private const int WarningColumnEmpty = 1004;

        public uint Column(string name)
        {
            var definition = stackalloc byte[28];
            *(uint*)definition = 28;
            Check(GetColumnInfo(session, table, name, definition, 28, InfoDefault), name);
            return *(uint*)(definition + 4);
        }

        public IEnumerable<bool> Rows()
        {
            var error = Move(session, table, MoveFirst, 0);
            while (error >= 0)
            {
                yield return true;
                error = Move(session, table, MoveNext, 0);
            }
            if (error != ErrNoCurrentRecord) Check(error, "row");
        }

        /// <summary>A whole number of any size; 0 for a column left empty.</summary>
        public long Integer(uint column)
        {
            long value = 0;
            var error = RetrieveColumn(session, table, column, &value, 8, out var actual, 0, 0);
            if (error < 0 || error == WarningColumnEmpty) return 0;
            return actual switch
            {
                1 => (byte)value,
                2 => (short)value,
                4 => (int)value,
                _ => value,
            };
        }

        public DateTime Time(uint column)
        {
            double value = 0;
            var error = RetrieveColumn(session, table, column, &value, 8, out _, 0, 0);
            return error < 0 || error == WarningColumnEmpty ? DateTime.MinValue : DateTime.SpecifyKind(DateTime.FromOADate(value), DateTimeKind.Utc);
        }

        public string UnicodeBlob(uint column)
        {
            var error = RetrieveColumn(session, table, column, null, 0, out var size, 0, 0);
            if (error == WarningColumnEmpty || size == 0) return "";
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                error = RetrieveColumn(session, table, column, p, size, out size, 0, 0);
            }
            return error < 0 ? "" : Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        }

        public void Dispose() => CloseTable(session, table);
    }
}
