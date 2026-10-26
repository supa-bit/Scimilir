// Only in the Windows app (see Scimilir.csproj): the small Scimilir icon by the
// clock, which replaces the black window. Click it to open Scimilir; right-click
// it to quit.
using System.Drawing;
using System.Windows.Forms;

namespace Scimilir;

public static class TrayIcon
{
    public static void Start(string url, IHostApplicationLifetime lifetime, bool firstRunTip)
    {
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            var iconFile = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            using var icon = new NotifyIcon
            {
                Icon = File.Exists(iconFile) ? new Icon(iconFile) : SystemIcons.Application,
                Text = "Scimilir",
                Visible = true,
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Scimilir", null, (_, _) => Browser.Open(url)).Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit Scimilir", null, (_, _) =>
            {
                icon.Visible = false;
                lifetime.StopApplication();
            });
            icon.ContextMenuStrip = menu;
            icon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) Browser.Open(url);
            };
            lifetime.ApplicationStopping.Register(() => icon.Visible = false);
            if (firstRunTip)
                icon.ShowBalloonTip(8000, "Scimilir is running",
                    "Click the Scimilir icon by the clock to open it. Right-click it to quit.", ToolTipIcon.Info);
            Application.Run();
        })
        {
            // Ends with the app, and needs its own "single-threaded apartment", as Windows menus do.
            IsBackground = true,
            Name = "Tray icon",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Shows a problem in a message box, since the Windows app has no window to print it in.</summary>
    public static void ShowError(string message) =>
        MessageBox.Show(message, "Scimilir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
