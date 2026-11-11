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
        MemcardManager.Core.L.Init(AppSettings.Load().Language);
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(MemcardManager.Core.L.T("Unerwarteter Fehler:\n", "Unexpected error:\n") + e.Exception.Message
                + MemcardManager.Core.L.T("\n\nKartendateien wurden nicht verändert, solange nicht gespeichert wurde.", "\n\nCard files have not been changed unless they were saved."),
                "Memory Card Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm(args));
    }
}
