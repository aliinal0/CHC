using System.IO;
using System.Text;
using System.Windows;
using CHC.App.Services;
using CHC.Core.Localization;
using CHC.Core.Reporting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CHC.App.ViewModels;

public sealed partial class ReportViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty]
    private bool _includeSerials;

    [ObservableProperty]
    private bool _includeSensors;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastPath))]
    private string? _lastPath;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePdfCommand), nameof(SaveHtmlCommand), nameof(SaveJsonCommand))]
    private bool _isBusy;

    public bool HasLastPath => LastPath is not null;
    public bool CanExport => main.Snapshot is not null && !IsBusy;

    public void Refresh()
    {
        OnPropertyChanged(nameof(CanExport));
        SavePdfCommand.NotifyCanExecuteChanged();
        SaveHtmlCommand.NotifyCanExecuteChanged();
        SaveJsonCommand.NotifyCanExecuteChanged();
        CopySummaryCommand.NotifyCanExecuteChanged();
    }

    private ReportOptions Options => new(IncludeSerials, IncludeSensors);

    /// <summary>The main export: a ready-to-share PDF built from the report template.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task SavePdfAsync()
    {
        if (main.Snapshot is not { } s || Application.Current.MainWindow is not { } owner) return;
        if (!PdfService.IsAvailable())
        {
            StatusMessage = Loc.T("report.pdf.noruntime");
            return;
        }
        if (AskPath("pdf", Loc.T("report.filter.pdf")) is not { } path) return;

        IsBusy = true;
        StatusMessage = Loc.T("report.pdf.creating");
        try
        {
            var html = ReportWriter.Html(s, main.Health, Options with { ForPrint = true });
            await PdfService.SaveAsync(html, path, Loc.T("report.title"), $"CHC {s.AppVersion} · Computer Health Check", owner);
            Saved(path, open: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            StatusMessage = Loc.T("report.savefailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void SaveHtml()
    {
        if (main.Snapshot is not { } s) return;
        SaveText("html", Loc.T("report.filter.html"), () => ReportWriter.Html(s, main.Health, Options), open: true);
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void SaveJson()
    {
        if (main.Snapshot is not { } s) return;
        SaveText("json", Loc.T("report.filter.json"), () => ReportWriter.Json(s, main.Health, Options), open: false);
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void CopySummary()
    {
        if (main.Snapshot is not { } s) return;
        try
        {
            Clipboard.SetText(ReportWriter.Text(s, main.Health, Options));
            StatusMessage = Loc.T("report.copied");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusMessage = Loc.T("report.copyfailed");
        }
    }

    [RelayCommand]
    private void OpenLast()
    {
        if (LastPath is not null) ShellService.Open(LastPath);
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (LastPath is not null) ShellService.ShowInExplorer(LastPath);
    }

    private static string? AskPath(string extension, string filter)
    {
        var dialog = new SaveFileDialog
        {
            // No computer name in the file name: reports are meant to be shared.
            FileName = $"CHC-Report-{DateTime.Now:yyyy-MM-dd_HHmm}.{extension}",
            Filter = $"{filter}|*.{extension}",
            DefaultExt = extension,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void SaveText(string extension, string filter, Func<string> build, bool open)
    {
        if (AskPath(extension, filter) is not { } path) return;
        try
        {
            File.WriteAllText(path, build(), new UTF8Encoding(false));
            Saved(path, open);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = Loc.T("report.savefailed", ex.Message);
        }
    }

    private void Saved(string path, bool open)
    {
        LastPath = path;
        StatusMessage = Loc.T("report.saved", Path.GetFileName(path));
        if (open) ShellService.Open(path);
    }
}
