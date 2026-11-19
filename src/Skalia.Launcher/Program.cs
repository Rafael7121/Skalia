using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Skalia.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            if (TryApplyUpdate())
            {
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        /// <summary>
        /// Atualizacao sem precisar fechar o launcher: uma versao nova chega como
        /// SKALIA.new.exe. Ao abrir, o SKALIA troca o proprio executavel e reinicia.
        /// </summary>
        private static bool TryApplyUpdate()
        {
            try
            {
                var current = Process.GetCurrentProcess().MainModule.FileName;
                var dir = Path.GetDirectoryName(current);
                var pending = Path.Combine(dir, "SKALIA.new.exe");
                var old = Path.Combine(dir, "SKALIA.old.exe");
                if (string.Equals(Path.GetFileName(current), "SKALIA.new.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                for (var i = 0; i < 10 && File.Exists(old); i++)
                {
                    try
                    {
                        File.Delete(old);
                    }
                    catch
                    {
                        Thread.Sleep(300);
                    }
                }
                if (!File.Exists(pending))
                {
                    return false;
                }
                if (File.Exists(old))
                {
                    return false;
                }
                File.Move(current, old);
                File.Move(pending, current);
                Process.Start(new ProcessStartInfo { FileName = current, WorkingDirectory = dir, UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
