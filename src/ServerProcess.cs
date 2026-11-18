using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace MineServer
{
    public class ServerProcess
    {
        readonly AppConfig cfg;
        Process proc;
        StreamWriter stdin;
        readonly List<DateTime> crashes = new List<DateTime>();
        TimeSpan lastCpu;
        DateTime lastCpuAt = DateTime.MinValue;

        public event Action<string> Output;
        public event Action StateChanged;
        public event Action<int> Started;
        public volatile bool StopRequested;
        public DateTime StartedAt;

        public ServerProcess(AppConfig c) { cfg = c; }

        public int Pid
        {
            get
            {
                try { return proc != null && !proc.HasExited ? proc.Id : -1; }
                catch { return -1; }
            }
        }

        public bool IsRunning
        {
            get
            {
                try { return proc != null && !proc.HasExited; }
                catch { return false; }
            }
        }

        /// <summary>Flag di Aikar (G1GC) adatti a server con poca CPU.</summary>
        public static string JvmFlags(int ramGB)
        {
            int mb = ramGB * 1024;
            return "-Xms" + mb + "M -Xmx" + mb + "M" +
                   " -XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200" +
                   " -XX:+UnlockExperimentalVMOptions -XX:+DisableExplicitGC -XX:+AlwaysPreTouch" +
                   " -XX:G1NewSizePercent=30 -XX:G1MaxNewSizePercent=40 -XX:G1HeapRegionSize=8M" +
                   " -XX:G1ReservePercent=20 -XX:G1HeapWastePercent=5 -XX:G1MixedGCCountTarget=4" +
                   " -XX:InitiatingHeapOccupancyPercent=15 -XX:G1MixedGCLiveThresholdPercent=90" +
                   " -XX:SurvivorRatio=32 -XX:+PerfDisableSharedMem -XX:MaxTenuringThreshold=1" +
                   " -Dusing.aikars.flags=https://mcflags.emc.gs -Daikars.new.flags=true" +
                   " -Dfile.encoding=UTF-8 -Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8";
        }

        void Raise(string s)
        {
            var h = Output;
            if (h != null) h(s);
        }

        void FireState()
        {
            var h = StateChanged;
            if (h != null) h();
        }

        public void Start()
        {
            if (IsRunning) return;
            if (!cfg.Installed) throw new Exception("Il server non è ancora installato.");
            if (!File.Exists(cfg.JavaPath)) throw new Exception("Java non trovato: reinstalla il server dalla scheda 1.");
            if (!Directory.Exists(cfg.ServerDir)) throw new Exception("Cartella del server non trovata.");

            string flags = JvmFlags(cfg.RamGB);
            string args;
            if (cfg.LaunchMode == "args")
            {
                // Forge moderno: i parametri JVM vanno in user_jvm_args.txt
                File.WriteAllText(Path.Combine(cfg.ServerDir, "user_jvm_args.txt"),
                    "# Generato da MineServer Manager\r\n" + flags.Replace(" -", "\r\n-") + "\r\n");
                args = "@user_jvm_args.txt @" + cfg.LaunchTarget + " nogui";
            }
            else
            {
                args = flags + " -jar \"" + cfg.LaunchTarget + "\" nogui";
            }

            var psi = new ProcessStartInfo(cfg.JavaPath, args);
            psi.WorkingDirectory = cfg.ServerDir;
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            var p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (s, e) => { if (e.Data != null) Raise(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) Raise(e.Data); };
            p.Exited += OnExited;

            StopRequested = false;
            p.Start();
            proc = p;
            stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
            stdin.AutoFlush = true;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            StartedAt = DateTime.Now;
            lastCpuAt = DateTime.MinValue;
            Raise("[Manager] Server avviato: " + cfg.ServerType + " " + cfg.McVersion + ", RAM " + cfg.RamGB + " GB");
            FireState();
            var st = Started;
            if (st != null)
            {
                try { st(p.Id); } catch (Exception ex) { Raise("[Manager] Priorità non applicata: " + ex.Message); }
            }
        }

        void OnExited(object sender, EventArgs e)
        {
            var p = sender as Process;
            int code = -1;
            try { code = p.ExitCode; } catch { }
            Raise("[Manager] Server fermato (codice " + code + ").");
            FireState();

            if (StopRequested || !cfg.AutoRestart) return;

            lock (crashes)
            {
                crashes.Add(DateTime.Now);
                crashes.RemoveAll(t => (DateTime.Now - t).TotalMinutes > 10);
                if (crashes.Count >= 3)
                {
                    Raise("[Manager] 3 crash in 10 minuti: riavvio automatico sospeso. Controlla il log qui sopra.");
                    return;
                }
            }
            Raise("[Manager] Crash rilevato, riavvio tra 10 secondi...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(10000);
                try { if (!StopRequested && !IsRunning) Start(); }
                catch (Exception ex) { Raise("[Manager] Riavvio non riuscito: " + ex.Message); }
            });
        }

        public bool SendCommand(string cmd)
        {
            if (!IsRunning || stdin == null) return false;
            if (cmd.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) StopRequested = true;
            try { stdin.WriteLine(cmd); return true; }
            catch { return false; }
        }

        public bool WaitForExit(int ms)
        {
            try { return proc == null || proc.WaitForExit(ms); }
            catch { return true; }
        }

        /// <summary>Ferma il server in modo pulito (bloccante: chiamare da un thread in background).</summary>
        public void Stop()
        {
            StopRequested = true;
            if (!IsRunning) return;
            Raise("[Manager] Arresto del server (salvataggio del mondo)...");
            SendCommand("stop");
            if (!WaitForExit(120000))
            {
                Raise("[Manager] Il server non risponde, lo chiudo forzatamente.");
                try { proc.Kill(); } catch { }
                WaitForExit(10000);
            }
        }

        public void Restart()
        {
            Stop();
            Thread.Sleep(3000);
            Start();
        }

        public bool GetStats(out long ramMB, out double cpu, out TimeSpan uptime)
        {
            ramMB = 0; cpu = 0; uptime = TimeSpan.Zero;
            if (!IsRunning) return false;
            try
            {
                proc.Refresh();
                ramMB = proc.WorkingSet64 / 1048576;
                var now = DateTime.UtcNow;
                var t = proc.TotalProcessorTime;
                if (lastCpuAt != DateTime.MinValue)
                {
                    double el = (now - lastCpuAt).TotalMilliseconds;
                    if (el > 0) cpu = (t - lastCpu).TotalMilliseconds / el / Environment.ProcessorCount * 100.0;
                }
                lastCpu = t;
                lastCpuAt = now;
                uptime = DateTime.Now - StartedAt;
                return true;
            }
            catch { return false; }
        }
    }
}
