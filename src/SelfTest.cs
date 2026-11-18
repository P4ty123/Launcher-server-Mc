using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MineServer
{
    /// <summary>
    /// Test automatici eseguiti da GitHub Actions su un vero PC Windows.
    ///   --selftest risultati.txt              test rapidi (rete, mod, backup, priorità, interfaccia, aggiornamenti)
    ///   --selftest-e2e Paper risultati.txt    prova completa: installa, avvia, comandi, backup, arresto
    /// Il codice di uscita è 0 se tutto è andato bene.
    /// </summary>
    public static class SelfTest
    {
        static StreamWriter w;
        static int failures, total;

        static void Log(string s)
        {
            lock (typeof(SelfTest))
            {
                w.WriteLine(s);
                w.Flush();
            }
        }

        static void Check(string name, Func<bool> test)
        {
            total++;
            var sw = Stopwatch.StartNew();
            try
            {
                bool ok = test();
                Log((ok ? "[OK]    " : "[ERRORE] ") + name + "  (" + sw.Elapsed.TotalSeconds.ToString("0.0") + " s)");
                if (!ok) failures++;
            }
            catch (Exception ex)
            {
                Log("[ERRORE] " + name + "  ->  " + ex.GetType().Name + ": " + ex.Message);
                failures++;
            }
        }

        static int Finish()
        {
            Log("");
            Log(failures == 0
                ? "RISULTATO: tutti i " + total + " test superati"
                : "RISULTATO: " + failures + " test falliti su " + total);
            return failures == 0 ? 0 : 1;
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "mss-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static Process Dummy()
        {
            var psi = new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            var p = Process.Start(psi);
            // GitHub Actions avvia i processi "sotto il normale": parto sempre da Normale
            p.PriorityClass = ProcessPriorityClass.Normal;
            return p;
        }

        // ================================================================== test rapidi

        public static int RunUnit(string outFile)
        {
            using (w = new StreamWriter(outFile, false, new UTF8Encoding(false)))
            {
                Log("MineServer Manager " + AppInfo.Version + " - test rapidi");
                Log("Windows " + Environment.OSVersion.Version + "   .NET " + Environment.Version +
                    "   CPU " + Environment.ProcessorCount + "   amministratore: " + PriorityManager.IsAdmin() +
                    "   priorità del test: " + PriorityManager.CurrentClassName(Process.GetCurrentProcess().Id));
                Log("");

                Check("Confronto versioni", () =>
                {
                    var c = new VersionComparer();
                    return c.Compare("1.21.10", "1.21.9") > 0 && c.Compare("26.1", "1.21.10") > 0 &&
                           c.Compare("1.0.0", "1.0") == 0 && c.Compare("1.0.1", "1.0.0") > 0;
                });

                foreach (var t in Installer.Types)
                {
                    string tt = t;
                    Check("Elenco versioni " + tt, () =>
                    {
                        var l = Installer.GetVersions(tt);
                        Log("         " + l.Count + " versioni, la più recente: " + (l.Count > 0 ? l[0] : "-"));
                        return l.Count > 0;
                    });
                }

                Check("Versione di Java richiesta", () =>
                {
                    string v = Installer.GetVersions("Vanilla")[0];
                    int j = Installer.JavaMajorFor(v);
                    Log("         Minecraft " + v + " -> Java " + j);
                    return j >= 17;
                });

                Check("Ricerca mod su Modrinth (Fabric)", () =>
                {
                    string v = Installer.GetVersions("Fabric")[0];
                    var r = Installer.Search("lithium", "Fabric", v);
                    Log("         " + r.Count + " risultati per " + v);
                    return r.Count > 0;
                });

                Check("Ricerca plugin su Modrinth (Paper)", () =>
                {
                    string v = Installer.GetVersions("Paper")[0];
                    var r = Installer.Search("chunky", "Paper", v);
                    Log("         " + r.Count + " risultati per " + v);
                    return r.Count > 0;
                });

                Check("Lettura/scrittura server.properties", () =>
                {
                    string d = TempDir();
                    var list = new List<KeyValuePair<string, string>>();
                    list.Add(new KeyValuePair<string, string>("motd", "Ciao è un test"));
                    list.Add(new KeyValuePair<string, string>("view-distance", "7"));
                    Props.Write(d, list);
                    bool ok = Props.Read(d).Count == 2 && Props.Get(d, "motd") == "Ciao è un test" &&
                              Props.Get(d, "view-distance") == "7";
                    Directory.Delete(d, true);
                    return ok;
                });

                Check("Backup e ripristino di un mondo", () =>
                {
                    string d = TempDir();
                    var cfg = new AppConfig();
                    cfg.ServerDir = d;
                    cfg.BackupKeep = 3;
                    Directory.CreateDirectory(Path.Combine(d, "world", "region"));
                    Directory.CreateDirectory(Path.Combine(d, "world_nether"));
                    var data = new byte[300000];
                    new Random(1).NextBytes(data);
                    File.WriteAllBytes(Path.Combine(d, "world", "region", "r.0.0.mca"), data);
                    File.WriteAllText(Path.Combine(d, "world", "level.dat"), "dati");
                    File.WriteAllText(Path.Combine(d, "world", "session.lock"), "x");
                    File.WriteAllText(Path.Combine(d, "world_nether", "level.dat"), "nether");

                    string zip = Backup.Run(cfg, new ServerProcess(cfg), s => Log("         " + s));
                    Directory.Delete(Path.Combine(d, "world"), true);
                    Directory.Delete(Path.Combine(d, "world_nether"), true);
                    Backup.Restore(cfg, zip);
                    bool ok = File.ReadAllBytes(Path.Combine(d, "world", "region", "r.0.0.mca")).SequenceEqual(data) &&
                              File.ReadAllText(Path.Combine(d, "world_nether", "level.dat")) == "nether" &&
                              !File.Exists(Path.Combine(d, "world", "session.lock"));
                    File.Delete(zip);
                    Directory.Delete(d, true);
                    return ok;
                });

                Check("Priorità del server: Alta + disco/RAM prioritari", () =>
                {
                    using (var p = Dummy())
                    {
                        string warn = PriorityManager.ApplyToServer(p.Id, "High", true);
                        string now = PriorityManager.CurrentClassName(p.Id);
                        Log("         priorità ottenuta: " + now + (warn != null ? "  (" + warn + ")" : ""));
                        try { p.Kill(); } catch { }
                        return warn == null && now == "Alta";
                    }
                });

                Check("Priorità del server: Tempo reale (richiede admin)", () =>
                {
                    using (var p = Dummy())
                    {
                        string warn = PriorityManager.ApplyToServer(p.Id, "RealTime", false);
                        string now = PriorityManager.CurrentClassName(p.Id);
                        Log("         priorità ottenuta: " + now);
                        try { p.Kill(); } catch { }
                        return PriorityManager.IsAdmin() ? now == "Tempo reale" : warn != null;
                    }
                });

                Check("Modalità server dedicato: abbassa e ripristina gli altri processi", () =>
                {
                    using (var p = Dummy())
                    {
                        Thread.Sleep(300);
                        var pm = new PriorityManager();
                        pm.Enabled = true;
                        pm.Aggressive = false;
                        pm.ApplyAll(() => -1, false);
                        string lowered = PriorityManager.CurrentClassName(p.Id);
                        Log("         processi abbassati: " + pm.LoweredCount + ", non modificabili: " + pm.SkippedCount +
                            ", processo di prova: " + lowered);
                        pm.Enabled = false;
                        pm.RestoreAll();
                        string restored = PriorityManager.CurrentClassName(p.Id);
                        Log("         dopo il ripristino: " + restored);
                        try { p.Kill(); } catch { }
                        return lowered == "Sotto il normale" && restored == "Normale";
                    }
                });

                Check("Modalità server dedicato: intensità Massima", () =>
                {
                    using (var p = Dummy())
                    {
                        Thread.Sleep(300);
                        var pm = new PriorityManager();
                        pm.Enabled = true;
                        pm.Aggressive = true;
                        pm.ApplyAll(() => -1, false);
                        string lowered = PriorityManager.CurrentClassName(p.Id);
                        pm.Enabled = false;
                        pm.RestoreAll();
                        string restored = PriorityManager.CurrentClassName(p.Id);
                        Log("         processo di prova: " + lowered + " -> " + restored);
                        try { p.Kill(); } catch { }
                        return lowered == "Bassa" && restored == "Normale";
                    }
                });

                Check("Interfaccia grafica (apertura di tutte le schede)", () =>
                {
                    using (var f = new MainForm(false, false))
                    {
                        f.Show();
                        Application.DoEvents();
                        var tabs = f.Controls.OfType<TabControl>().First();
                        for (int i = 0; i < tabs.TabCount; i++)
                        {
                            tabs.SelectedIndex = i;
                            Application.DoEvents();
                        }
                        Log("         " + tabs.TabCount + " schede aperte senza errori");
                        f.Close();
                        Application.DoEvents();
                        return tabs.TabCount == 7;
                    }
                });

                Check("Controllo aggiornamenti su GitHub", () =>
                {
                    try
                    {
                        var u = Updater.CheckLatest();
                        Log("         ultima versione pubblicata: " + u.Version + (u.ExeUrl != null ? " (con exe)" : " (senza exe)"));
                        return true;
                    }
                    catch (WebException ex)
                    {
                        var r = ex.Response as HttpWebResponse;
                        if (r != null && r.StatusCode == HttpStatusCode.NotFound)
                        {
                            Log("         nessuna versione pubblicata ancora (normale al primo giro)");
                            return true;
                        }
                        throw;
                    }
                });

                return Finish();
            }
        }

        // ================================================================== prova completa

        public static int RunE2E(string type, string outFile)
        {
            using (w = new StreamWriter(outFile, false, new UTF8Encoding(false)))
            {
                Log("MineServer Manager " + AppInfo.Version + " - prova completa " + type);
                Log("");
                string dir = Path.Combine(AppConfig.BaseDir, "e2e-" + type.ToLower());
                var cfg = new AppConfig();
                cfg.RamGB = 2;
                cfg.AutoRestart = false;
                string ver = null;

                Check("Scelta della versione più recente", () =>
                {
                    ver = Installer.GetVersions(type)[0];
                    Log("         " + type + " " + ver);
                    return true;
                });
                if (ver == null) return Finish();

                bool installed = false;
                Check("Installazione con Java e ottimizzazioni", () =>
                {
                    var inst = new Installer();
                    inst.Log = s => Log("         " + s);
                    inst.InstallServer(cfg, type, ver, dir, type != "Vanilla", false);
                    installed = true;
                    return File.Exists(cfg.JavaPath);
                });
                if (!installed) return Finish();

                var sp = new ServerProcess(cfg);
                var lines = new List<string>();
                var done = new ManualResetEvent(false);
                sp.Output += line =>
                {
                    lock (lines) lines.Add(line);
                    if (line.Contains("Done (")) done.Set();
                };

                bool started = false;
                Check("Avvio del server (attendo 'Done', massimo 12 minuti)", () =>
                {
                    sp.Start();
                    started = done.WaitOne(TimeSpan.FromMinutes(12));
                    if (!started)
                    {
                        Log("         il server non è partito. Ultime righe del log:");
                        lock (lines) foreach (var l in lines.Skip(Math.Max(0, lines.Count - 60))) Log("           " + l);
                    }
                    return started;
                });

                if (started)
                {
                    Check("Priorità Alta + disco e RAM prioritari sul server", () =>
                    {
                        string warn = PriorityManager.ApplyToServer(sp.Pid, "High", true);
                        Log("         priorità: " + PriorityManager.CurrentClassName(sp.Pid));
                        return warn == null;
                    });

                    Check("Comando dalla console (list)", () =>
                    {
                        var got = new ManualResetEvent(false);
                        Action<string> h = l => { if (l.Contains("players online") || l.Contains("giocatori")) got.Set(); };
                        sp.Output += h;
                        sp.SendCommand("list");
                        bool ok = got.WaitOne(30000);
                        sp.Output -= h;
                        return ok;
                    });

                    Check("Backup a server acceso (save-off / save-all / save-on)", () =>
                    {
                        string zip = Backup.Run(cfg, sp, s => Log("         " + s));
                        long kb = new FileInfo(zip).Length / 1024;
                        Log("         " + Path.GetFileName(zip) + " (" + kb + " KB)");
                        return kb > 10;
                    });

                    Check("Utilizzo risorse", () =>
                    {
                        long ram; double cpu; TimeSpan up;
                        sp.GetStats(out ram, out cpu, out up);
                        Thread.Sleep(2000);
                        sp.GetStats(out ram, out cpu, out up);
                        Log("         RAM " + ram + " MB, CPU " + cpu.ToString("0") + "%, acceso da " + up.TotalSeconds.ToString("0") + " s");
                        return ram > 0;
                    });
                }

                Check("Arresto pulito con salvataggio", () =>
                {
                    sp.Stop();
                    bool ok = !sp.IsRunning;
                    lock (lines) ok = ok && (!started || lines.Any(l => l.Contains("Stopping") || l.Contains("Saving")));
                    return ok;
                });

                Check("Errori nel log del server (solo informativo)", () =>
                {
                    List<string> errs;
                    lock (lines)
                        errs = lines.Where(l => l.Contains("ERROR") || l.Contains("Exception") || l.Contains("FATAL")).ToList();
                    Log("         " + errs.Count + " righe di errore");
                    foreach (var e in errs.Take(40)) Log("           " + e);
                    return true;
                });

                try
                {
                    lock (lines) File.WriteAllLines(Path.Combine(AppConfig.BaseDir, "e2e-server-log.txt"), lines, Encoding.UTF8);
                }
                catch { }
                return Finish();
            }
        }
    }
}
