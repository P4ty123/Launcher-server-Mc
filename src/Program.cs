using System;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace MineServer
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Test automatici (usati da GitHub Actions): nessuna finestra, risultato su file
            if (args.Length >= 2 && args[0] == "--selftest")
            {
                InitNet();
                Application.EnableVisualStyles();
                Environment.Exit(SelfTest.RunUnit(args[1]));
            }
            if (args.Length >= 2 && args[0] == "--screenshots")
            {
                InitNet();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var f = new MainForm(false, false);
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(0, 0);
                f.Show();
                f.ScreenshotTour(args[1]);
                Environment.Exit(0);
            }
            if (args.Length >= 3 && args[0] == "--selftest-e2e")
            {
                InitNet();
                Environment.Exit(SelfTest.RunE2E(args[1], args[2], args.Length >= 4 ? args[3] : null));
            }

            bool autostart = false, wait = false, startServer = false;
            foreach (var a in args)
            {
                if (a.Equals("--updated", StringComparison.OrdinalIgnoreCase)) Updater.JustUpdated = true;
                if (a.Equals("--autostart", StringComparison.OrdinalIgnoreCase)) autostart = true;
                if (a.Equals("--wait", StringComparison.OrdinalIgnoreCase)) wait = true;
                if (a.Equals("--startserver", StringComparison.OrdinalIgnoreCase)) startServer = true;
            }

            Mutex mutex = null;
            bool owned = false;
            try
            {
                mutex = new Mutex(true, "MineServerManager_SingleInstance", out owned);
                if (!owned && wait)
                {
                    // Riavvio come amministratore: aspetto che la vecchia finestra si chiuda
                    try { owned = mutex.WaitOne(20000); }
                    catch (AbandonedMutexException) { owned = true; }
                }
            }
            catch (UnauthorizedAccessException) { owned = false; }

            if (!owned)
            {
                MessageBox.Show("MineServer Manager è già aperto (guarda tra le icone vicino all'orologio).",
                    "MineServer Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            InitNet();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(autostart, startServer));
            GC.KeepAlive(mutex);
        }

        static void InitNet()
        {
            // TLS 1.2 (e 1.3 se disponibile) per scaricare da Mojang, PaperMC, Fabric, Forge, Modrinth, GitHub
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
            ServicePointManager.DefaultConnectionLimit = 8;
            // Se siamo amministratori: privilegi per priorità "Tempo reale", disco alto e servizi di sistema
            try { PriorityManager.EnablePrivileges(); } catch { }
        }
    }
}
