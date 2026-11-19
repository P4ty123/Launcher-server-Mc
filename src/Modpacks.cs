using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MineServer
{
    public class ModpackHit
    {
        public string Id, Slug, Title, Description;
        public long Downloads;
    }

    public class ModpackVersion
    {
        public string Name, Number, Url, FileName;
        public string[] GameVersions = new string[0], Loaders = new string[0];
        public string Label { get { return Number + "   (Minecraft " + string.Join(", ", GameVersions) + ", " + string.Join("/", Loaders) + ")"; } }
    }

    /// <summary>Cosa contiene un modpack: tipo di server, versione di Minecraft e del loader.</summary>
    public class ModpackPlan
    {
        public string Kind;          // mrpack, cf-client, cf-instance, server-pack
        public string Name = "";
        public string Type;          // Fabric, Forge, NeoForge
        public string Mc;            // versione di Minecraft (null = da chiedere)
        public string LoaderVersion; // null = consigliata
        public string Error;
        public string KindLabel
        {
            get
            {
                if (Kind == "mrpack") return "modpack Modrinth";
                if (Kind == "cf-client") return "modpack CurseForge (esportato dall'app)";
                if (Kind == "cf-instance") return "cartella dell'app CurseForge";
                return "server pack";
            }
        }
    }

    /// <summary>
    /// Importa un modpack come server:
    ///  - Modrinth (.mrpack, anche cercato dal programma): scarica solo i file che servono al server;
    ///  - CurseForge "Server Pack" (.zip): lo estrae e installa il loader giusto;
    ///  - CurseForge cartella dell'app (con minecraftinstance.json): copia mod e configurazioni già scaricate;
    ///  - CurseForge .zip esportato (manifest.json): prova a scaricare le mod dal sito; quelle bloccate dagli autori
    ///    vanno scaricate a mano (elenco in ManualDownloads).
    /// Le mod solo client si tolgono dopo con ModCheck (con conferma dell'utente).
    /// </summary>
    public class ModpackImporter
    {
        public Action<string> Log = delegate { };
        public Action<int> Progress = delegate { };
        public readonly List<string> ManualDownloads = new List<string>();
        public int SkippedClientFiles;
        public int Downloaded;

        const string Modrinth = "https://api.modrinth.com/v2";
        static readonly string[] ServerFolders = { "mods", "config", "defaultconfigs", "kubejs", "scripts", "global_packs", "datapacks", "packmenu", "resources" };

        // ------------------------------------------------------------ ricerca su Modrinth

        public static List<ModpackHit> SearchModrinth(string query)
        {
            string facets = "[[\"project_type:modpack\"],[\"categories:forge\",\"categories:neoforge\",\"categories:fabric\"]]";
            string url = Modrinth + "/search?limit=40&index=" + (string.IsNullOrEmpty(query) ? "downloads" : "relevance") +
                         "&query=" + Uri.EscapeDataString(query ?? "") + "&facets=" + Uri.EscapeDataString(facets);
            var res = new List<ModpackHit>();
            foreach (var h in J.A(J.P(Net.GetJson(url), "hits")) ?? new object[0])
            {
                var m = new ModpackHit();
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

        public static List<ModpackVersion> ModrinthVersions(string projectId)
        {
            var res = new List<ModpackVersion>();
            foreach (var v in J.A(Net.GetJson(Modrinth + "/project/" + Uri.EscapeDataString(projectId) + "/version")) ?? new object[0])
            {
                var mv = new ModpackVersion();
                mv.Name = J.S(J.P(v, "name"));
                mv.Number = J.S(J.P(v, "version_number"));
                mv.GameVersions = (J.A(J.P(v, "game_versions")) ?? new object[0]).Select(J.S).ToArray();
                mv.Loaders = (J.A(J.P(v, "loaders")) ?? new object[0]).Select(J.S).ToArray();
                if (!mv.Loaders.Any(l => l == "fabric" || l == "forge" || l == "neoforge")) continue;
                object f = null;
                foreach (var x in J.A(J.P(v, "files")) ?? new object[0])
                    if (f == null || true.Equals(J.P(x, "primary"))) f = x;
                if (f == null) continue;
                mv.Url = J.S(J.P(f, "url"));
                mv.FileName = J.S(J.P(f, "filename"));
                res.Add(mv);
            }
            return res;
        }

        /// <summary>Scarica un .mrpack nella cartella temporanea e ne restituisce il percorso.</summary>
        public string DownloadMrpack(ModpackVersion v)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mineserver-modpack");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, Safe(v.FileName ?? "modpack.mrpack"));
            Log("Scarico il modpack " + v.FileName + "...");
            Net.Download(v.Url, dest, (d, t) => Progress(t > 0 ? (int)(d * 100 / t) : -1));
            return dest;
        }

        // ------------------------------------------------------------ cosa contiene

        static string TypeFromLoaderKey(string key)
        {
            if (key == "fabric-loader" || key == "fabric") return "Fabric";
            if (key == "forge") return "Forge";
            if (key == "neoforge") return "NeoForge";
            if (key == "quilt-loader" || key == "quilt") return "Quilt";
            return null;
        }

        /// <summary>"forge-47.2.0" / "neoforge-21.1.77" / "fabric-0.15.11-1.20.1" -> tipo e versione del loader.</summary>
        public static void ParseLoaderId(string id, string mc, out string type, out string loaderVersion)
        {
            type = null; loaderVersion = null;
            if (string.IsNullOrEmpty(id)) return;
            int i = id.IndexOf('-');
            string key = i > 0 ? id.Substring(0, i) : id;
            type = TypeFromLoaderKey(key.ToLowerInvariant());
            loaderVersion = i > 0 ? id.Substring(i + 1) : null;
            if (loaderVersion != null && !string.IsNullOrEmpty(mc) && loaderVersion.EndsWith("-" + mc))
                loaderVersion = loaderVersion.Substring(0, loaderVersion.Length - mc.Length - 1);
        }

        static string ReadEntry(ZipArchive z, string name)
        {
            var e = z.GetEntry(name);
            if (e == null) return null;
            using (var sr = new StreamReader(e.Open(), Encoding.UTF8)) return sr.ReadToEnd();
        }

        public static ModpackPlan Inspect(string path)
        {
            var p = new ModpackPlan();
            try
            {
                if (Directory.Exists(path))
                {
                    string inst = Path.Combine(path, "minecraftinstance.json");
                    if (!File.Exists(inst)) { p.Error = "In questa cartella non trovo minecraftinstance.json (la cartella di un modpack dell'app CurseForge)."; return p; }
                    p.Kind = "cf-instance";
                    var o = Net.ParseJson(File.ReadAllText(inst));
                    p.Name = J.S(J.P(o, "name")) ?? Path.GetFileName(path);
                    p.Mc = J.S(J.P(o, "baseModLoader", "minecraftVersion")) ?? J.S(J.P(o, "gameVersion"));
                    string t, lv;
                    ParseLoaderId(J.S(J.P(o, "baseModLoader", "name")), p.Mc, out t, out lv);
                    p.Type = t; p.LoaderVersion = lv;
                }
                else
                {
                    using (var z = ZipFile.OpenRead(path))
                    {
                        string mr = ReadEntry(z, "modrinth.index.json");
                        string cf = ReadEntry(z, "manifest.json");
                        if (mr != null)
                        {
                            p.Kind = "mrpack";
                            var o = Net.ParseJson(mr);
                            p.Name = J.S(J.P(o, "name")) ?? Path.GetFileNameWithoutExtension(path);
                            var deps = J.O(J.P(o, "dependencies"));
                            if (deps != null)
                                foreach (var kv in deps)
                                {
                                    if (kv.Key == "minecraft") { p.Mc = J.S(kv.Value); continue; }
                                    string t = TypeFromLoaderKey(kv.Key);
                                    if (t != null) { p.Type = t; p.LoaderVersion = J.S(kv.Value); }
                                }
                        }
                        else if (cf != null && J.P(Net.ParseJson(cf), "files") != null)
                        {
                            p.Kind = "cf-client";
                            var o = Net.ParseJson(cf);
                            p.Name = J.S(J.P(o, "name")) ?? Path.GetFileNameWithoutExtension(path);
                            p.Mc = J.S(J.P(o, "minecraft", "version"));
                            foreach (var l in J.A(J.P(o, "minecraft", "modLoaders")) ?? new object[0])
                            {
                                string t, lv;
                                ParseLoaderId(J.S(J.P(l, "id")), p.Mc, out t, out lv);
                                if (t != null && (p.Type == null || true.Equals(J.P(l, "primary")))) { p.Type = t; p.LoaderVersion = lv; }
                            }
                        }
                        else
                        {
                            p.Kind = "server-pack";
                            p.Name = Path.GetFileNameWithoutExtension(path);
                            InspectServerPack(z, p);
                        }
                    }
                }
            }
            catch (Exception ex) { p.Error = "File non leggibile: " + ex.Message; return p; }
            if (p.Type == "Quilt") p.Error = "Questo modpack usa Quilt, che il programma non supporta (supportati: Fabric, Forge, NeoForge).";
            else if (p.Type == null && p.Kind != "server-pack") p.Error = "Non capisco quale loader usa il modpack (Fabric, Forge o NeoForge).";
            return p;
        }

        /// <summary>Server pack: cerca indizi su loader e versione (variables.txt, installer, librerie, mods).</summary>
        static void InspectServerPack(ZipArchive z, ModpackPlan p)
        {
            var names = z.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            var vars = z.Entries.FirstOrDefault(e => e.Name.Equals("variables.txt", StringComparison.OrdinalIgnoreCase));
            if (vars != null)
            {
                string txt;
                using (var sr = new StreamReader(vars.Open())) txt = sr.ReadToEnd();
                var mc = Regex.Match(txt, @"MINECRAFT_VERSION\s*=\s*""?([0-9.]+)");
                var ml = Regex.Match(txt, @"MODLOADER\s*=\s*""?(\w+)");
                var mv = Regex.Match(txt, @"MODLOADER_VERSION\s*=\s*""?([0-9A-Za-z.\-]+)");
                if (mc.Success) p.Mc = mc.Groups[1].Value;
                if (ml.Success) p.Type = TypeFromLoaderKey(ml.Groups[1].Value.ToLowerInvariant());
                if (mv.Success) p.LoaderVersion = mv.Groups[1].Value;
            }
            foreach (var n in names)
            {
                string f = n.Substring(n.LastIndexOf('/') + 1);
                Match m;
                if (p.Type == null && (m = Regex.Match(f, @"^neoforge-([0-9][0-9A-Za-z.\-]*?)-installer\.jar$")).Success)
                { p.Type = "NeoForge"; p.LoaderVersion = m.Groups[1].Value; p.Mc = Installer.NeoForgeMc(p.LoaderVersion); }
                else if (p.Type == null && (m = Regex.Match(f, @"^forge-(\d+\.\d+(?:\.\d+)?)-([0-9.]+)-installer\.jar$")).Success)
                { p.Type = "Forge"; p.Mc = m.Groups[1].Value; p.LoaderVersion = m.Groups[2].Value; }
                else if (p.Type == null && (m = Regex.Match(n, @"libraries/net/neoforged/neoforge/([^/]+)/")).Success)
                { p.Type = "NeoForge"; p.LoaderVersion = m.Groups[1].Value; p.Mc = Installer.NeoForgeMc(p.LoaderVersion); }
                else if (p.Type == null && (m = Regex.Match(n, @"libraries/net/minecraftforge/forge/(\d+\.\d+(?:\.\d+)?)-([^/]+)/")).Success)
                { p.Type = "Forge"; p.Mc = m.Groups[1].Value; p.LoaderVersion = m.Groups[2].Value; }
                else if (p.Type == null && f.Equals("fabric-server-launch.jar", StringComparison.OrdinalIgnoreCase))
                    p.Type = "Fabric";
            }
            if (p.Type == null)
            {
                // dalle mod: un loader dichiarato dalle mod dentro mods/
                foreach (var e in z.Entries.Where(e => e.FullName.Replace('\\', '/').Contains("mods/") && e.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).Take(30))
                {
                    try
                    {
                        using (var ms = new MemoryStream())
                        {
                            using (var s = e.Open()) s.CopyTo(ms);
                            ms.Position = 0;
                            using (var mz = new ZipArchive(ms, ZipArchiveMode.Read))
                            {
                                if (mz.GetEntry("fabric.mod.json") != null) { p.Type = "Fabric"; break; }
                                if (mz.GetEntry("META-INF/neoforge.mods.toml") != null) { p.Type = "NeoForge"; break; }
                                if (mz.GetEntry("META-INF/mods.toml") != null) { p.Type = "Forge"; break; }
                            }
                        }
                    }
                    catch { }
                }
            }
            if (p.Type == null) p.Error = "In questo zip non trovo un server Minecraft con mod (Fabric, Forge o NeoForge).";
        }

        // ------------------------------------------------------------ installazione

        static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>Percorso di destinazione sicuro (niente "..", niente percorsi assoluti).</summary>
        static string Inside(string root, string rel)
        {
            rel = (rel ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(root, rel));
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) throw new Exception("Percorso non valido nel modpack: " + rel);
            return full;
        }

        /// <summary>Estrae le voci dello zip che iniziano con prefix (es. "overrides/") dentro dir.</summary>
        static int ExtractPrefix(ZipArchive z, string prefix, string dir)
        {
            int n = 0;
            foreach (var e in z.Entries)
            {
                string fn = e.FullName.Replace('\\', '/');
                if (!fn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || fn.EndsWith("/")) continue;
                string dest = Inside(dir, fn.Substring(prefix.Length));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                e.ExtractToFile(dest, true);
                n++;
            }
            return n;
        }

        public void Install(string path, ModpackPlan p, AppConfig cfg, string dir, Installer inst)
        {
            if (p.Error != null) throw new Exception(p.Error);
            if (string.IsNullOrEmpty(p.Mc)) throw new Exception("Versione di Minecraft sconosciuta.");
            Directory.CreateDirectory(dir);

            if (p.Kind == "server-pack")
            {
                Log("Estraggo il server pack...");
                using (var z = ZipFile.OpenRead(path))
                {
                    // se tutto è dentro una sola cartella, la salto
                    var top = z.Entries.Select(e => e.FullName.Replace('\\', '/')).Where(n => n.Length > 0)
                                .Select(n => n.Contains("/") ? n.Substring(0, n.IndexOf('/') + 1) : "").Distinct().ToList();
                    string prefix = top.Count == 1 && top[0].Length > 0 ? top[0] : "";
                    int extracted = ExtractPrefix(z, prefix, dir);
                    Log("  " + extracted + " file estratti.");
                }
            }

            // loader e Java (non tocca mod e configurazioni già presenti)
            inst.InstallServer(cfg, p.Type, p.Mc, dir, false, false, p.LoaderVersion);

            if (p.Kind == "mrpack") InstallMrpackFiles(path, dir);
            else if (p.Kind == "cf-client") InstallCurseForgeZip(path, dir);
            else if (p.Kind == "cf-instance") CopyInstance(path, dir);

            int mods = Directory.Exists(Path.Combine(dir, "mods")) ? Directory.GetFiles(Path.Combine(dir, "mods"), "*.jar").Length : 0;
            Log("Mod nel server: " + mods + ".");
        }

        void InstallMrpackFiles(string path, string dir)
        {
            using (var z = ZipFile.OpenRead(path))
            {
                var o = Net.ParseJson(ReadEntry(z, "modrinth.index.json"));
                var files = J.A(J.P(o, "files")) ?? new object[0];
                var failed = new List<string>();
                int i = 0;
                foreach (var f in files)
                {
                    i++;
                    string rel = J.S(J.P(f, "path"));
                    string serverSide = J.S(J.P(f, "env", "server")) ?? "required";
                    if (serverSide == "unsupported") { SkippedClientFiles++; continue; }
                    string dest = Inside(dir, rel);
                    string sha1 = (J.S(J.P(f, "hashes", "sha1")) ?? "").ToLowerInvariant();
                    if (File.Exists(dest) && sha1.Length > 0 && ModCheck.Sha1(dest) == sha1) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    bool ok = false;
                    foreach (var u in J.A(J.P(f, "downloads")) ?? new object[0])
                    {
                        try
                        {
                            Net.Download(J.S(u), dest, null);
                            if (sha1.Length > 0 && ModCheck.Sha1(dest) != sha1) { File.Delete(dest); continue; }
                            ok = true;
                            break;
                        }
                        catch { }
                    }
                    if (ok) Downloaded++;
                    else failed.Add(rel);
                    Progress(i * 100 / Math.Max(1, files.Length));
                    if (i % 20 == 0) Log("  " + i + "/" + files.Length + " file...");
                }
                Log("Scaricati " + Downloaded + " file; saltati " + SkippedClientFiles + " solo per il gioco (client).");
                int ov = ExtractPrefix(z, "overrides/", dir) + ExtractPrefix(z, "server-overrides/", dir);
                Log("Configurazioni del modpack copiate: " + ov + " file.");
                if (failed.Count > 0)
                    throw new Exception(failed.Count + " file non scaricati (es. " + failed[0] + "). Controlla la connessione e riprova: quelli già scaricati non verranno riscaricati.");
            }
        }

        void InstallCurseForgeZip(string path, string dir)
        {
            using (var z = ZipFile.OpenRead(path))
            {
                var o = Net.ParseJson(ReadEntry(z, "manifest.json"));
                var files = J.A(J.P(o, "files")) ?? new object[0];
                string mods = Path.Combine(dir, "mods");
                Directory.CreateDirectory(mods);
                int i = 0;
                foreach (var f in files)
                {
                    i++;
                    if (false.Equals(J.P(f, "required"))) continue;
                    string pid = J.S(J.P(f, "projectID")), fid = J.S(J.P(f, "fileID"));
                    string url = "https://www.curseforge.com/api/v1/mods/" + pid + "/files/" + fid + "/download";
                    try
                    {
                        string saved = Net.DownloadToFolder(url, mods);
                        if (!saved.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) { File.Delete(saved); continue; }   // resource/shader pack
                        Downloaded++;
                    }
                    catch
                    {
                        ManualDownloads.Add("https://www.curseforge.com/projects/" + pid + "  (file " + fid + ")");
                    }
                    Progress(i * 100 / Math.Max(1, files.Length));
                    if (i % 20 == 0) Log("  " + i + "/" + files.Length + " mod...");
                }
                string ovDir = J.S(J.P(o, "overrides")) ?? "overrides";
                int ov = ExtractPrefix(z, ovDir.TrimEnd('/') + "/", dir);
                Log("Scaricate " + Downloaded + " mod da CurseForge; configurazioni copiate: " + ov + " file.");
                if (ManualDownloads.Count > 0)
                    Log("Attenzione: " + ManualDownloads.Count + " mod non si possono scaricare in automatico (l'autore lo vieta o CurseForge ha rifiutato).");
            }
        }

        void CopyInstance(string src, string dir)
        {
            int n = 0;
            foreach (var folder in ServerFolders)
            {
                string from = Path.Combine(src, folder);
                if (!Directory.Exists(from)) continue;
                foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(src.Length).TrimStart('\\', '/');
                    string dest = Inside(dir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(f, dest, true);
                    n++;
                }
            }
            Log("Copiati dalla cartella di CurseForge " + n + " file (mod e configurazioni).");
        }
    }
}
