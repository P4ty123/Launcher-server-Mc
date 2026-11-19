using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MineServer
{
    /// <summary>Lettura/scrittura dei file .ini del programma (chiave=valore, UTF-8).</summary>
    static class Ini
    {
        public static Dictionary<string, string> Read(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return d;
            try
            {
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return d;
        }

        public static void Write(string path, StringBuilder sb)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }

        public static string S(Dictionary<string, string> d, string k, string def)
        {
            string v;
            return d.TryGetValue(k, out v) ? v : def;
        }

        public static int I(Dictionary<string, string> d, string k, int def)
        {
            string v; int r;
            return d.TryGetValue(k, out v) && int.TryParse(v, out r) ? r : def;
        }

        public static bool B(Dictionary<string, string> d, string k, bool def)
        {
            string v; bool r;
            return d.TryGetValue(k, out v) && bool.TryParse(v, out r) ? r : def;
        }

        public static string OneLine(string v)
        {
            return (v ?? "").Replace("\r", " ").Replace("\n", " ");
        }
    }

    /// <summary>
    /// Un server (profilo). Ogni server ha il suo file servers\&lt;Id&gt;.ini.
    /// Id = 0 vuol dire "di prova, non salvato" (test automatici).
    /// </summary>
    public class AppConfig
    {
        public int Id = 0;
        public string Name = "";
        public bool AutoStart = false;           // si accende all'accensione del PC
        public string ServerType = "Paper";
        public string McVersion = "";
        public string ServerDir = "";
        public int RamGB = 10;
        public string JavaPath = "";
        public string LaunchMode = "jar";   // "jar" oppure "args" (Forge moderno)
        public string LaunchTarget = "";
        public bool Installed = false;
        public bool AutoRestart = true;
        public bool BackupEnabled = true;
        public string BackupTime = "04:00";
        public int BackupKeep = 7;
        public bool DailyRestart = false;
        public string RestartTime = "05:00";
        public bool Geyser = false;
        public bool IdleSleep = false;          // spegne il server quando è vuoto e lo riaccende quando qualcuno entra
        public int IdleMinutes = 15;
        // Personalizza (testi con i colori "&")
        public string WelcomeMsg = DefWelcome;
        public string LeaveMsg = "";
        public string StartMsg = "";
        public string PauseMotd = DefPause;
        public string WakeMsg = DefWake;
        public const string DefWelcome = "&eCiao &b{giocatore}&e, benvenuto!";
        public const string DefPause = "&6Server in pausa: &eentra per accenderlo (circa 1 minuto)";
        public const string DefWake = "&6Il server si sta accendendo... &eRiprova tra circa un minuto.";

        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        public static string ServersDir
        {
            get { return Path.Combine(BaseDir, "servers"); }
        }

        string FilePath
        {
            get { return Path.Combine(ServersDir, Id + ".ini"); }
        }

        /// <summary>Il primo server usa la cartella "backup" di sempre, gli altri "backup-2", "backup-3"...</summary>
        public string BackupDir
        {
            get { return Path.Combine(BaseDir, Id <= 1 ? "backup" : "backup-" + Id); }
        }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return Name;
                return Installed ? ServerType + " " + McVersion : "Server " + Id;
            }
        }

        public string TypeLabel
        {
            get { return Installed ? ServerType + " " + McVersion : "(da installare)"; }
        }

        void ReadFrom(Dictionary<string, string> d)
        {
            Name = Ini.S(d, "Name", Name);
            AutoStart = Ini.B(d, "AutoStart", AutoStart);
            ServerType = Ini.S(d, "ServerType", ServerType);
            McVersion = Ini.S(d, "McVersion", McVersion);
            ServerDir = Ini.S(d, "ServerDir", ServerDir);
            RamGB = Ini.I(d, "RamGB", RamGB);
            JavaPath = Ini.S(d, "JavaPath", JavaPath);
            LaunchMode = Ini.S(d, "LaunchMode", LaunchMode);
            LaunchTarget = Ini.S(d, "LaunchTarget", LaunchTarget);
            Installed = Ini.B(d, "Installed", Installed);
            AutoRestart = Ini.B(d, "AutoRestart", AutoRestart);
            BackupEnabled = Ini.B(d, "BackupEnabled", BackupEnabled);
            BackupTime = Ini.S(d, "BackupTime", BackupTime);
            BackupKeep = Ini.I(d, "BackupKeep", BackupKeep);
            DailyRestart = Ini.B(d, "DailyRestart", DailyRestart);
            RestartTime = Ini.S(d, "RestartTime", RestartTime);
            Geyser = Ini.B(d, "Geyser", Geyser);
            IdleSleep = Ini.B(d, "IdleSleep", IdleSleep);
            IdleMinutes = Math.Max(1, Math.Min(240, Ini.I(d, "IdleMinutes", IdleMinutes)));
            WelcomeMsg = Ini.S(d, "WelcomeMsg", WelcomeMsg);
            LeaveMsg = Ini.S(d, "LeaveMsg", LeaveMsg);
            StartMsg = Ini.S(d, "StartMsg", StartMsg);
            PauseMotd = Ini.S(d, "PauseMotd", PauseMotd);
            WakeMsg = Ini.S(d, "WakeMsg", WakeMsg);
        }

        public static AppConfig LoadFile(string path)
        {
            var c = new AppConfig();
            int id;
            if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out id) || id <= 0) return null;
            c.Id = id;
            c.ReadFrom(Ini.Read(path));
            return c;
        }

        /// <summary>Server dal vecchio config.ini (versioni fino alla 1.3): diventa il server n. 1.</summary>
        public static AppConfig FromLegacy(Dictionary<string, string> d)
        {
            var c = new AppConfig();
            c.Id = 1;
            c.ReadFrom(d);
            c.AutoStart = c.Installed && Ini.B(d, "StartServerOnLaunch", true);
            return c;
        }

        public void Save()
        {
            if (Id <= 0) return;
            lock (this)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Name=" + Ini.OneLine(Name));
                sb.AppendLine("AutoStart=" + AutoStart);
                sb.AppendLine("ServerType=" + ServerType);
                sb.AppendLine("McVersion=" + McVersion);
                sb.AppendLine("ServerDir=" + ServerDir);
                sb.AppendLine("RamGB=" + RamGB);
                sb.AppendLine("JavaPath=" + JavaPath);
                sb.AppendLine("LaunchMode=" + LaunchMode);
                sb.AppendLine("LaunchTarget=" + LaunchTarget);
                sb.AppendLine("Installed=" + Installed);
                sb.AppendLine("AutoRestart=" + AutoRestart);
                sb.AppendLine("BackupEnabled=" + BackupEnabled);
                sb.AppendLine("BackupTime=" + BackupTime);
                sb.AppendLine("BackupKeep=" + BackupKeep);
                sb.AppendLine("DailyRestart=" + DailyRestart);
                sb.AppendLine("RestartTime=" + RestartTime);
                sb.AppendLine("Geyser=" + Geyser);
                sb.AppendLine("IdleSleep=" + IdleSleep);
                sb.AppendLine("IdleMinutes=" + IdleMinutes);
                sb.AppendLine("WelcomeMsg=" + Ini.OneLine(WelcomeMsg));
                sb.AppendLine("LeaveMsg=" + Ini.OneLine(LeaveMsg));
                sb.AppendLine("StartMsg=" + Ini.OneLine(StartMsg));
                sb.AppendLine("PauseMotd=" + Ini.OneLine(PauseMotd));
                sb.AppendLine("WakeMsg=" + Ini.OneLine(WakeMsg));
                Ini.Write(FilePath, sb);
            }
        }

        public void DeleteFile()
        {
            try { if (Id > 0 && File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
    }

    /// <summary>Impostazioni del programma (valgono per tutti i server): config.ini.</summary>
    public class GlobalConfig
    {
        public int CurrentId = 1;
        public string ServerPriority = "AboveNormal";   // Normal, AboveNormal, High, RealTime
        public bool BoostEnabled = false;
        public bool BoostAggressive = false;
        public bool BoostTrim = true;
        public bool AutostartAdmin = false;
        public bool CheckUpdates = true;
        public bool PlayitEnabled = false;
        public bool AutoPowerPlan = false;      // Prestazioni eccellenti / Risparmio di energia in automatico
        public string PowerPerfGuid = "";
        public string PowerSaverGuid = "";
        public string PowerOriginalGuid = "";   // piano che c'era prima, rimesso alla chiusura
        public bool StaggerStart = true;        // all'accensione del PC: un server alla volta
        public bool SaverWhenPaused = true;     // "Risparmio di energia" anche quando i server sono solo in pausa
        public string ThemeMode = "auto";       // modalità notte: auto (come Windows), light, dark

        public readonly List<AppConfig> Servers = new List<AppConfig>();

        /// <summary>Se false le impostazioni non vengono scritte su disco (test automatici).</summary>
        public bool Persist = true;

        static string FilePath
        {
            get { return Path.Combine(AppConfig.BaseDir, "config.ini"); }
        }

        public static GlobalConfig Load()
        {
            var g = new GlobalConfig();
            var d = Ini.Read(FilePath);
            g.CurrentId = Ini.I(d, "CurrentId", g.CurrentId);
            g.ServerPriority = Ini.S(d, "ServerPriority", g.ServerPriority);
            g.BoostEnabled = Ini.B(d, "BoostEnabled", g.BoostEnabled);
            g.BoostAggressive = Ini.B(d, "BoostAggressive", g.BoostAggressive);
            g.BoostTrim = Ini.B(d, "BoostTrim", g.BoostTrim);
            g.AutostartAdmin = Ini.B(d, "AutostartAdmin", g.AutostartAdmin);
            g.CheckUpdates = Ini.B(d, "CheckUpdates", g.CheckUpdates);
            g.PlayitEnabled = Ini.B(d, "PlayitEnabled", g.PlayitEnabled);
            g.AutoPowerPlan = Ini.B(d, "AutoPowerPlan", g.AutoPowerPlan);
            g.PowerPerfGuid = Ini.S(d, "PowerPerfGuid", g.PowerPerfGuid);
            g.PowerSaverGuid = Ini.S(d, "PowerSaverGuid", g.PowerSaverGuid);
            g.PowerOriginalGuid = Ini.S(d, "PowerOriginalGuid", g.PowerOriginalGuid);
            g.StaggerStart = Ini.B(d, "StaggerStart", g.StaggerStart);
            g.SaverWhenPaused = Ini.B(d, "SaverWhenPaused", g.SaverWhenPaused);
            g.ThemeMode = Ini.S(d, "ThemeMode", g.ThemeMode);
            if (g.ThemeMode != "light" && g.ThemeMode != "dark") g.ThemeMode = "auto";

            if (Directory.Exists(AppConfig.ServersDir))
            {
                foreach (var f in Directory.GetFiles(AppConfig.ServersDir, "*.ini"))
                {
                    var c = AppConfig.LoadFile(f);
                    if (c != null) g.Servers.Add(c);
                }
            }
            if (g.Servers.Count == 0)
            {
                // primo avvio della 1.4 (o primo avvio in assoluto): il server di prima diventa il n. 1
                var c = AppConfig.FromLegacy(d);
                if (!c.Installed && d.Count == 0) c.Name = "Il mio server";
                g.Servers.Add(c);
                c.Save();
                g.CurrentId = 1;
                // riscrivo config.ini solo se il file del server è stato salvato davvero (niente dati persi)
                if (File.Exists(Path.Combine(AppConfig.ServersDir, "1.ini"))) g.Save();
            }
            g.Servers.Sort((a, b) => a.Id.CompareTo(b.Id));
            if (g.Find(g.CurrentId) == null) g.CurrentId = g.Servers[0].Id;
            return g;
        }

        public AppConfig Find(int id)
        {
            foreach (var s in Servers) if (s.Id == id) return s;
            return null;
        }

        public AppConfig AddServer(string name)
        {
            var c = new AppConfig();
            c.Id = Servers.Count == 0 ? 1 : Servers.Max(s => s.Id) + 1;
            c.Name = name;
            Servers.Add(c);
            c.Save();
            return c;
        }

        public void RemoveServer(AppConfig c)
        {
            Servers.Remove(c);
            c.DeleteFile();
            if (Find(CurrentId) == null && Servers.Count > 0) CurrentId = Servers[0].Id;
            Save();
        }

        public void Save()
        {
            if (!Persist) return;
            lock (this)
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Impostazioni generali. Ogni server ha il suo file nella cartella 'servers'.");
                sb.AppendLine("CurrentId=" + CurrentId);
                sb.AppendLine("ServerPriority=" + ServerPriority);
                sb.AppendLine("BoostEnabled=" + BoostEnabled);
                sb.AppendLine("BoostAggressive=" + BoostAggressive);
                sb.AppendLine("BoostTrim=" + BoostTrim);
                sb.AppendLine("AutostartAdmin=" + AutostartAdmin);
                sb.AppendLine("CheckUpdates=" + CheckUpdates);
                sb.AppendLine("PlayitEnabled=" + PlayitEnabled);
                sb.AppendLine("AutoPowerPlan=" + AutoPowerPlan);
                sb.AppendLine("PowerPerfGuid=" + PowerPerfGuid);
                sb.AppendLine("PowerSaverGuid=" + PowerSaverGuid);
                sb.AppendLine("PowerOriginalGuid=" + PowerOriginalGuid);
                sb.AppendLine("StaggerStart=" + StaggerStart);
                sb.AppendLine("SaverWhenPaused=" + SaverWhenPaused);
                sb.AppendLine("ThemeMode=" + ThemeMode);
                Ini.Write(FilePath, sb);
            }
        }
    }
}
