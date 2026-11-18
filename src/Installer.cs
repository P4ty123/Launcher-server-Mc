using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MineServer
{
    public class ModHit
    {
        public string Id;
        public string Slug;
        public string Title;
        public string Description;
        public long Downloads;
    }

    public class Installer
    {
        public Action<string> Log = delegate { };
        public Action<int> Progress = delegate { };

        public static readonly string[] Types = { "Vanilla", "Paper", "Fabric", "Forge" };

        const string MojangManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        const string PaperApi = "https://fill.papermc.io/v3/projects/paper";
        const string FabricMeta = "https://meta.fabricmc.net/v2/versions";
        const string ForgePromos = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
        const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
        const string Modrinth = "https://api.modrinth.com/v2";
        const string GeyserApi = "https://download.geysermc.org/v2/projects/";

        static object manifestCache;
        static readonly Regex ReleaseVersion = new Regex(@"^\d+(\.\d+)+$");

        // ---------------------------------------------------------------- info sui tipi

        public static string AddonFolder(string type) { return type == "Paper" ? "plugins" : "mods"; }

        public static string[] LoadersFor(string type)
        {
            if (type == "Fabric") return new[] { "fabric" };
            if (type == "Forge") return new[] { "forge" };
            if (type == "Paper") return new[] { "paper", "spigot", "bukkit" };
            return new string[0];
        }

        public static string[] Recommended(string type)
        {
            if (type == "Fabric") return new[] { "fabric-api", "lithium", "ferrite-core", "krypton", "chunky" };
            if (type == "Forge") return new[] { "ferrite-core", "modernfix", "chunky" };
            if (type == "Paper") return new[] { "chunky" };
            return new string[0];
        }

        // ---------------------------------------------------------------- versioni

        static object Manifest()
        {
            if (manifestCache == null) manifestCache = Net.GetJson(MojangManifest);
            return manifestCache;
        }

        public static List<string> GetVersions(string type)
        {
            var list = new List<string>();
            if (type == "Vanilla")
            {
                foreach (var v in J.A(J.P(Manifest(), "versions")))
                    if (J.S(J.P(v, "type")) == "release") list.Add(J.S(J.P(v, "id")));
            }
            else if (type == "Paper")
            {
                var vers = J.O(J.P(Net.GetJson(PaperApi), "versions"));
                if (vers != null)
                    foreach (var kv in vers)
                        foreach (var x in J.A(kv.Value) ?? new object[0])
                        {
                            string s = J.S(x);
                            if (s != null && ReleaseVersion.IsMatch(s) && !list.Contains(s)) list.Add(s);
                        }
                list.Sort(new VersionComparer());
                list.Reverse();
            }
            else if (type == "Fabric")
            {
                foreach (var v in J.A(Net.GetJson(FabricMeta + "/game")))
                    if (true.Equals(J.P(v, "stable"))) list.Add(J.S(J.P(v, "version")));
            }
            else if (type == "Forge")
            {
                var promos = J.O(J.P(Net.GetJson(ForgePromos), "promos"));
                if (promos != null)
                    foreach (var k in promos.Keys)
                    {
                        int i = k.LastIndexOf('-');
                        if (i <= 0) continue;
                        string mc = k.Substring(0, i);
                        if (ReleaseVersion.IsMatch(mc) && !list.Contains(mc)) list.Add(mc);
                    }
                list.Sort(new VersionComparer());
                list.Reverse();
            }
            return list;
        }

        static string MojangVersionUrl(string mcVersion)
        {
            foreach (var v in J.A(J.P(Manifest(), "versions")))
                if (J.S(J.P(v, "id")) == mcVersion) return J.S(J.P(v, "url"));
            return null;
        }

        public static int JavaMajorFor(string mcVersion)
        {
            try
            {
                string url = MojangVersionUrl(mcVersion);
                if (url != null)
                {
                    var mj = J.P(Net.GetJson(url), "javaVersion", "majorVersion");
                    return mj != null ? Convert.ToInt32(mj) : 8;
                }
            }
            catch { }
            return 21;
        }

        // ---------------------------------------------------------------- Java

        void Prog(long done, long total)
        {
            Progress(total > 0 ? (int)(done * 100 / total) : -1);
        }

        static string FindJava(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            foreach (var f in Directory.GetFiles(dir, "java.exe", SearchOption.AllDirectories))
                if (f.EndsWith("\\bin\\java.exe", StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        public string EnsureJava(int major)
        {
            var candidates = new List<int> { major };
            foreach (int lts in new[] { 8, 11, 17, 21, 25 })
                if (lts > major) candidates.Add(lts);

            foreach (int m in candidates)
            {
                string dir = Path.Combine(AppConfig.BaseDir, "runtime", "java" + m);
                string exe = FindJava(dir);
                if (exe != null) { Log("Java " + m + " già presente."); return exe; }

                foreach (string kind in new[] { "jre", "jdk" })
                {
                    string url = "https://api.adoptium.net/v3/binary/latest/" + m + "/ga/windows/x64/" + kind + "/hotspot/normal/eclipse";
                    try
                    {
                        Log("Scarico Java " + m + " (" + kind.ToUpper() + " Temurin)...");
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                        Directory.CreateDirectory(dir);
                        string zip = Path.Combine(dir, "java.zip");
                        Net.Download(url, zip, Prog);
                        Log("Estraggo Java...");
                        Progress(-1);
                        ZipFile.ExtractToDirectory(zip, dir);
                        File.Delete(zip);
                        exe = FindJava(dir);
                        if (exe != null) { Log("Java " + m + " pronto."); return exe; }
                    }
                    catch (Exception ex)
                    {
                        Log("  non riuscito: " + ex.Message);
                    }
                }
            }
            throw new Exception("Impossibile scaricare Java " + major + ". Controlla la connessione.");
        }

        // ---------------------------------------------------------------- server

        public void InstallServer(AppConfig cfg, string type, string ver, string dir, bool optimMods, bool geyser)
        {
            Directory.CreateDirectory(dir);
            Log("=== Installo " + type + " " + ver + " in " + dir + " ===");

            int jm = JavaMajorFor(ver);
            Log("Minecraft " + ver + " richiede Java " + jm + ".");
            string java = EnsureJava(jm);

            string mode = "jar";
            string target = "server.jar";

            if (type == "Vanilla")
            {
                string vurl = MojangVersionUrl(ver);
                if (vurl == null) throw new Exception("Versione non trovata.");
                string surl = J.S(J.P(Net.GetJson(vurl), "downloads", "server", "url"));
                if (surl == null) throw new Exception("Questa versione non ha un server ufficiale scaricabile.");
                Log("Scarico il server ufficiale Mojang...");
                Net.Download(surl, Path.Combine(dir, target), Prog);
            }
            else if (type == "Paper")
            {
                var builds = J.A(Net.GetJson(PaperApi + "/versions/" + ver + "/builds"));
                if (builds == null || builds.Length == 0) throw new Exception("Nessuna build di Paper per " + ver + ".");
                object chosen = null;
                foreach (var b in builds)
                    if (J.S(J.P(b, "channel")) == "STABLE") { chosen = b; break; }
                if (chosen == null)
                {
                    chosen = builds[0];
                    Log("Attenzione: per " + ver + " non c'è ancora una build stabile, uso la più recente (sperimentale).");
                }
                string url = J.S(J.P(chosen, "downloads", "server:default", "url"));
                if (url == null) throw new Exception("Link di download di Paper non trovato.");
                Log("Scarico Paper build " + J.S(J.P(chosen, "id")) + "...");
                Net.Download(url, Path.Combine(dir, target), Prog);
            }
            else if (type == "Fabric")
            {
                string loader = FirstStable(FabricMeta + "/loader");
                string inst = FirstStable(FabricMeta + "/installer");
                Log("Fabric loader " + loader + ", installer " + inst + ".");
                target = "fabric-server-launch.jar";
                Net.Download(FabricMeta + "/loader/" + ver + "/" + loader + "/" + inst + "/server/jar",
                    Path.Combine(dir, target), Prog);
                Log("Al primo avvio Fabric scaricherà da solo il server Mojang.");
            }
            else if (type == "Forge")
            {
                var promos = J.O(J.P(Net.GetJson(ForgePromos), "promos"));
                string fv = null;
                if (promos != null && promos.ContainsKey(ver + "-recommended")) fv = J.S(promos[ver + "-recommended"]);
                else if (promos != null && promos.ContainsKey(ver + "-latest")) fv = J.S(promos[ver + "-latest"]);
                if (fv == null) throw new Exception("Nessuna versione di Forge per " + ver + ".");
                string full = ver + "-" + fv;
                string instPath = Path.Combine(dir, "forge-installer.jar");
                Log("Scarico l'installer di Forge " + full + "...");
                Net.Download(ForgeMaven + full + "/forge-" + full + "-installer.jar", instPath, Prog);
                Log("Eseguo l'installer di Forge (qualche minuto, scarica molte librerie)...");
                Progress(-1);
                int code = RunProcess(java, "-jar \"" + instPath + "\" --installServer", dir);
                if (code != 0) throw new Exception("L'installer di Forge è terminato con errore (" + code + ").");

                string argsFile = Path.Combine(dir, "libraries", "net", "minecraftforge", "forge", full, "win_args.txt");
                if (File.Exists(argsFile))
                {
                    mode = "args";
                    target = "libraries/net/minecraftforge/forge/" + full + "/win_args.txt";
                }
                else
                {
                    var jars = Directory.GetFiles(dir, "forge-*.jar")
                        .Where(f => f.IndexOf("installer", StringComparison.OrdinalIgnoreCase) < 0).ToArray();
                    if (jars.Length == 0) throw new Exception("Forge installato ma non trovo il file da avviare.");
                    target = Path.GetFileName(jars[0]);
                }
                try { File.Delete(instPath); } catch { }
                try { File.Delete(instPath + ".log"); } catch { }
            }

            File.WriteAllText(Path.Combine(dir, "eula.txt"), "eula=true\r\n");
            EnsureLightProperties(dir);

            if (optimMods)
            {
                Log("Installo le ottimizzazioni consigliate...");
                var visited = new HashSet<string>();
                foreach (var slug in Recommended(type))
                {
                    try { InstallFromModrinth(slug, type, ver, dir, visited); }
                    catch (Exception ex) { Log("  " + slug + ": errore " + ex.Message); }
                }
            }

            if (geyser)
            {
                try { InstallGeyser(type, ver, dir); }
                catch (Exception ex) { Log("Geyser non installato (" + ex.Message + "). Il server funziona lo stesso."); geyser = false; }
            }

            cfg.ServerType = type;
            cfg.McVersion = ver;
            cfg.ServerDir = dir;
            cfg.JavaPath = java;
            cfg.LaunchMode = mode;
            cfg.LaunchTarget = target;
            cfg.Geyser = geyser;
            cfg.Installed = true;
            cfg.Save();
            Progress(100);
        }

        static string FirstStable(string url)
        {
            var arr = J.A(Net.GetJson(url));
            if (arr == null || arr.Length == 0) throw new Exception("Risposta Fabric non valida.");
            foreach (var v in arr)
                if (true.Equals(J.P(v, "stable"))) return J.S(J.P(v, "version"));
            return J.S(J.P(arr[0], "version"));
        }

        int RunProcess(string exe, string args, string dir)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = dir;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            using (var p = new Process())
            {
                p.StartInfo = psi;
                DataReceivedEventHandler h = (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log("  " + e.Data); };
                p.OutputDataReceived += h;
                p.ErrorDataReceived += h;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        /// <summary>Se server.properties non esiste ancora, ne crea uno già alleggerito.</summary>
        static void EnsureLightProperties(string dir)
        {
            string f = Path.Combine(dir, "server.properties");
            if (File.Exists(f)) return;
            var sb = new StringBuilder();
            sb.AppendLine("#Creato da MineServer Manager - impostazioni leggere per mini PC");
            sb.AppendLine("motd=Server Minecraft");
            sb.AppendLine("max-players=10");
            sb.AppendLine("view-distance=7");
            sb.AppendLine("simulation-distance=5");
            sb.AppendLine("sync-chunk-writes=false");
            sb.AppendLine("network-compression-threshold=256");
            sb.AppendLine("entity-broadcast-range-percentage=75");
            sb.AppendLine("allow-flight=true");
            File.WriteAllText(f, sb.ToString(), new UTF8Encoding(false));
        }

        // ---------------------------------------------------------------- mod / plugin (Modrinth)

        static string JsonArr(IEnumerable<string> items)
        {
            return "[" + string.Join(",", items.Select(a => "\"" + a + "\"")) + "]";
        }

        public static List<ModHit> Search(string query, string type, string ver)
        {
            var facets = new List<string>();
            facets.Add("[" + string.Join(",", LoadersFor(type).Select(l => "\"categories:" + l + "\"")) + "]");
            facets.Add("[\"versions:" + ver + "\"]");
            if (type != "Paper")
            {
                facets.Add("[\"project_type:mod\"]");
                facets.Add("[\"server_side:required\",\"server_side:optional\"]");
            }
            string url = Modrinth + "/search?limit=40&index=relevance&query=" + Uri.EscapeDataString(query ?? "") +
                         "&facets=" + Uri.EscapeDataString("[" + string.Join(",", facets) + "]");
            var res = new List<ModHit>();
            foreach (var h in J.A(J.P(Net.GetJson(url), "hits")) ?? new object[0])
            {
                var m = new ModHit();
                m.Id = J.S(J.P(h, "project_id"));
                m.Slug = J.S(J.P(h, "slug"));
                m.Title = J.S(J.P(h, "title"));
                m.Description = J.S(J.P(h, "description"));
                object dl = J.P(h, "downloads");
                m.Downloads = dl == null ? 0 : Convert.ToInt64(dl);
                res.Add(m);
            }
            return res;
        }

        public bool InstallFromModrinth(string idOrSlug, string type, string ver, string serverDir, HashSet<string> visited)
        {
            if (visited == null) visited = new HashSet<string>();
            if (!visited.Add(idOrSlug)) return true;

            string url = Modrinth + "/project/" + Uri.EscapeDataString(idOrSlug) + "/version?loaders=" +
                         Uri.EscapeDataString(JsonArr(LoadersFor(type))) + "&game_versions=" +
                         Uri.EscapeDataString(JsonArr(new[] { ver }));
            object[] versions;
            try { versions = J.A(Net.GetJson(url)); }
            catch (WebException ex) { Log(idOrSlug + ": non trovato (" + ex.Message + ")"); return false; }

            if (versions == null || versions.Length == 0)
            {
                Log(idOrSlug + ": nessuna versione compatibile con " + type + " " + ver + ", saltato.");
                return false;
            }

            object chosen = versions[0];
            foreach (var v in versions)
                if (J.S(J.P(v, "version_type")) == "release") { chosen = v; break; }

            var files = J.A(J.P(chosen, "files"));
            if (files == null || files.Length == 0) return false;
            object f = files[0];
            foreach (var x in files)
                if (true.Equals(J.P(x, "primary"))) { f = x; break; }

            string fname = J.S(J.P(f, "filename"));
            string furl = J.S(J.P(f, "url"));
            string folder = Path.Combine(serverDir, AddonFolder(type));
            Directory.CreateDirectory(folder);
            string dest = Path.Combine(folder, fname);
            if (File.Exists(dest)) Log(fname + " già installata.");
            else
            {
                Log("Scarico " + fname + "...");
                Net.Download(furl, dest, Prog);
            }

            var deps = J.A(J.P(chosen, "dependencies"));
            if (deps != null)
                foreach (var d in deps)
                {
                    if (J.S(J.P(d, "dependency_type")) != "required") continue;
                    string pid = J.S(J.P(d, "project_id"));
                    if (string.IsNullOrEmpty(pid)) continue;
                    Log("  serve anche una dipendenza (" + pid + ")...");
                    try { InstallFromModrinth(pid, type, ver, serverDir, visited); }
                    catch (Exception ex) { Log("  dipendenza non installata: " + ex.Message); }
                }
            return true;
        }

        // ---------------------------------------------------------------- Geyser (Bedrock)

        public void InstallGeyser(string type, string ver, string dir)
        {
            string plat = type == "Paper" ? "spigot" : type == "Fabric" ? "fabric" : null;
            if (plat == null) { Log("Geyser non è disponibile per " + type + "."); return; }
            string folder = Path.Combine(dir, AddonFolder(type));
            Directory.CreateDirectory(folder);
            Log("Scarico Geyser + Floodgate (giocatori Bedrock)...");
            Log("Nota: Geyser supporta solo le versioni più recenti di Minecraft.");
            Net.Download(GeyserApi + "geyser/versions/latest/builds/latest/downloads/" + plat,
                Path.Combine(folder, "Geyser-" + plat + ".jar"), Prog);
            Net.Download(GeyserApi + "floodgate/versions/latest/builds/latest/downloads/" + plat,
                Path.Combine(folder, "floodgate-" + plat + ".jar"), Prog);
            if (type == "Fabric") InstallFromModrinth("fabric-api", type, ver, dir, null);
        }
    }
}
