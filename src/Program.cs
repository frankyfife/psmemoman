global using Region = MemcardManager.Core.Region;
using MemcardManager.UI;

namespace MemcardManager;

internal static class Program
{
    static Icon? _icon;

    public static Icon? AppIcon
    {
        get
        {
            if (_icon != null) return _icon;
            try
            {
                using var s = typeof(Program).Assembly.GetManifestResourceStream("memcard.ico");
                if (s != null) _icon = new Icon(s);
            }
            catch { }
            return _icon;
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Dpi.F = Math.Max(1f, g.DpiX / 96f);
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show("Unerwarteter Fehler:\n" + e.Exception.Message + "\n\nKartendateien wurden nicht verändert, solange nicht gespeichert wurde.",
                "Memory Card Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm(args));
    }
}
