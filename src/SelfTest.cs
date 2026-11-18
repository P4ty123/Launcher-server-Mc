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

        /// <summary>Simula un giocatore che prova a entrare e restituisce il messaggio con cui viene respinto.</summary>
        static string Login(int port, string name)
        {
            using (var c = new System.Net.Sockets.TcpClient("127.0.0.1", port))
            {
                c.ReceiveTimeout = 5000;
                var s = c.GetStream();
                var hs = new MemoryStream();
                McProto.WriteVarInt(hs, 767);
                McProto.WriteString(hs, "localhost");
                hs.WriteByte((byte)(port >> 8));
                hs.WriteByte((byte)(port & 0xFF));
                McProto.WriteVarInt(hs, 2);
                McProto.SendPacket(s, 0x00, hs.ToArray());
                var ls = new MemoryStream();
                McProto.WriteString(ls, name);
                ls.Write(new byte[16], 0, 16);                       // UUID
                McProto.SendPacket(s, 0x00, ls.ToArray());
                int id;
                var body = McProto.ReadPacket(s, 65536, out id);
                return id == 0x00 ? McProto.ReadString(body, 65536) : "(pacchetto " + id + ")";
            }
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
                        return tabs.TabCount == 8;
                    }
                });

                Check("playit.gg: scarica il programma e contatta il servizio", () =>
                {
                    var pa = new PlayitAgent();
                    pa.Log = s => Log("         " + s);
                    pa.EnsureBinary(null);
                    var psi = new ProcessStartInfo(Path.Combine(PlayitAgent.Dir, "playitd.exe"), "--help");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    string help;
                    int code;
                    using (var p = Process.Start(psi))
                    {
                        help = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(20000);
                        code = p.ExitCode;
                    }
                    bool argsOk = help.Contains("--secret") && help.Contains("--socket-path") && help.Contains("--log-path");
                    Log("         playitd.exe --help: codice " + code + ", opzioni attese presenti: " + argsOk);
                    var body = new Dictionary<string, object>();
                    body["code"] = Guid.NewGuid().ToString("N").Substring(0, 10);
                    body["agent_type"] = "self-managed";
                    body["version"] = "MineServer Manager test";
                    string txt = Net.PostString("https://api.playit.gg/claim/setup", Net.ToJson(body), null);
                    Log("         api.playit.gg/claim/setup -> " + txt);
                    return code == 0 && argsOk && J.S(J.P(Net.ParseJson(txt), "status")) == "success";
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
                        if (r != null && (int)r.StatusCode == 403)
                        {
                            Log("         limite di richieste dell'API GitHub raggiunto (normale sui PC condivisi di GitHub)");
                            return true;
                        }
                        if (r != null && r.StatusCode == HttpStatusCode.NotFound)
                        {
                            Log("         nessuna versione pubblicata ancora (normale al primo giro)");
                            return true;
                        }
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (!ex.Message.StartsWith("Nessuna versione")) throw;
                        Log("         nessuna versione pubblicata ancora (normale al primo giro)");
                        return true;
                    }
                });

                Check("Pausa automatica: lista server, risveglio Java e Bedrock", () =>
                {
                    var wl = new WakeListener();
                    string woke = null;
                    var ev = new ManualResetEvent(false);
                    wl.WakeRequested = who => { woke = who; ev.Set(); };
                    wl.CanWake = n => n != "Intruso";
                    wl.Start(25599, 19199);
                    try
                    {
                        int online = McProto.QueryPlayers(25599, 5000);
                        Log("         lista server: " + online + " giocatori");

                        // Bedrock: Unconnected Ping -> Pong
                        bool pong;
                        using (var u = new System.Net.Sockets.UdpClient())
                        {
                            u.Client.ReceiveTimeout = 5000;
                            var ping = new byte[33];
                            ping[0] = 0x01;
                            u.Send(ping, ping.Length, "127.0.0.1", 19199);
                            var from = new IPEndPoint(IPAddress.Any, 0);
                            var r = u.Receive(ref from);
                            pong = r.Length > 35 && r[0] == 0x1C && Encoding.UTF8.GetString(r, 35, r.Length - 35).StartsWith("MCPE;");
                        }
                        Log("         risposta Bedrock: " + pong);

                        string kickIntruso = Login(25599, "Intruso");
                        bool notWoken = !ev.WaitOne(1000);
                        string kick = Login(25599, "Tester");
                        bool woken = ev.WaitOne(5000);
                        Log("         non in whitelist: " + kickIntruso);
                        Log("         messaggio al giocatore: " + kick + "   risvegliato da: " + woke);
                        return online == 0 && pong && notWoken && woken && woke == "Tester" &&
                               kick.Contains("accendendo") && kickIntruso.Contains("whitelist");
                    }
                    finally { wl.Stop(); }
                });

                Check("Piani energetici (Prestazioni eccellenti / Risparmio di energia)", () =>
                {
                    string ini = Path.Combine(AppConfig.BaseDir, "config.ini");
                    bool hadIni = File.Exists(ini);
                    string before = PowerPlan.Active();
                    var c = new AppConfig();
                    try
                    {
                        Action<string> lg = s => Log("         " + s);
                        string perf = PowerPlan.EnsurePerformance(c, lg);
                        string saver = PowerPlan.EnsureSaver(c, lg);
                        Log("         prima: " + PowerPlan.NameOf(before) + "   acceso: " + PowerPlan.NameOf(perf) + "   spento: " + PowerPlan.NameOf(saver));
                        bool ok = perf != null && saver != null;
                        if (ok) ok = PowerPlan.SetActive(perf) && PowerPlan.Active() == perf;
                        if (ok) ok = PowerPlan.SetActive(saver) && PowerPlan.Active() == saver;
                        // la seconda volta deve riusare gli stessi piani, senza crearne altri
                        ok = ok && PowerPlan.EnsurePerformance(c, lg) == perf;
                        return ok;
                    }
                    finally
                    {
                        if (before != null) PowerPlan.SetActive(before);
                        if (!hadIni) try { File.Delete(ini); } catch { }
                    }
                });

                return Finish();
            }
        }

        // ================================================================== prova completa

        /// <summary>
        /// Prova completa. versions: vuoto = la più recente; "1.20" = tutte le 1.20.x (e la 1.20); "1.20.1" = solo quella.
        /// </summary>
        public static int RunE2E(string type, string outFile, string versions)
        {
            using (w = new StreamWriter(outFile, false, new UTF8Encoding(false)))
            {
                Log("MineServer Manager " + AppInfo.Version + " - prova completa " + type + " " + (versions ?? "(ultima versione)"));
                Log("");
                var toTest = new List<string>();
                Check("Elenco delle versioni da provare", () =>
                {
                    var all = Installer.GetVersions(type);
                    if (string.IsNullOrEmpty(versions)) toTest.Add(all[0]);
                    else
                        foreach (var v in all)
                            if (v == versions || v.StartsWith(versions + ".")) toTest.Add(v);
                    toTest.Reverse();
                    Log("         " + string.Join(", ", toTest));
                    return toTest.Count > 0;
                });

                var results = new List<string>();
                foreach (var ver in toTest)
                {
                    Log("");
                    Log("================ " + type + " " + ver + " ================");
                    int before = failures;
                    try { E2EOne(type, ver); }
                    catch (Exception ex) { Log("[ERRORE] " + ver + "  ->  " + ex.Message); failures++; }
                    results.Add((failures == before ? "  OK      " : "  PROBLEMI ") + type + " " + ver);
                }

                Log("");
                Log("RIEPILOGO");
                foreach (var r in results) Log(r);
                return Finish();
            }
        }

        static void E2EOne(string type, string ver)
        {
            string dir = Path.Combine(AppConfig.BaseDir, "e2e-" + type.ToLower() + "-" + ver);
            var cfg = new AppConfig();
            cfg.RamGB = 2;
            cfg.AutoRestart = false;
            bool installed = false;
            Check(ver + "  Installazione con Java e ottimizzazioni", () =>
            {
                var inst = new Installer();
                inst.Log = s => Log("         " + s);
                inst.InstallServer(cfg, type, ver, dir, type != "Vanilla", false);
                installed = true;
                return File.Exists(cfg.JavaPath);
            });
            if (!installed) return;

            var sp = new ServerProcess(cfg);
            var lines = new List<string>();
            var done = new ManualResetEvent(false);
            sp.Output += line =>
            {
                lock (lines) lines.Add(line);
                if (line.Contains("Done (")) done.Set();
            };

            bool started = false;
            Check(ver + "  Avvio del server (attendo 'Done', massimo 12 minuti)", () =>
            {
                sp.Start();
                var sw = Stopwatch.StartNew();
                while (!started && sw.Elapsed.TotalMinutes < 12)
                {
                    started = done.WaitOne(1000);
                    if (!started && !sp.IsRunning) { Log("         il server si è chiuso da solo durante l'avvio"); break; }
                }
                if (!started)
                {
                    Log("         il server non è partito. Ultime righe del log:");
                    lock (lines) foreach (var l in lines.Skip(Math.Max(0, lines.Count - 60))) Log("           " + l);
                }
                return started;
            });

            if (started)
            {
                Check(ver + "  Priorità Alta + disco e RAM prioritari sul server", () =>
                {
                    string warn = PriorityManager.ApplyToServer(sp.Pid, "High", true);
                    Log("         priorità: " + PriorityManager.CurrentClassName(sp.Pid));
                    return warn == null;
                });

                Check(ver + "  Comando dalla console (list)", () =>
                {
                    var got = new ManualResetEvent(false);
                    Action<string> h = l => { if (l.Contains("players online") || l.Contains("giocatori")) got.Set(); };
                    sp.Output += h;
                    sp.SendCommand("list");
                    bool ok = got.WaitOne(30000);
                    sp.Output -= h;
                    return ok;
                });

                Check(ver + "  Giocatori online letti come nella lista server (per la pausa automatica)", () =>
                {
                    int port;
                    if (!int.TryParse(Props.Get(cfg.ServerDir, "server-port"), out port)) port = 25565;
                    int n = McProto.QueryPlayers(port, 10000);
                    Log("         porta " + port + ": " + n + " giocatori online");
                    return n == 0;
                });

                Check(ver + "  Backup a server acceso (save-off / save-all / save-on)", () =>
                {
                    string zip = Backup.Run(cfg, sp, s => Log("         " + s));
                    long kb = new FileInfo(zip).Length / 1024;
                    Log("         " + Path.GetFileName(zip) + " (" + kb + " KB)");
                    return kb > 10;
                });

                Check(ver + "  Utilizzo risorse", () =>
                {
                    long ram; double cpu; TimeSpan up;
                    sp.GetStats(out ram, out cpu, out up);
                    Thread.Sleep(2000);
                    sp.GetStats(out ram, out cpu, out up);
                    Log("         RAM " + ram + " MB, CPU " + cpu.ToString("0") + "%, acceso da " + up.TotalSeconds.ToString("0") + " s");
                    return ram > 0;
                });
            }

            Check(ver + "  Arresto pulito con salvataggio", () =>
            {
                sp.Stop();
                bool ok = !sp.IsRunning;
                lock (lines) ok = ok && (!started || lines.Any(l => l.Contains("Stopping") || l.Contains("Saving")));
                return ok;
            });

            Check(ver + "  Errori nel log del server (solo informativo)", () =>
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
                lock (lines) File.WriteAllLines(Path.Combine(AppConfig.BaseDir, "e2e-log-" + type.ToLower() + "-" + ver + ".txt"), lines, Encoding.UTF8);
            }
            catch { }
        }
    }
}
