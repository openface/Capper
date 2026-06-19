using System.Threading;
using System.Windows.Forms;
using Velopack;

namespace Capper;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Must run first: during install/update/uninstall Velopack relaunches the exe with special
        // arguments, handles them here, and exits before any UI (or the single-instance mutex) starts.
        VelopackApp.Build().Run();

        using var mutex = new Mutex(true, "Capper_SingleInstance_4b1d9f0e", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Capper is already running — look for its icon in the system tray.",
                "Capper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayApplicationContext());
    }
}
