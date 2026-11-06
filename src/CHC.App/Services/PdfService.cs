using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Net;
using System.Text.Json;
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

    /// <param name="footerText">Shown at the foot of every page, next to the page number.</param>
    public static async Task SaveAsync(string html, string pdfPath, string footerText, string pageWord, string ofWord, Window owner)
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

            // The DevTools way of printing, because it takes a footer of our own; the plain one prints a date and a web address.
            var footer =
                "<div style=\"width:100%;padding:0 18mm;font:8px 'Segoe UI',sans-serif;color:#59616b;display:flex;justify-content:space-between\">" +
                $"<span>{WebUtility.HtmlEncode(footerText)}</span><span>{WebUtility.HtmlEncode(pageWord)} <span class=\"pageNumber\"></span> {WebUtility.HtmlEncode(ofWord)} <span class=\"totalPages\"></span></span></div>";
            var request = JsonSerializer.Serialize(new
            {
                printBackground = true,
                paperWidth = 8.27,   // A4, inches
                paperHeight = 11.69,
                marginTop = 0.7,
                marginBottom = 0.75,
                marginLeft = 0.71,
                marginRight = 0.71,
                displayHeaderFooter = true,
                headerTemplate = "<span></span>",
                footerTemplate = footer,
            });
            var reply = await web.CallDevToolsProtocolMethodAsync("Page.printToPDF", request);
            using var document = JsonDocument.Parse(reply);
            await File.WriteAllBytesAsync(pdfPath, Convert.FromBase64String(document.RootElement.GetProperty("data").GetString() ?? ""));
        }
        finally
        {
            controller?.Close();
            try { File.Delete(htmlPath); } catch (IOException) { }
        }
    }
}
