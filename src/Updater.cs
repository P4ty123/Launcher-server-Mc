using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace MineServer
{
    public class UpdateInfo
    {
        public string Version;
        public string Notes;
        public string PageUrl;
        public string ExeUrl;
        public string ExeDigest;
        public string SetupUrl;
        public string SetupDigest;
    }

    /// <summary>
    /// Aggiornamenti da GitHub Releases. Il nuovo .exe viene scaricato, verificato (SHA-256 fornito da GitHub)
    /// e sostituito a quello in uso; se la cartella è protetta si usa l'installer.
    /// </summary>
    public static class Updater
    {
        public static bool JustUpdated;

        static string FlagFile { get { return Path.Combine(Path.GetTempPath(), "mineserver_avvia_server.flag"); } }

        public static UpdateInfo CheckLatest()
        {
            var rel = Net.GetJson("https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest");
            var u = new UpdateInfo();
            u.Version = (J.S(J.P(rel, "tag_name")) ?? "").TrimStart('v', 'V');
            u.Notes = J.S(J.P(rel, "body")) ?? "";
            u.PageUrl = J.S(J.P(rel, "html_url"));
            foreach (var a in J.A(J.P(rel, "assets")) ?? new object[0])
            {
                string name = J.S(J.P(a, "name")) ?? "";
                string url = J.S(J.P(a, "browser_download_url"));
                string digest = J.S(J.P(a, "digest"));
                if (name.Equals("MineServerManager.exe", StringComparison.OrdinalIgnoreCase))
                {
                    u.ExeUrl = url;
                    u.ExeDigest = digest;
                }
                else if (name.StartsWith("MineServerManager-Setup", StringComparison.OrdinalIgnoreCase) &&
                         name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    u.SetupUrl = url;
                    u.SetupDigest = digest;
                }
            }
            return u;
        }

        public static bool IsNewer(string version)
        {
            return !string.IsNullOrEmpty(version) && new VersionComparer().Compare(version, AppInfo.Version) > 0;
        }

        static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        static void Verify(string file, string digest)
        {
            var head = new byte[2];
            using (var fs = File.OpenRead(file)) fs.Read(head, 0, 2);
            if (head[0] != 'M' || head[1] != 'Z')
            {
                File.Delete(file);
                throw new Exception("Il file scaricato non è un programma valido.");
            }
            if (!string.IsNullOrEmpty(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                string want = digest.Substring(7).ToLowerInvariant();
                if (Sha256(file) != want)
                {
                    File.Delete(file);
                    throw new Exception("Verifica SHA-256 non superata: il download è corrotto. Riprova.");
                }
            }
        }

        static string Download(string url, string digest, string fileName, Action<int> progress)
        {
            string tmp = Path.Combine(Path.GetTempPath(), fileName);
            Net.Download(url, tmp, (d, t) => { if (progress != null) progress(t > 0 ? (int)(d * 100 / t) : -1); });
            Verify(tmp, digest);
            return tmp;
        }

        public static string DownloadExe(UpdateInfo u, Action<int> progress)
        {
            return Download(u.ExeUrl, u.ExeDigest, "MineServerManager.new.exe", progress);
        }

        public static string DownloadSetup(UpdateInfo u, Action<int> progress)
        {
            return Download(u.SetupUrl, u.SetupDigest, "MineServerManager-Setup-update.exe", progress);
        }

        /// <summary>Sostituisce l'exe in uso: Windows permette di rinominare un programma aperto.</summary>
        public static void Swap(string newExe)
        {
            string exe = Application.ExecutablePath;
            string old = Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + ".old.exe");
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Copy(newExe, exe); }
            catch
            {
                File.Move(old, exe);
                throw;
            }
            try { File.Delete(newExe); } catch { }
        }

        public static void StartNew()
        {
            Process.Start(Application.ExecutablePath, "--wait --updated");
        }

        public static void RunSetup(string setupPath)
        {
            var psi = new ProcessStartInfo(setupPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Process.Start(psi);
        }

        public static void SetStartServerFlag(bool on)
        {
            try
            {
                if (on) File.WriteAllText(FlagFile, DateTime.Now.ToString("o"));
                else if (File.Exists(FlagFile)) File.Delete(FlagFile);
            }
            catch { }
        }

        /// <summary>True se prima dell'aggiornamento il server era acceso (vale 10 minuti).</summary>
        public static bool ConsumeStartServerFlag()
        {
            try
            {
                if (!File.Exists(FlagFile)) return false;
                bool fresh = (DateTime.Now - File.GetLastWriteTime(FlagFile)).TotalMinutes < 10;
                File.Delete(FlagFile);
                return fresh;
            }
            catch { return false; }
        }

        /// <summary>Cancella l'exe vecchio rimasto dopo un aggiornamento.</summary>
        public static void CleanupOld()
        {
            string exe = Application.ExecutablePath;
            string old = Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + ".old.exe");
            for (int i = 0; i < 10 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { Thread.Sleep(1000); }
            }
        }
    }
}
