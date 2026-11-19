using System;
using System.Diagnostics;
using System.IO;
using System.Net;
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
            object rel;
            try { rel = Net.GetJson("https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest"); }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                // 403/429 = limite di richieste dell'API di GitHub: uso la pagina delle release, che non ha limiti
                if (r != null && ((int)r.StatusCode == 403 || (int)r.StatusCode == 429)) return CheckLatestNoApi();
                throw;
            }
            return Parse(rel);
        }

        /// <summary>Senza API: github.com/.../releases/latest reindirizza a .../releases/tag/vX.Y.Z</summary>
        static UpdateInfo CheckLatestNoApi()
        {
            var req = (HttpWebRequest)WebRequest.Create(AppInfo.RepoUrl + "/releases/latest");
            req.UserAgent = Net.UA;
            req.AllowAutoRedirect = false;
            req.Method = "HEAD";
            req.Timeout = 30000;
            string loc;
            using (var resp = (HttpWebResponse)req.GetResponse()) loc = resp.Headers["Location"] ?? "";
            int i = loc.IndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
            if (i < 0) throw new Exception("Nessuna versione pubblicata.");
            string tag = Uri.UnescapeDataString(loc.Substring(i + "/releases/tag/".Length));
            var u = new UpdateInfo();
            u.Version = tag.TrimStart('v', 'V');
            u.Notes = "";
            u.PageUrl = loc;
            string dl = AppInfo.RepoUrl + "/releases/download/" + tag + "/";
            u.ExeUrl = dl + "MineServerManager.exe";
            u.SetupUrl = dl + "MineServerManager-Setup.exe";
            return u;
        }

        static UpdateInfo Parse(object rel)
        {
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

        /// <summary>Ricorda quali server riaccendere dopo l'aggiornamento/riavvio (null o vuoto = nessuno).</summary>
        public static void SetStartServerFlag(string ids)
        {
            try
            {
                if (!string.IsNullOrEmpty(ids)) File.WriteAllText(FlagFile, "ids:" + ids);
                else if (File.Exists(FlagFile)) File.Delete(FlagFile);
            }
            catch { }
        }

        /// <summary>
        /// Server da riaccendere (vale 10 minuti): "1,3"; "*" se il flag l'ha scritto una versione vecchia
        /// (prima della 1.4 c'era un solo server); null se nessuno.
        /// </summary>
        public static string ConsumeStartServerFlag()
        {
            try
            {
                if (!File.Exists(FlagFile)) return null;
                bool fresh = (DateTime.Now - File.GetLastWriteTime(FlagFile)).TotalMinutes < 10;
                string txt = File.ReadAllText(FlagFile).Trim();
                File.Delete(FlagFile);
                if (!fresh) return null;
                return txt.StartsWith("ids:") ? txt.Substring(4) : "*";
            }
            catch { return null; }
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
