using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace CHC.App.Services;

/// <summary>
/// Turns the HTML report template into a PDF with the WebView2 (Edge) engine that ships with
/// Windows 10/11, so the PDF looks exactly like the designed report without a PDF layout library.
/// </summary>
public static class PdfService
{
    private static readonly string WorkFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CHC");

    public static bool IsAvailable()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch (WebView2RuntimeNotFoundException) { return false; }
    }

    public static async Task SaveAsync(string html, string pdfPath, string headerTitle, string footer, Window owner)
    {
        Directory.CreateDirectory(WorkFolder);
        var htmlPath = Path.Combine(WorkFolder, $"report-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false));
        CoreWebView2Controller? controller = null;
        try
        {
            // The default user-data folder sits next to the exe, which may be read-only (Program Files).
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(WorkFolder, "WebView2"));
            controller = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(owner).Handle);
            controller.IsVisible = false;
            var web = controller.CoreWebView2;

            var loaded = new TaskCompletionSource<bool>();
            web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            web.Navigate(new Uri(htmlPath).AbsoluteUri);
            if (!await loaded.Task) throw new IOException("The report template could not be loaded.");

            var settings = environment.CreatePrintSettings();
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.PageWidth = 8.27;   // A4, inches
            settings.PageHeight = 11.69;
            settings.MarginTop = settings.MarginBottom = 0.45;
            settings.MarginLeft = settings.MarginRight = 0.4;
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = true;
            settings.HeaderTitle = headerTitle;
            settings.FooterUri = footer;

            if (!await web.PrintToPdfAsync(pdfPath, settings))
                throw new IOException("The PDF could not be written.");
        }
        finally
        {
            controller?.Close();
            try { File.Delete(htmlPath); } catch (IOException) { }
        }
    }
}
