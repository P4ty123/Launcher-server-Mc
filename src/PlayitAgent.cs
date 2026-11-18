using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MineServer
{
    /// <summary>
    /// Accesso da internet senza aprire porte, tramite playit.gg.
    /// 1) scarica l'agent ufficiale (playitd.exe), 2) collega il programma all'account con un solo clic nel browser,
    /// 3) crea da solo il tunnel "Minecraft Java" (e Bedrock se serve), 4) tiene acceso l'agent e restituisce l'indirizzo.
    /// </summary>
    public class PlayitAgent
    {
        const string Api = "https://api.playit.gg";
        const string ReleasesApi = "https://api.github.com/repos/playit-cloud/playit-agent/releases/latest";
        const string FallbackExe = "https://github.com/playit-cloud/playit-agent/releases/latest/download/playitd-windows-x86_64.exe";
        const string Pipe = @"\\.\pipe\mineserver-playitd";

        public Action<string> Log = delegate { };
        Process proc;
        volatile bool stopping;
        readonly List<DateTime> crashes = new List<DateTime>();

        public static string Dir { get { return Path.Combine(AppConfig.BaseDir, "playit"); } }
        static string ExePath { get { return Path.Combine(Dir, "playitd.exe"); } }
        static string SecretFile { get { return Path.Combine(Dir, "secret.txt"); } }
        static string AddressFile { get { return Path.Combine(Dir, "indirizzo.txt"); } }
        static string LogFile { get { return Path.Combine(Dir, "playit.log"); } }
        static string VersionFile { get { return Path.Combine(Dir, "versione.txt"); } }

        public static bool IsSetUp { get { return File.Exists(SecretFile) && File.Exists(ExePath); } }

        public static string SavedAddress
        {
            get
            {
                try { return File.Exists(AddressFile) ? File.ReadAllText(AddressFile).Trim() : null; }
                catch { return null; }
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

        // ------------------------------------------------------------ API playit.gg

        static object Call(string path, object body, string secret)
        {
            string txt = Net.PostString(Api + path, Net.ToJson(body), secret == null ? null : "Agent-Key " + secret.Trim());
            var res = Net.ParseJson(txt);
            string status = J.S(J.P(res, "status"));
            if (status == "success") return J.P(res, "data");
            object data = J.P(res, "data");
            string detail = data is string ? (string)data : Net.ToJson(data);
            throw new PlayitException(status == "fail" ? detail : "errore " + detail, detail);
        }

        // ------------------------------------------------------------ 1. programma playit

        public void EnsureBinary(Action<int> progress)
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(ExePath)) return;
            string url = FallbackExe;
            try
            {
                var rel = Net.GetJson(ReleasesApi);
                string tag = (J.S(J.P(rel, "tag_name")) ?? "").TrimStart('v', 'V');
                if (tag.Length > 0) File.WriteAllText(VersionFile, tag);
                string best = null;
                foreach (var a in J.A(J.P(rel, "assets")) ?? new object[0])
                {
                    string name = (J.S(J.P(a, "name")) ?? "").ToLowerInvariant();
                    if (!name.StartsWith("playitd-windows-x86_64") || !name.EndsWith(".exe")) continue;
                    if (best == null || name.Contains("signed")) best = J.S(J.P(a, "browser_download_url"));
                }
                if (best != null) url = best;
            }
            catch { }
            Log("Scarico il programma ufficiale di playit.gg...");
            Net.Download(url, ExePath, (d, t) => { if (progress != null) progress(t > 0 ? (int)(d * 100 / t) : -1); });
        }

        // ------------------------------------------------------------ 2. collegamento all'account (un clic)

        static string NewClaimCode()
        {
            var b = new byte[5];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Apre il browser sulla pagina di playit.gg e aspetta che l'utente approvi (max 15 minuti).</summary>
        public void Claim(Func<bool> cancelled)
        {
            string code = NewClaimCode();
            string url = "https://playit.gg/claim/" + code;
            // playit.gg si aspetta "playit x.y.z" come il suo programma ufficiale
            string agentVer = null;
            try { if (File.Exists(VersionFile)) agentVer = File.ReadAllText(VersionFile).Trim(); } catch { }
            string version = "playit " + (string.IsNullOrEmpty(agentVer) ? "2.0.2" : agentVer);
            bool opened = false;
            var sw = Stopwatch.StartNew();

            while (true)
            {
                if (cancelled != null && cancelled()) throw new OperationCanceledException("Annullato.");
                if (sw.Elapsed.TotalMinutes > 15) throw new Exception("Tempo scaduto: non è arrivata l'approvazione nel browser.");

                var body = new Dictionary<string, object>();
                body["code"] = code;
                body["agent_type"] = "self-managed";
                body["version"] = version;
                string state;
                try { state = J.S(Call("/claim/setup", body, null)); }
                catch (PlayitException) { throw; }
                catch (Exception ex) { Log("  rete: " + ex.Message + " (riprovo)"); Thread.Sleep(3000); continue; }

                if (state == "UserAccepted") break;
                if (state == "UserRejected") throw new Exception("Collegamento rifiutato nel browser.");
                if (!opened)
                {
                    Log("Si apre il browser: accedi a playit.gg (o crea un account gratuito) e premi il pulsante per approvare.");
                    Log("Se il browser non si apre, vai qui: " + url);
                    try { Process.Start(url); } catch { }
                    opened = true;
                }
                Thread.Sleep(2500);
            }

            Log("Approvato! Ottengo la chiave dell'agent...");
            var ex2 = new Dictionary<string, object>();
            ex2["code"] = code;
            string secret = J.S(J.P(Call("/claim/exchange", ex2, null), "secret_key"));
            if (string.IsNullOrEmpty(secret)) throw new Exception("playit.gg non ha restituito la chiave.");
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SecretFile, secret);
        }

        static string Secret()
        {
            return File.Exists(SecretFile) ? File.ReadAllText(SecretFile).Trim() : null;
        }

        // ------------------------------------------------------------ 3. tunnel Minecraft

        static object RunData(string secret)
        {
            return Call("/agents/rundata", new Dictionary<string, object>(), secret);
        }

        static object FindTunnel(object rundata, string type)
        {
            foreach (var t in J.A(J.P(rundata, "tunnels")) ?? new object[0])
                if (J.S(J.P(t, "tunnel_type")) == type) return t;
            return null;
        }

        static bool HasPending(object rundata)
        {
            var p = J.A(J.P(rundata, "pending"));
            return p != null && p.Length > 0;
        }

        static string AddressOf(object tunnel, bool java)
        {
            string host = J.S(J.P(tunnel, "custom_domain"));
            if (string.IsNullOrEmpty(host)) host = J.S(J.P(tunnel, "assigned_domain"));
            if (string.IsNullOrEmpty(host)) return null;
            // Java: i tunnel Minecraft usano la porta 25565 con instradamento per nome, basta il dominio
            if (java) return host;
            object port = J.P(tunnel, "port", "from");
            return port != null ? host + ":" + J.S(port) : host;
        }

        void CreateTunnel(string secret, string agentId, string type, string portType, int localPort)
        {
            var origin = new Dictionary<string, object>();
            origin["type"] = "agent";
            var data = new Dictionary<string, object>();
            data["agent_id"] = agentId;
            data["local_ip"] = "127.0.0.1";
            data["local_port"] = localPort;
            origin["data"] = data;

            var req = new Dictionary<string, object>();
            req["name"] = type == "minecraft-java" ? "Minecraft Java (MineServer)" : "Minecraft Bedrock (MineServer)";
            req["tunnel_type"] = type;
            req["port_type"] = portType;
            req["port_count"] = 1;
            req["origin"] = origin;
            req["enabled"] = true;
            req["alloc"] = null;
            req["firewall_id"] = null;
            req["proxy_protocol"] = null;
            Call("/tunnels/create", req, secret);
        }

        /// <summary>Crea (se mancano) i tunnel e restituisce l'indirizzo Java da dare agli amici.</summary>
        public string EnsureTunnels(int javaPort, bool bedrock, out string bedrockAddress)
        {
            bedrockAddress = null;
            string secret = Secret();
            if (secret == null) throw new Exception("playit.gg non è ancora collegato.");

            var rd = RunData(secret);
            string agentId = J.S(J.P(rd, "agent_id"));
            string account = J.S(J.P(rd, "account_status"));
            if (account != null && account != "verified" && account != "guest")
                Log("Stato dell'account playit: " + account);

            bool needJava = FindTunnel(rd, "minecraft-java") == null;
            bool needBedrock = bedrock && FindTunnel(rd, "minecraft-bedrock") == null;
            if (needJava || needBedrock)
            {
                // playit.gg conosce la versione dell'agent solo dopo che si è collegato almeno una volta:
                // senza questo passaggio la creazione del tunnel risponde "AgentVersionTooOld".
                if (!IsRunning)
                {
                    Log("Accendo playit e aspetto che si colleghi...");
                    Start();
                    Thread.Sleep(5000);
                }
            }
            if (needJava)
            {
                Log("Creo il tunnel Minecraft Java...");
                CreateWithRetry(secret, agentId, "minecraft-java", "tcp", javaPort, true);
            }
            if (needBedrock)
            {
                Log("Creo il tunnel Minecraft Bedrock (Geyser)...");
                try { CreateWithRetry(secret, agentId, "minecraft-bedrock", "udp", 19132, false); }
                catch (Exception ex) { Log("  Bedrock non creato: " + ex.Message); }
            }

            // playit assegna l'indirizzo in pochi secondi
            for (int i = 0; i < 60; i++)
            {
                rd = RunData(secret);
                var java = FindTunnel(rd, "minecraft-java");
                string addr = java != null ? AddressOf(java, true) : null;
                if (addr != null)
                {
                    var bt = FindTunnel(rd, "minecraft-bedrock");
                    if (bt != null) bedrockAddress = AddressOf(bt, false);
                    File.WriteAllText(AddressFile, addr);
                    return addr;
                }
                Thread.Sleep(2000);
            }
            throw new Exception(HasPending(rd)
                ? "Il tunnel è in preparazione su playit.gg: riprova tra un minuto."
                : "playit.gg non ha ancora assegnato un indirizzo: riprova tra un minuto.");
        }

        /// <summary>Se playit.gg non ha ancora visto l'agent collegato, riprova per circa 2 minuti.</summary>
        void CreateWithRetry(string secret, string agentId, string type, string portType, int localPort, bool java)
        {
            for (int i = 0; ; i++)
            {
                try { CreateTunnel(secret, agentId, type, portType, localPort); return; }
                catch (PlayitException ex)
                {
                    bool tooOld = (ex.Detail ?? "").Contains("AgentVersionTooOld");
                    if (!tooOld || i >= 24) throw Friendly(ex);
                    if (!IsRunning) Start();
                    if (i == 0) Log("playit si sta ancora collegando, riprovo tra qualche secondo...");
                    Thread.Sleep(5000);
                }
            }
        }

        static Exception Friendly(PlayitException ex)
        {
            string d = ex.Detail ?? "";
            if (d.Contains("RequiresVerifiedAccount"))
                return new Exception("playit.gg chiede di verificare l'account: controlla l'e-mail di conferma di playit, poi premi di nuovo il pulsante.");
            if (d.Contains("RequiresPlayitPremium"))
                return new Exception("Hai raggiunto il limite di tunnel gratuiti: cancellane uno vecchio su playit.gg e riprova.");
            if (d.Contains("AgentVersionTooOld"))
                return new Exception("playit.gg non riesce a vedere il programma playit collegato (dice che è troppo vecchio). " +
                                     "Controlla che il PC sia connesso a internet e riprova tra qualche minuto; se non basta, premi 'Scollega account playit' e rifai il collegamento.");
            return new Exception("playit.gg: " + d);
        }

        // ------------------------------------------------------------ 4. agent sempre acceso

        public void Start()
        {
            if (IsRunning) return;
            string secret = Secret();
            if (secret == null || !File.Exists(ExePath)) return;
            stopping = false;
            var psi = new ProcessStartInfo(ExePath,
                "--secret " + secret + " --socket-path \"" + Pipe + "\" --log-path \"" + LogFile + "\"");
            psi.WorkingDirectory = Dir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            var p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (s, e) => { };
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log("playit: " + e.Data); };
            p.Exited += OnExited;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            proc = p;
            Log("playit.gg acceso: i tuoi amici possono collegarsi da internet.");
        }

        void OnExited(object sender, EventArgs e)
        {
            if (stopping) return;
            lock (crashes)
            {
                crashes.Add(DateTime.Now);
                crashes.RemoveAll(t => (DateTime.Now - t).TotalMinutes > 10);
                if (crashes.Count >= 4)
                {
                    Log("playit.gg si è chiuso più volte: riavvio sospeso. Controlla " + LogFile);
                    return;
                }
            }
            Log("playit.gg si è chiuso, lo riavvio tra 5 secondi...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(5000);
                try { if (!stopping) Start(); } catch (Exception ex) { Log("playit: " + ex.Message); }
            });
        }

        public void Stop()
        {
            stopping = true;
            try
            {
                if (IsRunning)
                {
                    proc.Kill();
                    proc.WaitForExit(5000);
                }
            }
            catch { }
        }

        /// <summary>Scollega: ferma l'agent e cancella chiave e indirizzo (i tunnel restano sull'account playit).</summary>
        public void Reset()
        {
            Stop();
            try { File.Delete(SecretFile); } catch { }
            try { File.Delete(AddressFile); } catch { }
        }

        public int Pid
        {
            get
            {
                try { return IsRunning ? proc.Id : -1; }
                catch { return -1; }
            }
        }
    }

    public class PlayitException : Exception
    {
        public string Detail;
        public PlayitException(string message, string detail) : base(message) { Detail = detail; }
    }
}
