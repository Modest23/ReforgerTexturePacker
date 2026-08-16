using System;
using System.Windows.Forms;

namespace ReforgerTexturePacker
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.SetMode(Settings.LoadDark());
            // A file dragged onto the exe arrives as argv[0] and auto-fills the set.
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
    }
}
