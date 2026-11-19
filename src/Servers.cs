using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MineServer
{
    /// <summary>Un server mentre il programma è aperto: il processo, la pausa automatica, il log della console.</summary>
    public class ServerSlot
    {
        public readonly AppConfig Cfg;
        public readonly ServerProcess Proc;
        public readonly WakeListener Wake = new WakeListener();
        public volatile bool Sleeping;              // spento perché vuoto, in ascolto sulle porte
        public volatile bool IdleQueryBusy;
        public volatile bool GreetStart;
        public volatile bool BackupRunning;
        public DateTime EmptySince = DateTime.MinValue;
        public DateTime LastChunky = DateTime.MinValue;
        public int LastPlayers = -1;                // ultimo conteggio letto (-1 = sconosciuto)

        const int MaxHistory = 3000;
        readonly List<string> history = new List<string>();
        readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();

        public ServerSlot(AppConfig cfg)
        {
            Cfg = cfg;
            Proc = new ServerProcess(cfg);
        }

        public bool IsRunning { get { return Proc.IsRunning; } }

        /// <summary>Acceso oppure in pausa: in entrambi i casi "occupa" porte e (in parte) RAM.</summary>
        public bool IsActive { get { return Proc.IsRunning || Sleeping; } }

        public void Add(string line)
        {
            lock (history)
            {
                history.Add(line);
                if (history.Count > MaxHistory) history.RemoveRange(0, history.Count - MaxHistory);
            }
            pending.Enqueue(line);
        }

        public bool TryTake(out string line)
        {
            return pending.TryDequeue(out line);
        }

        public bool HasPending { get { return !pending.IsEmpty; } }

        public void DropPending()
        {
            string l;
            while (pending.TryDequeue(out l)) { }
        }

        public string HistoryText()
        {
            lock (history) return string.Join("\r\n", history) + (history.Count > 0 ? "\r\n" : "");
        }

        public string StateText()
        {
            if (Proc.IsRunning)
            {
                if (Proc.StopRequested) return "Arresto in corso...";
                if (LastPlayers > 0) return "Acceso · " + LastPlayers + (LastPlayers == 1 ? " giocatore" : " giocatori");
                return "Acceso";
            }
            if (Sleeping) return "In pausa (vuoto)";
            return Cfg.Installed ? "Fermo" : "Da installare";
        }
    }

    /// <summary>Porte, Geyser, server già presenti sul disco.</summary>
    public static class ServerTools
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = 64;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] MEMORYSTATUSEX m);

        /// <summary>RAM del PC in GB (arrotondata), 0 se non si riesce a leggerla.</summary>
        public static int TotalRamGB()
        {
            try
            {
                var m = new MEMORYSTATUSEX();
                if (!GlobalMemoryStatusEx(m)) return 0;
                return (int)Math.Round(m.ullTotalPhys / 1073741824.0);
            }
            catch { return 0; }
        }

        public const int FirstJavaPort = 25565;
        public const int FirstBedrockPort = 19132;

        public static int JavaPort(AppConfig c)
        {
            int port;
            string v = c.Installed ? Props.Get(c.ServerDir, "server-port") : null;
            return int.TryParse(v, out port) && port > 0 ? port : FirstJavaPort;
        }

        /// <summary>Porta Bedrock (Geyser): 19132 per il server sulla 25565, 19133 per la 25566 e così via.</summary>
        public static int BedrockPort(AppConfig c)
        {
            int off = JavaPort(c) - FirstJavaPort;
            if (off < 0 || off > 500) off = 0;
            return FirstBedrockPort + off;
        }

        /// <summary>La prima porta libera dalla 25565 in su, non usata da altri server.</summary>
        public static int NextFreePort(IEnumerable<AppConfig> all, AppConfig self)
        {
            var used = new HashSet<int>();
            foreach (var c in all)
                if (c != self && c.Installed) used.Add(JavaPort(c));
            int p = FirstJavaPort;
            while (used.Contains(p)) p++;
            return p;
        }

        /// <summary>Altro server (attivo) che usa la stessa porta Java o Bedrock, oppure null.</summary>
        public static ServerSlot PortConflict(ServerSlot me, IEnumerable<ServerSlot> slots)
        {
            int jp = JavaPort(me.Cfg), bp = BedrockPort(me.Cfg);
            foreach (var s in slots)
            {
                if (s == me || !s.IsActive) continue;
                if (JavaPort(s.Cfg) == jp) return s;
                if (me.Cfg.Geyser && s.Cfg.Geyser && BedrockPort(s.Cfg) == bp) return s;
            }
            return null;
        }

        /// <summary>File di configurazione di Geyser (Paper: plugins\Geyser-*, Fabric/Forge: config\Geyser-*).</summary>
        public static List<string> GeyserConfigs(string serverDir)
        {
            var list = new List<string>();
            foreach (var root in new[] { "plugins", "config" })
            {
                string r = Path.Combine(serverDir, root);
                if (!Directory.Exists(r)) continue;
                try
                {
                    foreach (var d in Directory.GetDirectories(r))
                    {
                        if (Path.GetFileName(d).IndexOf("geyser", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string f = Path.Combine(d, "config.yml");
                        if (File.Exists(f)) list.Add(f);
                    }
                }
                catch { }
            }
            return list;
        }

        /// <summary>Imposta "port:" dentro la sezione "bedrock:" di config.yml di Geyser. True se ha cambiato qualcosa.</summary>
        public static bool SetGeyserPort(string configYml, int port)
        {
            var lines = File.ReadAllLines(configYml, Encoding.UTF8).ToList();
            bool inBedrock = false, changed = false;
            var portRx = new Regex(@"^(\s+)port:\s*(\d+)\s*$");
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                if (l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith("#"))
                {
                    inBedrock = l.TrimEnd().Equals("bedrock:", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inBedrock) continue;
                var m = portRx.Match(l);
                if (!m.Success) continue;
                if (m.Groups[2].Value != port.ToString())
                {
                    lines[i] = m.Groups[1].Value + "port: " + port;
                    changed = true;
                }
                break;
            }
            if (changed) File.WriteAllLines(configYml, lines, new UTF8Encoding(false));
            return changed;
        }

        /// <summary>Cartella predefinita per un nuovo server: server-paper, poi server-paper-2, ...</summary>
        public static string DefaultDir(string type, IEnumerable<AppConfig> all, AppConfig self)
        {
            string baseName = Path.Combine(AppConfig.BaseDir, "server-" + type.ToLower());
            for (int i = 1; ; i++)
            {
                string d = i == 1 ? baseName : baseName + "-" + i;
                bool used = all.Any(c => c != self && c.Installed &&
                    string.Equals(Full(c.ServerDir), Full(d), StringComparison.OrdinalIgnoreCase));
                bool busy = Directory.Exists(d) && Directory.EnumerateFileSystemEntries(d).Any();
                if (!used && (!busy || (self != null && string.Equals(Full(self.ServerDir), Full(d), StringComparison.OrdinalIgnoreCase))))
                    return d;
            }
        }

        public static string Full(string p)
        {
            try { return Path.GetFullPath(p ?? "").TrimEnd('\\', '/'); }
            catch { return p ?? ""; }
        }

        // ------------------------------------------------------------ server già presenti sul disco

        public class Detected
        {
            public string Type, Version, Mode = "jar", Target;
            public bool Geyser;
            public string Error;
        }

        /// <summary>Riconosce tipo, versione e file da avviare di un server già installato in una cartella.</summary>
        public static Detected Detect(string dir)
        {
            var d = new Detected();
            if (!Directory.Exists(dir)) { d.Error = "La cartella non esiste."; return d; }

            // Forge moderno
            string forgeLib = Path.Combine(dir, "libraries", "net", "minecraftforge", "forge");
            if (Directory.Exists(forgeLib))
            {
                foreach (var v in Directory.GetDirectories(forgeLib).OrderByDescending(x => x))
                {
                    string args = Path.Combine(v, "win_args.txt");
                    if (!File.Exists(args)) continue;
                    string full = Path.GetFileName(v);
                    d.Type = "Forge";
                    d.Version = full.Split('-')[0];
                    d.Mode = "args";
                    d.Target = "libraries/net/minecraftforge/forge/" + full + "/win_args.txt";
                    break;
                }
            }
            if (d.Type == null)
            {
                var forgeJar = Directory.GetFiles(dir, "forge-*.jar").FirstOrDefault(f => f.IndexOf("installer", StringComparison.OrdinalIgnoreCase) < 0);
                if (forgeJar != null)
                {
                    d.Type = "Forge";
                    d.Target = Path.GetFileName(forgeJar);
                    var m = Regex.Match(d.Target, @"forge-(\d+\.\d+(\.\d+)?)-");
                    if (m.Success) d.Version = m.Groups[1].Value;
                }
            }
            if (d.Type == null && File.Exists(Path.Combine(dir, "fabric-server-launch.jar")))
            {
                d.Type = "Fabric";
                d.Target = "fabric-server-launch.jar";
            }
            if (d.Type == null && File.Exists(Path.Combine(dir, "server.jar")))
            {
                d.Target = "server.jar";
                bool paper = Directory.Exists(Path.Combine(dir, "plugins")) || File.Exists(Path.Combine(dir, "bukkit.yml")) ||
                             Directory.Exists(Path.Combine(dir, "cache")) && Directory.GetFiles(Path.Combine(dir, "cache"), "mojang_*.jar").Length > 0;
                d.Type = paper ? "Paper" : "Vanilla";
            }
            if (d.Type == null)
            {
                d.Error = "In questa cartella non trovo un server Minecraft (server.jar, Fabric o Forge).";
                return d;
            }
            if (d.Version == null) d.Version = GuessVersion(dir, d.Target);
            d.Geyser = GeyserConfigs(dir).Count > 0 ||
                       Directory.Exists(Path.Combine(dir, "plugins")) && Directory.GetFiles(Path.Combine(dir, "plugins"), "Geyser*.jar").Length > 0 ||
                       Directory.Exists(Path.Combine(dir, "mods")) && Directory.GetFiles(Path.Combine(dir, "mods"), "Geyser*.jar").Length > 0;
            return d;
        }

        static readonly Regex VerRx = new Regex(@"^\d+\.\d+(\.\d+)?$");

        static string GuessVersion(string dir, string target)
        {
            // 1) version.json dentro il jar (Vanilla e Paper)
            try
            {
                string jar = Path.Combine(dir, target ?? "server.jar");
                if (File.Exists(jar))
                {
                    using (var z = ZipFile.OpenRead(jar))
                    {
                        var e = z.GetEntry("version.json");
                        if (e != null)
                            using (var sr = new StreamReader(e.Open()))
                            {
                                var o = Net.ParseJson(sr.ReadToEnd());
                                string id = J.S(J.P(o, "id")) ?? J.S(J.P(o, "name"));
                                if (id != null && VerRx.IsMatch(id)) return id;
                            }
                    }
                }
            }
            catch { }
            // 2) cartelle versions\<ver> (Paper), libraries\net\minecraft\server\<ver> (Fabric/Forge), .fabric\server\<ver>-server.jar
            foreach (var p in new[] { Path.Combine(dir, "versions"), Path.Combine(dir, "libraries", "net", "minecraft", "server") })
            {
                try
                {
                    if (!Directory.Exists(p)) continue;
                    var v = Directory.GetDirectories(p).Select(Path.GetFileName).Where(n => VerRx.IsMatch(n))
                        .OrderByDescending(n => n, new VersionComparer()).FirstOrDefault();
                    if (v != null) return v;
                }
                catch { }
            }
            try
            {
                string fab = Path.Combine(dir, ".fabric", "server");
                if (Directory.Exists(fab))
                    foreach (var f in Directory.GetFiles(fab, "*-server.jar"))
                    {
                        string n = Path.GetFileName(f);
                        n = n.Substring(0, n.Length - "-server.jar".Length);
                        if (VerRx.IsMatch(n)) return n;
                    }
            }
            catch { }
            return "";
        }
    }
}
