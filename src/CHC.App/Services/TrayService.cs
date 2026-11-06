using System.Windows;
using CHC.Core.Localization;
using Forms = System.Windows.Forms;

namespace CHC.App.Services;

/// <summary>CHC's icon in the notification area while it records with the window closed.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Action _show;
    private readonly Action<bool> _pause;
    private readonly Action _exit;
    private bool _paused;

    /// <param name="show">Open the window.</param>
    /// <param name="pause">Pause (true) or resume (false) recording.</param>
    /// <param name="exit">Quit CHC; recording stops until the next sign-in.</param>
    public TrayService(Action show, Action<bool> pause, Action exit)
    {
        _show = show;
        _pause = pause;
        _exit = exit;
        _icon = new Forms.NotifyIcon { Icon = LoadIcon(), Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _show();
        };
        BuildMenu();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            BuildMenu();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => BuildMenu();

    private void BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = menu.Items.Add(Loc.T("tray.open"), null, (_, _) => _show());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(Loc.T(_paused ? "tray.resume" : "tray.pause"), null, (_, _) => _pause(!_paused));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("tray.exit"), null, (_, _) => _exit());
        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
        var tip = "CHC · " + Loc.T(_paused ? "tray.paused" : "tray.recording");
        _icon.Text = tip.Length > 63 ? tip[..63] : tip; // Windows limit for tray tooltips
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/chc.ico"))!;
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
