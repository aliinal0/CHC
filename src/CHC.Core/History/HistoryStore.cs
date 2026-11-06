using Microsoft.Data.Sqlite;

namespace CHC.Core.History;

/// <summary>
/// The history database (%LocalAppData%\CHC\history.db). Minutes are kept for a while (90 days by default), then
/// only their hourly summary stays. Everything is on this PC; nothing is sent anywhere.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CHC", "history.db");

    private readonly object _sync = new();
    private readonly SqliteConnection _db;
    private readonly Dictionary<string, long> _appIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _appPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows' own records go back about two months, and can't be read again once it has dropped them.</summary>
    private const int PastKeepDays = 120;

    /// <param name="path">The database file, or null for a database that lives in memory only (demo mode).</param>
    public HistoryStore(string? path = null)
    {
        FilePath = path;
        if (path is not null) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(path is null ? "Data Source=:memory:" : $"Data Source={path};Pooling=False");
        _db.Open();
        Execute("PRAGMA auto_vacuum = INCREMENTAL");
        if (path is not null)
        {
            Execute("PRAGMA journal_mode = WAL");
            Execute("PRAGMA synchronous = NORMAL");
        }
        Execute(Schema);
        AddMissingColumns();
    }

    /// <summary>Whatever the file looks like, these columns are there afterwards.</summary>
    private void AddMissingColumns()
    {
        using var cmd = _db.CreateCommand();
        AddColumn("apps", "title", "TEXT");
        AddColumn("apps", "path", "TEXT");
        AddColumn("minutes", "fps", "REAL");
        AddColumn("minutes", "fps_low", "REAL");

        void AddColumn(string table, string column, string type)
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0) Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
        }
    }

    public string? FilePath { get; }

    /// <summary>Days for which every minute is kept; older days keep their hourly summary.</summary>
    public int DetailDays { get; set; } = 90;

    private const string MetricColumns = """
        cpu_temp REAL, cpu_temp_max REAL, gpu_temp REAL, gpu_temp_max REAL,
        cpu_load REAL, cpu_load_max REAL, gpu_load REAL, gpu_load_max REAL, ram_load REAL,
        cpu_power REAL, cpu_power_max REAL, gpu_power REAL, gpu_power_max REAL,
        sys_power REAL, sys_power_max REAL,
        v12 REAL, v12_min REAL, v5_min REAL, v33_min REAL,
        fan_max REAL, disk_temp_max REAL,
        net_down INTEGER NOT NULL DEFAULT 0, net_up INTEGER NOT NULL DEFAULT 0
        """;

    private const string Schema = $"""
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS apps (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, title TEXT);
        CREATE TABLE IF NOT EXISTS minutes (t INTEGER PRIMARY KEY, samples INTEGER NOT NULL, {MetricColumns},
            sys_power_measured INTEGER NOT NULL DEFAULT 0, app INTEGER, fullscreen INTEGER NOT NULL DEFAULT 0,
            on_battery INTEGER, battery REAL);
        CREATE TABLE IF NOT EXISTS hours (t INTEGER PRIMARY KEY, minutes INTEGER NOT NULL, {MetricColumns});
        CREATE TABLE IF NOT EXISTS app_hours (t INTEGER NOT NULL, app INTEGER NOT NULL, minutes INTEGER NOT NULL,
            fullscreen INTEGER NOT NULL, cpu_temp REAL, cpu_temp_max REAL, gpu_temp REAL, gpu_temp_max REAL, sys_power_max REAL,
            PRIMARY KEY (t, app));
        CREATE TABLE IF NOT EXISTS program_hours (t INTEGER NOT NULL, app INTEGER NOT NULL, game INTEGER NOT NULL DEFAULT 0,
            seconds REAL NOT NULL, front_seconds REAL NOT NULL, cpu_sum REAL NOT NULL, cpu_max REAL NOT NULL, mem_sum REAL NOT NULL, mem_max REAL NOT NULL,
            read_bytes INTEGER NOT NULL, write_bytes INTEGER NOT NULL, gpu_sum REAL NOT NULL, gpu_max REAL NOT NULL,
            video_sum REAL NOT NULL, compute_sum REAL NOT NULL, vram_max REAL NOT NULL, adapter TEXT,
            PRIMARY KEY (t, app, game));
        CREATE TABLE IF NOT EXISTS past_blocks (t INTEGER NOT NULL, start INTEGER NOT NULL, app INTEGER NOT NULL, seconds REAL NOT NULL, focus REAL NOT NULL,
            input REAL NOT NULL, cpu_sum REAL NOT NULL, disk_bytes INTEGER NOT NULL, net_bytes INTEGER NOT NULL, hourly INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (t, app));
        CREATE TABLE IF NOT EXISTS wifi_minutes (t INTEGER PRIMARY KEY, signal INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS device_events (t INTEGER NOT NULL, key TEXT NOT NULL, name TEXT NOT NULL, arrived INTEGER NOT NULL, wireless INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS device_events_t ON device_events (t);
        CREATE TABLE IF NOT EXISTS changes (key TEXT PRIMARY KEY, t INTEGER NOT NULL, kind INTEGER NOT NULL, title TEXT NOT NULL,
            before TEXT, after TEXT, exact INTEGER NOT NULL, setting TEXT, stability INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS changes_t ON changes (t);
        CREATE TABLE IF NOT EXISTS inventory (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """;

    // ---------- writing ----------

    public void Write(MinuteRecord m)
    {
        var t = ToUnix(m.Start);
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            Insert(m, t, tx);
            var hour = t / 3600 * 3600;
            RollUp(hour, hour + 3600, tx);
            tx.Commit();
        }
    }

    /// <summary>Writes many minutes in one go and summarizes their hours once (demo data, tests).</summary>
    public void WriteAll(IEnumerable<MinuteRecord> minutes)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            long first = long.MaxValue, last = long.MinValue;
            foreach (var m in minutes)
            {
                var t = ToUnix(m.Start);
                Insert(m, t, tx);
                first = Math.Min(first, t);
                last = Math.Max(last, t);
            }
            if (first <= last) RollUp(first / 3600 * 3600, last / 3600 * 3600 + 3600, tx);
            tx.Commit();
        }
    }

    /// <summary>Saves what programs used in an hour; a row saved earlier for the same hour is replaced by its newer total.</summary>
    public void WritePrograms(IEnumerable<ProgramHour> rows)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (var p in rows)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO program_hours (t, app, game, seconds, front_seconds, cpu_sum, cpu_max, mem_sum, mem_max,
                        read_bytes, write_bytes, gpu_sum, gpu_max, video_sum, compute_sum, vram_max, adapter)
                    VALUES ($t, $app, $game, $seconds, $front, $cpu, $cpu_max, $mem, $mem_max, $read, $write, $gpu, $gpu_max, $video, $compute, $vram, $adapter)
                    """;
                Add(cmd, "$t", ToUnix(p.Start));
                Add(cmd, "$app", AppId(p.App, p.AppTitle, null, tx));
                Add(cmd, "$game", p.Game is null ? 0 : AppId(p.Game, null, null, tx));
                Add(cmd, "$seconds", p.Seconds);
                Add(cmd, "$front", p.FrontSeconds);
                Add(cmd, "$cpu", p.CpuSum);
                Add(cmd, "$cpu_max", p.CpuMax);
                Add(cmd, "$mem", p.MemorySum);
                Add(cmd, "$mem_max", p.MemoryMax);
                Add(cmd, "$read", p.ReadBytes);
                Add(cmd, "$write", p.WriteBytes);
                Add(cmd, "$gpu", p.GpuSum);
                Add(cmd, "$gpu_max", p.GpuMax);
                Add(cmd, "$video", p.VideoSum);
                Add(cmd, "$compute", p.ComputeSum);
                Add(cmd, "$vram", p.VramMax);
                Add(cmd, "$adapter", p.Adapter);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private void Insert(MinuteRecord m, long t, SqliteTransaction tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO minutes (t, samples, cpu_temp, cpu_temp_max, gpu_temp, gpu_temp_max,
                cpu_load, cpu_load_max, gpu_load, gpu_load_max, ram_load, cpu_power, cpu_power_max, gpu_power, gpu_power_max,
                sys_power, sys_power_max, sys_power_measured, v12, v12_min, v5_min, v33_min, fan_max, disk_temp_max,
                net_down, net_up, app, fullscreen, on_battery, battery, fps, fps_low)
            VALUES ($t, $samples, $cpu_temp, $cpu_temp_max, $gpu_temp, $gpu_temp_max,
                $cpu_load, $cpu_load_max, $gpu_load, $gpu_load_max, $ram_load, $cpu_power, $cpu_power_max, $gpu_power, $gpu_power_max,
                $sys_power, $sys_power_max, $measured, $v12, $v12_min, $v5_min, $v33_min, $fan_max, $disk_temp_max,
                $net_down, $net_up, $app, $fullscreen, $on_battery, $battery, $fps, $fps_low)
            """;
        Add(cmd, "$t", t);
        Add(cmd, "$samples", m.Samples);
        Add(cmd, "$cpu_temp", m.CpuTemp);
        Add(cmd, "$cpu_temp_max", m.CpuTempMax);
        Add(cmd, "$gpu_temp", m.GpuTemp);
        Add(cmd, "$gpu_temp_max", m.GpuTempMax);
        Add(cmd, "$cpu_load", m.CpuLoad);
        Add(cmd, "$cpu_load_max", m.CpuLoadMax);
        Add(cmd, "$gpu_load", m.GpuLoad);
        Add(cmd, "$gpu_load_max", m.GpuLoadMax);
        Add(cmd, "$ram_load", m.RamLoad);
        Add(cmd, "$cpu_power", m.CpuPower);
        Add(cmd, "$cpu_power_max", m.CpuPowerMax);
        Add(cmd, "$gpu_power", m.GpuPower);
        Add(cmd, "$gpu_power_max", m.GpuPowerMax);
        Add(cmd, "$sys_power", m.SystemPower);
        Add(cmd, "$sys_power_max", m.SystemPowerMax);
        Add(cmd, "$measured", m.SystemPowerMeasured ? 1 : 0);
        Add(cmd, "$v12", m.Volt12);
        Add(cmd, "$v12_min", m.Volt12Min);
        Add(cmd, "$v5_min", m.Volt5Min);
        Add(cmd, "$v33_min", m.Volt33Min);
        Add(cmd, "$fan_max", m.FanMax);
        Add(cmd, "$disk_temp_max", m.DiskTempMax);
        Add(cmd, "$net_down", m.NetDown);
        Add(cmd, "$net_up", m.NetUp);
        Add(cmd, "$app", m.App is null ? null : AppId(m.App, m.AppTitle, m.AppPath, tx));
        Add(cmd, "$fullscreen", m.Fullscreen ? 1 : 0);
        Add(cmd, "$on_battery", m.OnBattery is null ? null : m.OnBattery.Value ? 1 : 0);
        Add(cmd, "$battery", m.BatteryPercent);
        Add(cmd, "$fps", m.Fps);
        Add(cmd, "$fps_low", m.FpsLow);
        cmd.ExecuteNonQuery();
    }

    private long AppId(string name, string? title, string? path, SqliteTransaction tx)
    {
        // A program often gets its path later than its id: only once it has run fullscreen.
        if (_appIds.TryGetValue(name, out var id) && (path is null || _appPaths.Contains(name))) return id;
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO apps (name, title, path) VALUES ($n, $title, $path)
                ON CONFLICT(name) DO UPDATE SET title = COALESCE(apps.title, excluded.title), path = COALESCE(excluded.path, apps.path);
            SELECT id FROM apps WHERE name = $n
            """;
        Add(cmd, "$n", name);
        Add(cmd, "$title", title);
        Add(cmd, "$path", path);
        id = (long)cmd.ExecuteScalar()!;
        _appIds[name] = id;
        if (path is not null) _appPaths.Add(name);
        return id;
    }

    /// <summary>Summarizes the minutes of [from, to) into their hours.</summary>
    private void RollUp(long from, long to, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO hours SELECT t / 3600 * 3600, COUNT(*),
                AVG(cpu_temp), MAX(cpu_temp_max), AVG(gpu_temp), MAX(gpu_temp_max),
                AVG(cpu_load), MAX(cpu_load_max), AVG(gpu_load), MAX(gpu_load_max), AVG(ram_load),
                AVG(cpu_power), MAX(cpu_power_max), AVG(gpu_power), MAX(gpu_power_max),
                AVG(sys_power), MAX(sys_power_max),
                AVG(v12), MIN(v12_min), MIN(v5_min), MIN(v33_min),
                MAX(fan_max), MAX(disk_temp_max), SUM(net_down), SUM(net_up)
            FROM minutes WHERE t >= $from AND t < $to GROUP BY t / 3600;
            INSERT OR REPLACE INTO app_hours SELECT t / 3600 * 3600, app, COUNT(*), SUM(fullscreen),
                AVG(cpu_temp), MAX(cpu_temp_max), AVG(gpu_temp), MAX(gpu_temp_max), MAX(sys_power_max)
            FROM minutes WHERE app IS NOT NULL AND t >= $from AND t < $to GROUP BY t / 3600, app;
            """;
        Add(cmd, "$from", from);
        Add(cmd, "$to", to);
        cmd.ExecuteNonQuery();
    }

    // ---------- reading ----------

    private static (string Avg, string Min, string Max) Columns(HistoryMetric metric) => metric switch
    {
        HistoryMetric.CpuTemp => ("cpu_temp", "cpu_temp", "cpu_temp_max"),
        HistoryMetric.GpuTemp => ("gpu_temp", "gpu_temp", "gpu_temp_max"),
        HistoryMetric.CpuLoad => ("cpu_load", "cpu_load", "cpu_load_max"),
        HistoryMetric.GpuLoad => ("gpu_load", "gpu_load", "gpu_load_max"),
        HistoryMetric.RamLoad => ("ram_load", "ram_load", "ram_load"),
        HistoryMetric.CpuPower => ("cpu_power", "cpu_power", "cpu_power_max"),
        HistoryMetric.GpuPower => ("gpu_power", "gpu_power", "gpu_power_max"),
        HistoryMetric.SystemPower => ("sys_power", "sys_power", "sys_power_max"),
        HistoryMetric.Volt12 => ("v12", "v12_min", "v12"),
        HistoryMetric.Volt5 => ("v5_min", "v5_min", "v5_min"),
        HistoryMetric.Volt33 => ("v33_min", "v33_min", "v33_min"),
        HistoryMetric.Fan => ("fan_max", "fan_max", "fan_max"),
        HistoryMetric.DiskTemp => ("disk_temp_max", "disk_temp_max", "disk_temp_max"),
        HistoryMetric.NetDown => ("net_down", "net_down", "net_down"),
        _ => ("net_up", "net_up", "net_up"),
    };

    private static bool IsTraffic(HistoryMetric metric) => metric is HistoryMetric.NetDown or HistoryMetric.NetUp;

    /// <summary>
    /// Chart points for [from, to): one per <paramref name="slot"/>, aligned to local time so days start at midnight.
    /// Network traffic comes as bytes per second (average of the slot; the highest minute as Max).
    /// </summary>
    public IReadOnlyList<HistoryPoint> Series(HistoryMetric metric, DateTime from, DateTime to, TimeSpan slot)
    {
        var (avg, min, max) = Columns(metric);
        var minutes = slot < TimeSpan.FromHours(1);
        var table = minutes ? "minutes" : "hours";
        var count = minutes ? "COUNT(*)" : "SUM(minutes)";
        var select = IsTraffic(metric)
            ? $"SUM({avg}) / ({count} * 60.0), NULL, MAX({avg} * 1.0 / {(minutes ? "60" : "(minutes * 60)")})"
            : $"AVG({avg}), MIN({min}), MAX({max})";
        var s = (long)slot.TotalSeconds;
        var off = Offset(from);
        var list = new List<HistoryPoint>();
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT (t + $off) / $s * $s - $off AS b, {select} FROM {table}
                WHERE t >= $from AND t < $to GROUP BY (t + $off) / $s ORDER BY b
                """;
            Add(cmd, "$off", off);
            Add(cmd, "$s", s);
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new HistoryPoint(FromUnix(r.GetInt64(0)), Num(r, 1), Num(r, 2), Num(r, 3)));
        }
        return list;
    }

    /// <summary>The programs in front in [from, to), most used first.</summary>
    public IReadOnlyList<AppUsage> Apps(DateTime from, DateTime to, int limit = 20)
    {
        var list = new List<AppUsage>();
        var recent = from >= DateTime.UtcNow.AddDays(-DetailDays + 1);
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = recent
                ? """
                  SELECT a.name, a.title, COUNT(*), SUM(m.fullscreen), AVG(m.cpu_temp), MAX(m.cpu_temp_max), AVG(m.gpu_temp), MAX(m.gpu_temp_max), MAX(m.sys_power_max)
                  FROM minutes m JOIN apps a ON a.id = m.app WHERE m.t >= $from AND m.t < $to
                  GROUP BY m.app ORDER BY COUNT(*) DESC LIMIT $n
                  """
                : """
                  SELECT a.name, a.title, SUM(h.minutes), SUM(h.fullscreen), SUM(h.cpu_temp * h.minutes) / SUM(h.minutes), MAX(h.cpu_temp_max),
                         SUM(h.gpu_temp * h.minutes) / SUM(h.minutes), MAX(h.gpu_temp_max), MAX(h.sys_power_max)
                  FROM app_hours h JOIN apps a ON a.id = h.app WHERE h.t >= $from AND h.t < $to
                  GROUP BY h.app ORDER BY SUM(h.minutes) DESC LIMIT $n
                  """;
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            Add(cmd, "$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AppUsage(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2), r.GetInt32(3), Num(r, 4), Num(r, 5), Num(r, 6), Num(r, 7), Num(r, 8)));
        }
        return list;
    }

    /// <summary>What ran fullscreen in [from, to), most played first.</summary>
    public IReadOnlyList<GameUsage> Games(DateTime from, DateTime to, int limit = 20)
    {
        var list = new List<GameUsage>();
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT a.name, a.title, a.path, COUNT(*), MAX(m.t), AVG(m.fps), AVG(m.fps_low), MAX(m.fps), COUNT(m.fps),
                       AVG(m.gpu_load), COALESCE(100.0 * SUM(m.gpu_load >= 90) / COUNT(m.gpu_load), 0), AVG(m.cpu_load),
                       MAX(m.gpu_temp_max), MAX(m.cpu_temp_max), AVG(m.ram_load)
                FROM minutes m JOIN apps a ON a.id = m.app WHERE m.fullscreen = 1 AND m.t >= $from AND m.t < $to
                GROUP BY m.app ORDER BY COUNT(*) DESC LIMIT $n
                """;
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            Add(cmd, "$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GameUsage(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3),
                    FromUnix(r.GetInt64(4)), Num(r, 5), Num(r, 6), Num(r, 7), r.GetInt32(8), Num(r, 9), r.GetDouble(10), Num(r, 11), Num(r, 12), Num(r, 13), Num(r, 14)));
        }
        return list;
    }

    /// <summary>What each program used in [from, to); with <paramref name="game"/> only while that game was being played.</summary>
    public IReadOnlyList<ProgramUsage> Programs(DateTime from, DateTime to, string? game = null)
    {
        var list = new List<ProgramUsage>();
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            var filter = game is null ? "" : "AND p.game = (SELECT id FROM apps WHERE name = $game)";
            cmd.CommandText = $"""
                SELECT a.name, a.title, SUM(p.seconds), SUM(p.front_seconds), SUM(p.cpu_sum) / SUM(p.seconds), MAX(p.cpu_max),
                       SUM(p.mem_sum) / SUM(p.seconds), MAX(p.mem_max), SUM(p.read_bytes), SUM(p.write_bytes),
                       SUM(p.gpu_sum) / SUM(p.seconds), MAX(p.gpu_max), SUM(p.video_sum) / SUM(p.seconds), SUM(p.compute_sum) / SUM(p.seconds), MAX(p.vram_max),
                       (SELECT x.adapter FROM program_hours x WHERE x.app = p.app AND x.t >= $from AND x.t < $to AND x.adapter IS NOT NULL ORDER BY x.gpu_sum DESC LIMIT 1)
                FROM program_hours p JOIN apps a ON a.id = p.app
                WHERE p.t >= $from AND p.t < $to AND p.seconds > 0 {filter}
                GROUP BY p.app
                """;
            // Hours are whole: the one the period begins in counts.
            Add(cmd, "$from", ToUnix(from) / 3600 * 3600);
            Add(cmd, "$to", ToUnix(to));
            if (game is not null) Add(cmd, "$game", game);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ProgramUsage(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7),
                    r.GetInt64(8), r.GetInt64(9), r.GetDouble(10), r.GetDouble(11), r.GetDouble(12), r.GetDouble(13), r.GetDouble(14), r.IsDBNull(15) ? null : r.GetString(15)));
        }
        return list;
    }

    /// <summary>Keeps what Windows' usage records said. Reading them again replaces what was read before.</summary>
    public void AddPastBlocks(IEnumerable<Past.PastBlock> blocks)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO past_blocks (t, start, app, seconds, focus, input, cpu_sum, disk_bytes, net_bytes, hourly) VALUES ($t, $start, $app, $seconds, $focus, $input, $cpu, $disk, $net, $hourly)";
            var t = cmd.CreateParameter(); t.ParameterName = "$t"; cmd.Parameters.Add(t);
            var start = cmd.CreateParameter(); start.ParameterName = "$start"; cmd.Parameters.Add(start);
            var app = cmd.CreateParameter(); app.ParameterName = "$app"; cmd.Parameters.Add(app);
            var seconds = cmd.CreateParameter(); seconds.ParameterName = "$seconds"; cmd.Parameters.Add(seconds);
            var focus = cmd.CreateParameter(); focus.ParameterName = "$focus"; cmd.Parameters.Add(focus);
            var input = cmd.CreateParameter(); input.ParameterName = "$input"; cmd.Parameters.Add(input);
            var cpu = cmd.CreateParameter(); cpu.ParameterName = "$cpu"; cmd.Parameters.Add(cpu);
            var disk = cmd.CreateParameter(); disk.ParameterName = "$disk"; cmd.Parameters.Add(disk);
            var net = cmd.CreateParameter(); net.ParameterName = "$net"; cmd.Parameters.Add(net);
            var hourly = cmd.CreateParameter(); hourly.ParameterName = "$hourly"; cmd.Parameters.Add(hourly);
            foreach (var b in blocks)
            {
                t.Value = ToUnix(b.End);
                start.Value = ToUnix(b.Start);
                app.Value = AppId(b.Exe, null, b.Path, tx);
                seconds.Value = b.Seconds;
                focus.Value = b.Focus;
                input.Value = b.Input;
                cpu.Value = b.CpuSum;
                disk.Value = b.DiskBytes;
                net.Value = b.NetBytes;
                hourly.Value = b.Hourly ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>What Windows' usage records said about [from, to).</summary>
    public IReadOnlyList<Past.PastBlock> PastBlocks(DateTime from, DateTime to)
    {
        var list = new List<Past.PastBlock>();
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT b.start, b.t, a.name, a.path, b.seconds, b.focus, b.input, b.cpu_sum, b.disk_bytes, b.net_bytes, b.hourly
                FROM past_blocks b JOIN apps a ON a.id = b.app WHERE b.t >= $from AND b.start < $to ORDER BY b.t
                """;
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Past.PastBlock(FromUnix(r.GetInt64(0)), FromUnix(r.GetInt64(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetInt64(8), r.GetInt64(9), r.GetInt32(10) == 1));
        }
        return list;
    }

    /// <summary>How far back Windows' usage records were read; null if they never were.</summary>
    public DateTime? PastOldest()
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT MIN(start) FROM past_blocks";
            return cmd.ExecuteScalar() is long t ? FromUnix(t) : null;
        }
    }

    /// <summary>Where the exact stretches in the history begin; earlier readings may reach further back than Windows still does.</summary>
    public DateTime? PastExactSince()
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT MIN(start) FROM past_blocks WHERE hourly = 0";
            return cmd.ExecuteScalar() is long t ? FromUnix(t) : null;
        }
    }

    /// <summary>Keeps the Wi-Fi signal of some minutes.</summary>
    public void AddWifiSignals(IEnumerable<(DateTime Time, int Signal)> signals)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (var (time, signal) in signals)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO wifi_minutes (t, signal) VALUES ($t, $signal)";
                Add(cmd, "$t", ToUnix(time));
                Add(cmd, "$signal", signal);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>The signal in [from, to): null when nothing was recorded, as on a PC without Wi-Fi.</summary>
    public Devices.WifiHealth? WifiSignal(DateTime from, DateTime to, int weakBelow)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), AVG(signal), MIN(signal), SUM(signal < $weak) FROM wifi_minutes WHERE t >= $from AND t < $to";
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            Add(cmd, "$weak", weakBelow);
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.GetInt32(0) == 0) return null;
            var count = r.GetInt32(0);
            return new Devices.WifiHealth(count, (int)Math.Round(r.GetDouble(1)), r.GetInt32(2), (int)Math.Round(r.GetInt32(3) * 100.0 / count));
        }
    }

    /// <summary>Keeps devices arriving and going.</summary>
    public void AddDeviceEvents(IEnumerable<Devices.DeviceEvent> events)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (var e in events)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO device_events (t, key, name, arrived, wireless) VALUES ($t, $key, $name, $arrived, $wireless)";
                Add(cmd, "$t", new DateTimeOffset(e.Time).ToUnixTimeMilliseconds());
                Add(cmd, "$key", e.Key);
                Add(cmd, "$name", e.Name);
                Add(cmd, "$arrived", e.Arrived ? 1 : 0);
                Add(cmd, "$wireless", e.Wireless ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Devices arriving and going in [from, to), oldest first.</summary>
    public IReadOnlyList<Devices.DeviceEvent> DeviceEvents(DateTime from, DateTime to)
    {
        var list = new List<Devices.DeviceEvent>();
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT t, key, name, arrived, wireless FROM device_events WHERE t >= $from AND t < $to ORDER BY t";
            Add(cmd, "$from", new DateTimeOffset(from).ToUnixTimeMilliseconds());
            Add(cmd, "$to", new DateTimeOffset(to).ToUnixTimeMilliseconds());
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Devices.DeviceEvent(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).LocalDateTime, r.GetString(1), r.GetString(2), r.GetInt32(3) == 1, r.GetInt32(4) == 1));
        }
        return list;
    }

    /// <summary>Percent of the recorded minutes in [from, to) in which a metric's average reached <paramref name="level"/>.</summary>
    public double ShareAbove(HistoryMetric metric, double level, DateTime from, DateTime to)
    {
        var col = Columns(metric).Avg;
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT COALESCE(100.0 * SUM({col} >= $level) / COUNT({col}), 0) FROM minutes WHERE t >= $from AND t < $to";
            Add(cmd, "$level", level);
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            return Convert.ToDouble(cmd.ExecuteScalar());
        }
    }

    /// <summary>On how many local days of [from, to) something was recorded.</summary>
    public int RecordedDays(DateTime from, DateTime to)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(DISTINCT (t + $off) / 86400) FROM minutes WHERE t >= $from AND t < $to";
            Add(cmd, "$off", Offset(from));
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>The last minute recorded in the five minutes up to <paramref name="time"/>: what the PC was doing right then.</summary>
    public Models.IncidentMoment? MomentBefore(DateTime time)
    {
        lock (_sync)
        {
            // Before CHC was recording, Windows' records can still say what was in front; only the load and the heat are unknown.
            using var past = _db.CreateCommand();
            past.CommandText = "SELECT COALESCE(a.title, a.name), a.name FROM past_blocks b JOIN apps a ON a.id = b.app WHERE b.start <= $t AND b.t >= $t AND b.focus >= 30 ORDER BY b.focus DESC LIMIT 1";
            Add(past, "$t", ToUnix(time));
            Models.IncidentMoment? fromRecords = null;
            using (var row = past.ExecuteReader())
                if (row.Read()) fromRecords = new Models.IncidentMoment(row.IsDBNull(0) ? null : row.GetString(0), row.GetString(1), false, null, null, null, null, null);

            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(a.title, a.name), a.name, m.fullscreen, m.cpu_load, m.gpu_load, m.cpu_temp_max, m.gpu_temp_max, m.ram_load
                FROM minutes m LEFT JOIN apps a ON a.id = m.app WHERE m.t <= $t AND m.t >= $t - 300 ORDER BY m.t DESC LIMIT 1
                """;
            Add(cmd, "$t", ToUnix(time));
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new Models.IncidentMoment(r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2) == 1, Num(r, 3), Num(r, 4), Num(r, 5), Num(r, 6), Num(r, 7))
                : fromRecords;
        }
    }

    /// <summary>A game's average frame rate in [from, to), plugged in only: a laptop on battery isn't the same PC.</summary>
    public (double? Fps, int Minutes) GameFps(string app, DateTime from, DateTime to)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT AVG(m.fps), COUNT(m.fps) FROM minutes m JOIN apps a ON a.id = m.app
                WHERE a.name = $n AND m.fullscreen = 1 AND COALESCE(m.on_battery, 0) = 0 AND m.t >= $from AND m.t < $to
                """;
            Add(cmd, "$n", app);
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            using var r = cmd.ExecuteReader();
            return r.Read() ? (Num(r, 0), r.GetInt32(1)) : (null, 0);
        }
    }

    /// <summary>A game's frame rate per local day in [from, to): average, its slowest 1% as Min, the best minute as Max.</summary>
    /// <summary>The minutes CHC itself saw a game being played, per day.</summary>
    public IReadOnlyList<Past.PastDay> GameMinutesDaily(string app, DateTime from, DateTime to) => MinutesDaily(from, to, app);

    /// <summary>The minutes CHC was recording, per day.</summary>
    public IReadOnlyList<Past.PastDay> RecordedMinutesDaily(DateTime from, DateTime to) => MinutesDaily(from, to, null);

    private List<Past.PastDay> MinutesDaily(DateTime from, DateTime to, string? app)
    {
        const long day = 86400;
        var off = Offset(from);
        var list = new List<Past.PastDay>();
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            var filter = app is null ? "" : "JOIN apps a ON a.id = m.app WHERE a.name = $n AND m.fullscreen = 1 AND";
            cmd.CommandText = $"SELECT (m.t + $off) / $day * $day - $off, COUNT(*) FROM minutes m {(app is null ? "WHERE" : filter)} m.t >= $from AND m.t < $to GROUP BY (m.t + $off) / $day ORDER BY 1";
            Add(cmd, "$off", off);
            Add(cmd, "$day", day);
            if (app is not null) Add(cmd, "$n", app);
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new Past.PastDay(FromUnix(r.GetInt64(0)).Date, r.GetInt32(1)));
        }
        return list;
    }

    /// <summary>The hours in which CHC was recording at all.</summary>
    public IReadOnlyList<DateTime> RecordedHours(DateTime from, DateTime to)
    {
        var list = new List<DateTime>();
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT t / 3600 * 3600 FROM minutes WHERE t >= $from AND t < $to";
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(FromUnix(r.GetInt64(0)));
        }
        return list;
    }

    public IReadOnlyList<HistoryPoint> GameDaily(string app, DateTime from, DateTime to)
    {
        const long day = 86400;
        var off = Offset(from);
        var list = new List<HistoryPoint>();
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT (m.t + $off) / $day * $day - $off AS b, AVG(m.fps), AVG(m.fps_low), MAX(m.fps) FROM minutes m JOIN apps a ON a.id = m.app
                WHERE a.name = $n AND m.fullscreen = 1 AND m.fps IS NOT NULL AND m.t >= $from AND m.t < $to
                GROUP BY (m.t + $off) / $day ORDER BY b
                """;
            Add(cmd, "$off", off);
            Add(cmd, "$day", day);
            Add(cmd, "$n", app);
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new HistoryPoint(FromUnix(r.GetInt64(0)), Num(r, 1), Num(r, 2), Num(r, 3)));
        }
        return list;
    }

    /// <summary>The highest value of a metric in [from, to) and what was in front at that moment.</summary>
    public HistoryPeak? Peak(HistoryMetric metric, DateTime from, DateTime to)
    {
        if (IsTraffic(metric)) return null;
        var col = Columns(metric).Max;
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT m.t, m.{col}, COALESCE(a.title, a.name) FROM minutes m LEFT JOIN apps a ON a.id = m.app
                WHERE m.t >= $from AND m.t < $to AND m.{col} IS NOT NULL ORDER BY m.{col} DESC LIMIT 1
                """;
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            using (var r = cmd.ExecuteReader())
                if (r.Read()) return new HistoryPeak(metric, r.GetDouble(1), FromUnix(r.GetInt64(0)), r.IsDBNull(2) ? null : r.GetString(2));

            // Older than the kept minutes: the hour it happened in.
            cmd.CommandText = $"SELECT t, {col} FROM hours WHERE t >= $from AND t < $to AND {col} IS NOT NULL ORDER BY {col} DESC LIMIT 1";
            using var h = cmd.ExecuteReader();
            return h.Read() ? new HistoryPeak(metric, h.GetDouble(1), FromUnix(h.GetInt64(0)), null) : null;
        }
    }

    /// <summary>Bytes received and sent in [from, to).</summary>
    public (long Down, long Up) Traffic(DateTime from, DateTime to)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(net_down), 0), COALESCE(SUM(net_up), 0) FROM hours WHERE t >= $from AND t < $to";
            Add(cmd, "$from", ToUnix(from) / 3600 * 3600);
            Add(cmd, "$to", ToUnix(to));
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
        }
    }

    /// <summary>Minutes recorded in [from, to) (how much of the period the history covers).</summary>
    public int RecordedMinutes(DateTime from, DateTime to)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = from >= DateTime.UtcNow.AddDays(-DetailDays + 1)
                ? "SELECT COUNT(*) FROM minutes WHERE t >= $from AND t < $to"
                : "SELECT COALESCE(SUM(minutes), 0) FROM hours WHERE t >= $from AND t < $to";
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>When the history starts, or null while it is empty.</summary>
    public DateTime? FirstRecord()
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT MIN(t) FROM hours";
            return cmd.ExecuteScalar() is long t ? FromUnix(t) : null;
        }
    }

    public long SizeBytes =>
        FilePath is null ? 0 : new[] { FilePath, FilePath + "-wal" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);

    // ---------- change timeline ----------

    /// <summary>Adds changes to the timeline; one already there (same key) keeps its first time.</summary>
    public void AddChanges(IEnumerable<Timeline.SystemChange> changes)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (var c in changes)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO changes (key, t, kind, title, before, after, exact, setting, stability)
                    VALUES ($key, $t, $kind, $title, $before, $after, $exact, $setting, $stability)
                    """;
                Add(cmd, "$key", c.Key);
                Add(cmd, "$t", ToUnix(c.Time));
                Add(cmd, "$kind", (int)c.Kind);
                Add(cmd, "$title", c.Title);
                Add(cmd, "$before", c.Before);
                Add(cmd, "$after", c.After);
                Add(cmd, "$exact", c.Exact ? 1 : 0);
                Add(cmd, "$setting", c.Setting);
                Add(cmd, "$stability", c.CanAffectStability ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Changes in [from, to), newest first.</summary>
    public IReadOnlyList<Timeline.SystemChange> Changes(DateTime from, DateTime to, int limit = 500)
    {
        var list = new List<Timeline.SystemChange>();
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT key, t, kind, title, before, after, exact, setting, stability FROM changes
                WHERE t >= $from AND t < $to ORDER BY t DESC LIMIT $n
                """;
            Add(cmd, "$from", ToUnix(from));
            Add(cmd, "$to", ToUnix(to));
            Add(cmd, "$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Timeline.SystemChange
                {
                    Key = r.GetString(0),
                    Time = FromUnix(r.GetInt64(1)),
                    Kind = (Timeline.ChangeKind)r.GetInt32(2),
                    Title = r.GetString(3),
                    Before = r.IsDBNull(4) ? null : r.GetString(4),
                    After = r.IsDBNull(5) ? null : r.GetString(5),
                    Exact = r.GetInt32(6) == 1,
                    Setting = r.IsDBNull(7) ? null : r.GetString(7),
                    CanAffectStability = r.GetInt32(8) == 1,
                });
        }
        return list;
    }

    /// <summary>How CHC last saw the PC (BIOS version, parts, settings, programs), to notice what changed since.</summary>
    public Dictionary<string, string> Inventory()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM inventory";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetString(0)] = r.GetString(1);
        }
        return result;
    }

    /// <summary>Replaces the inventory entries whose keys start with one of <paramref name="prefixes"/>.</summary>
    public void SaveInventory(IReadOnlyDictionary<string, string> values, IReadOnlyCollection<string> prefixes)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (var prefix in prefixes)
            {
                using var delete = _db.CreateCommand();
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM inventory WHERE lower(substr(key, 1, length($p))) = lower($p)";
                Add(delete, "$p", prefix);
                delete.ExecuteNonQuery();
            }
            foreach (var (key, value) in values)
            {
                if (!prefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                using var insert = _db.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT OR REPLACE INTO inventory (key, value) VALUES ($k, $v)";
                Add(insert, "$k", key);
                Add(insert, "$v", value);
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------- upkeep ----------

    /// <summary>Drops minutes older than <see cref="DetailDays"/> (their hours stay) and gives the space back.</summary>
    public void Prune()
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            // Device events count in milliseconds.
            cmd.CommandText = "DELETE FROM minutes WHERE t < $before; DELETE FROM program_hours WHERE t < $before; DELETE FROM device_events WHERE t < $before * 1000; DELETE FROM wifi_minutes WHERE t < $before; DELETE FROM past_blocks WHERE t < $pastBefore";
            Add(cmd, "$before", ToUnix(DateTime.UtcNow.AddDays(-DetailDays)));
            Add(cmd, "$pastBefore", ToUnix(DateTime.UtcNow.AddDays(-Math.Max(DetailDays, PastKeepDays))));
            if (cmd.ExecuteNonQuery() > 0) Execute("PRAGMA incremental_vacuum");
        }
    }

    /// <summary>Deletes the whole history.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            Execute("DELETE FROM minutes; DELETE FROM hours; DELETE FROM app_hours; DELETE FROM program_hours; DELETE FROM device_events; DELETE FROM wifi_minutes; DELETE FROM past_blocks; DELETE FROM apps; DELETE FROM changes; DELETE FROM inventory;");
            _appIds.Clear();
            _appPaths.Clear();
            Execute("VACUUM");
        }
    }

    // ---------- helpers ----------

    /// <summary>Lets other parts of CHC (the change timeline) keep their tables in the same file.</summary>
    internal T Use<T>(Func<SqliteConnection, T> work)
    {
        lock (_sync) return work(_db);
    }

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static void Add(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static double? Num(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    internal static long ToUnix(DateTime time) =>
        new DateTimeOffset(time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Local) : time).ToUnixTimeSeconds();

    internal static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;

    /// <summary>Seconds to add to UTC to get local time around <paramref name="time"/>, so slots start at local midnight.</summary>
    private static long Offset(DateTime time) => (long)TimeZoneInfo.Local.GetUtcOffset(time).TotalSeconds;

    public void Dispose()
    {
        lock (_sync) _db.Dispose();
    }
}
