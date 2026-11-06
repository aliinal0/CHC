using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CHC.Core.History;
using CHC.Core.Past;

namespace CHC.App.Services;

public enum RecordsResult
{
    Done,

    /// <summary>The user said no to Windows' administrator question.</summary>
    Declined,
    Failed,
}

public sealed record RecordsOutcome(RecordsResult Result, string? Error = null);

/// <summary>
/// Reads Windows' usage records through a short-lived copy of CHC, started with administrator rights for that alone,
/// so that CHC itself needn't have them and a failure in Windows' database engine can't take it down.
/// </summary>
public static class RecordsService
{
    /// <summary>How far back to read; Windows itself keeps about two months.</summary>
    private static readonly TimeSpan Period = TimeSpan.FromDays(100);

    private const int UserDeclined = 1223;

    public const string Option = "--read-records";

    /// <summary>The part that runs in the short-lived copy.</summary>
    public static bool RunChild(string outputPath) => Srum.ReadToFile(outputPath, Period);

    public static async Task<RecordsOutcome> ReadAsync(HistoryStore store, bool elevated)
    {
        if (Environment.ProcessPath is not { } exe) return new RecordsOutcome(RecordsResult.Failed, "CHC can't find its own program file.");
        var folder = Path.GetDirectoryName(HistoryStore.DefaultPath)!;
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, $"records-{Guid.NewGuid():N}.json");
        try
        {
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"{Option} \"{output}\"",
            };
            if (!elevated) start.Verb = "runas";

            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == UserDeclined)
            {
                return new RecordsOutcome(RecordsResult.Declined);
            }
            if (process is null) return new RecordsOutcome(RecordsResult.Failed, "The reading didn't start.");
            using (process)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    return new RecordsOutcome(RecordsResult.Failed, "The reading took too long.");
                }
            }
            if (!File.Exists(output)) return new RecordsOutcome(RecordsResult.Failed, "The reading left no result.");

            return await Task.Run(() =>
            {
                var records = JsonSerializer.Deserialize<SrumRecords>(File.ReadAllText(output));
                if (records is null) return new RecordsOutcome(RecordsResult.Failed, "The result couldn't be read.");
                if (records.Error is { } error) return new RecordsOutcome(RecordsResult.Failed, error);
                store.AddPastBlocks(PastAnalyzer.ToBlocks(records, store.PastExactSince()));
                return new RecordsOutcome(RecordsResult.Done);
            });
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new RecordsOutcome(RecordsResult.Failed, ex.Message);
        }
        finally
        {
            try { File.Delete(output); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a small file in CHC's own folder */ }
        }
    }
}
