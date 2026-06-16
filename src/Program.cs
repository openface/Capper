using System.Threading;
using System.Windows.Forms;

namespace Clipfoo;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "Clipfoo_SingleInstance_4b1d9f0e", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Clipfoo is already running — look for its icon in the system tray.",
                "Clipfoo", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayApplicationContext());
    }
}
