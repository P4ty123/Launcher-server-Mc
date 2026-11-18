using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace MineServer
{
    /// <summary>Lettura/scrittura di server.properties.</summary>
    public static class Props
    {
        public static List<KeyValuePair<string, string>> Read(string serverDir)
        {
            var list = new List<KeyValuePair<string, string>>();
            string f = Path.Combine(serverDir, "server.properties");
            if (!File.Exists(f)) return list;
            foreach (var line in File.ReadAllLines(f, Encoding.UTF8))
            {
                if (line.TrimStart().StartsWith("#") || line.Trim() == "") continue;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                list.Add(new KeyValuePair<string, string>(line.Substring(0, i).Trim(), line.Substring(i + 1)));
            }
            return list;
        }

        public static string Get(string serverDir, string key)
        {
            foreach (var kv in Read(serverDir))
                if (kv.Key == key) return kv.Value.Trim();
            return null;
        }

        public static void Write(string serverDir, List<KeyValuePair<string, string>> values)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#Minecraft server properties");
            sb.AppendLine("#Modificato da MineServer Manager il " + DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
            foreach (var kv in values) sb.AppendLine(kv.Key + "=" + kv.Value);
            File.WriteAllText(Path.Combine(serverDir, "server.properties"), sb.ToString(), new UTF8Encoding(false));
        }
    }

    public static class Backup
    {
        public static string[] WorldDirs(string serverDir)
        {
            string level = Props.Get(serverDir, "level-name");
            if (string.IsNullOrEmpty(level)) level = "world";
            var l = new List<string>();
            foreach (var n in new[] { level, level + "_nether", level + "_the_end" })
            {
                string p = Path.Combine(serverDir, n);
                if (Directory.Exists(p)) l.Add(p);
            }
            return l.ToArray();
        }

        /// <summary>Crea un backup .zip dei mondi. Se il server è acceso usa save-off/save-all per un backup coerente.</summary>
        public static string Run(AppConfig cfg, ServerProcess sp, Action<string> log)
        {
            var dirs = WorldDirs(cfg.ServerDir);
            if (dirs.Length == 0) throw new Exception("Nessun mondo da salvare (avvia il server almeno una volta).");

            bool running = sp.IsRunning;
            if (running)
            {
                var saved = new ManualResetEvent(false);
                Action<string> h = line => { if (line.Contains("Saved the game")) saved.Set(); };
                sp.Output += h;
                sp.SendCommand("say Backup in corso, potrebbe esserci un attimo di lag...");
                sp.SendCommand("save-off");
                sp.SendCommand("save-all flush");
                saved.WaitOne(90000);
                sp.Output -= h;
                Thread.Sleep(1500);
            }

            try
            {
                Directory.CreateDirectory(cfg.BackupDir);
                string path = Path.Combine(cfg.BackupDir, "backup_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".zip");
                using (var fs = new FileStream(path, FileMode.Create))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var d in dirs)
                    {
                        string root = Path.GetFileName(d);
                        foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories))
                        {
                            if (Path.GetFileName(f).Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                            string rel = root + "/" + f.Substring(d.Length).TrimStart('\\').Replace('\\', '/');
                            try
                            {
                                using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                {
                                    var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
                                    using (var es = entry.Open()) src.CopyTo(es);
                                }
                            }
                            catch (IOException ex) { log("saltato " + rel + ": " + ex.Message); }
                        }
                    }
                }
                Rotate(cfg);
                return path;
            }
            finally
            {
                if (running)
                {
                    sp.SendCommand("save-on");
                    sp.SendCommand("say Backup completato.");
                }
            }
        }

        static void Rotate(AppConfig cfg)
        {
            int keep = Math.Max(1, cfg.BackupKeep);
            foreach (var f in Directory.GetFiles(cfg.BackupDir, "backup_*.zip").OrderByDescending(x => x).Skip(keep))
            {
                try { File.Delete(f); } catch { }
            }
        }

        public static string[] List(AppConfig cfg)
        {
            if (!Directory.Exists(cfg.BackupDir)) return new string[0];
            return Directory.GetFiles(cfg.BackupDir, "backup_*.zip").OrderByDescending(x => x).ToArray();
        }

        /// <summary>Ripristina un backup. Il mondo attuale viene rinominato, non cancellato.</summary>
        public static void Restore(AppConfig cfg, string zipPath)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
            List<string> roots;
            using (var z = ZipFile.OpenRead(zipPath))
                roots = z.Entries.Select(e => e.FullName.Split('/')[0]).Distinct().ToList();
            foreach (var r in roots)
            {
                string p = Path.Combine(cfg.ServerDir, r);
                if (Directory.Exists(p)) Directory.Move(p, p + "_prima_del_ripristino_" + stamp);
            }
            ZipFile.ExtractToDirectory(zipPath, cfg.ServerDir);
        }
    }
}
