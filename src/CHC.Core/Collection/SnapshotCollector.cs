using System.Reflection;
using CHC.Core.Models;
using CHC.Core.Sensors;

namespace CHC.Core.Collection;

/// <summary>Runs every collector and builds a <see cref="SystemSnapshot"/>.</summary>
public static class SnapshotCollector
{
    /// <param name="progress">Receives a localization key for each step ("scan.step.cpu"...).</param>
    public static SystemSnapshot Collect(SensorHub hub, IProgress<string>? progress = null)
    {
        progress?.Report("scan.step.sensors");
        hub.Open();
        var sensors = hub.Refresh();

        var snapshot = new SystemSnapshot
        {
            IsElevated = Native.IsElevated(),
            AppVersion = AppVersion,
            Sensors = sensors,
        };

        progress?.Report("scan.step.system");
        snapshot.Os = Safe(SystemCollector.CollectOs, new OsInfo());
        snapshot.Board = Safe(SystemCollector.CollectBoard, new BoardInfo());

        progress?.Report("scan.step.cpu");
        snapshot.Cpu = Safe(() => SystemCollector.CollectCpu(sensors), new CpuInfo());

        progress?.Report("scan.step.memory");
        snapshot.Memory = Safe(SystemCollector.CollectMemory, new MemoryInfo());

        progress?.Report("scan.step.storage");
        (snapshot.Disks, snapshot.Volumes) = Safe(() => StorageCollector.Collect(hub), ([], []));

        progress?.Report("scan.step.gpu");
        snapshot.Gpus = Safe(() => SystemCollector.CollectGpus(sensors), []);

        progress?.Report("scan.step.battery");
        snapshot.Battery = Safe(() => SystemCollector.CollectBattery(sensors), null);

        progress?.Report("scan.step.stability");
        snapshot.Stability = Safe(StabilityCollector.Collect, new StabilityInfo());
        snapshot.ProblemDevices = Safe(SystemCollector.CollectProblemDevices, []);

        return snapshot;
    }

    public static bool IsElevated => Native.IsElevated();

    public static string AppVersion { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>Applies fresh live sensor values to the parts of a snapshot that show them.</summary>
    public static void ApplyLiveSensors(SystemSnapshot snapshot, SensorSnapshot sensors)
    {
        snapshot.Sensors = sensors;
        if (sensors.Device(snapshot.Cpu.SensorHardwareId) is { } cpu)
        {
            snapshot.Cpu.LoadPercent = cpu.Find("Load", "CPU Total")?.Value ?? snapshot.Cpu.LoadPercent;
            snapshot.Cpu.TemperatureC = SystemCollector.CpuTemperature(cpu).Positive();
            snapshot.Cpu.PackagePowerW = cpu.Find("Power", "Package", "CPU Package").Positive();
        }
        foreach (var gpu in snapshot.Gpus)
            if (sensors.Device(gpu.SensorHardwareId) is { } device)
                SystemCollector.ApplyGpuSensors(gpu, device);
    }

    private static T Safe<T>(Func<T> collect, T fallback)
    {
        try { return collect(); }
        catch { return fallback; }
    }
}
