using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MineServer
{
    public class AppConfig
    {
        public string ServerType = "Paper";
        public string McVersion = "";
        public string ServerDir = "";
        public int RamGB = 10;
        public string JavaPath = "";
        public string LaunchMode = "jar";   // "jar" oppure "args" (Forge moderno)
        public string LaunchTarget = "";
        public bool Installed = false;
        public bool AutoRestart = true;
        public bool StartServerOnLaunch = true;
        public bool BackupEnabled = true;
        public string BackupTime = "04:00";
        public int BackupKeep = 7;
        public bool DailyRestart = false;
        public string RestartTime = "05:00";
        public bool Geyser = false;
        public string ServerPriority = "AboveNormal";   // Normal, AboveNormal, High, RealTime
        public bool BoostEnabled = false;
        public bool BoostAggressive = false;
        public bool BoostTrim = true;
        public bool AutostartAdmin = false;
        public bool CheckUpdates = true;

        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        static string FilePath
        {
            get { return Path.Combine(BaseDir, "config.ini"); }
        }

        public string BackupDir
        {
            get { return Path.Combine(BaseDir, "backup"); }
        }

        public static AppConfig Load()
        {
            var c = new AppConfig();
            if (!File.Exists(FilePath)) return c;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { return c; }

            c.ServerType = S(d, "ServerType", c.ServerType);
            c.McVersion = S(d, "McVersion", c.McVersion);
            c.ServerDir = S(d, "ServerDir", c.ServerDir);
            c.RamGB = I(d, "RamGB", c.RamGB);
            c.JavaPath = S(d, "JavaPath", c.JavaPath);
            c.LaunchMode = S(d, "LaunchMode", c.LaunchMode);
            c.LaunchTarget = S(d, "LaunchTarget", c.LaunchTarget);
            c.Installed = B(d, "Installed", c.Installed);
            c.AutoRestart = B(d, "AutoRestart", c.AutoRestart);
            c.StartServerOnLaunch = B(d, "StartServerOnLaunch", c.StartServerOnLaunch);
            c.BackupEnabled = B(d, "BackupEnabled", c.BackupEnabled);
            c.BackupTime = S(d, "BackupTime", c.BackupTime);
            c.BackupKeep = I(d, "BackupKeep", c.BackupKeep);
            c.DailyRestart = B(d, "DailyRestart", c.DailyRestart);
            c.RestartTime = S(d, "RestartTime", c.RestartTime);
            c.Geyser = B(d, "Geyser", c.Geyser);
            c.ServerPriority = S(d, "ServerPriority", c.ServerPriority);
            c.BoostEnabled = B(d, "BoostEnabled", c.BoostEnabled);
            c.BoostAggressive = B(d, "BoostAggressive", c.BoostAggressive);
            c.BoostTrim = B(d, "BoostTrim", c.BoostTrim);
            c.AutostartAdmin = B(d, "AutostartAdmin", c.AutostartAdmin);
            c.CheckUpdates = B(d, "CheckUpdates", c.CheckUpdates);
            return c;
        }

        public void Save()
        {
            lock (this)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("ServerType=" + ServerType);
                    sb.AppendLine("McVersion=" + McVersion);
                    sb.AppendLine("ServerDir=" + ServerDir);
                    sb.AppendLine("RamGB=" + RamGB);
                    sb.AppendLine("JavaPath=" + JavaPath);
                    sb.AppendLine("LaunchMode=" + LaunchMode);
                    sb.AppendLine("LaunchTarget=" + LaunchTarget);
                    sb.AppendLine("Installed=" + Installed);
                    sb.AppendLine("AutoRestart=" + AutoRestart);
                    sb.AppendLine("StartServerOnLaunch=" + StartServerOnLaunch);
                    sb.AppendLine("BackupEnabled=" + BackupEnabled);
                    sb.AppendLine("BackupTime=" + BackupTime);
                    sb.AppendLine("BackupKeep=" + BackupKeep);
                    sb.AppendLine("DailyRestart=" + DailyRestart);
                    sb.AppendLine("RestartTime=" + RestartTime);
                    sb.AppendLine("Geyser=" + Geyser);
                    sb.AppendLine("ServerPriority=" + ServerPriority);
                    sb.AppendLine("BoostEnabled=" + BoostEnabled);
                    sb.AppendLine("BoostAggressive=" + BoostAggressive);
                    sb.AppendLine("BoostTrim=" + BoostTrim);
                    sb.AppendLine("AutostartAdmin=" + AutostartAdmin);
                    sb.AppendLine("CheckUpdates=" + CheckUpdates);
                    File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        static string S(Dictionary<string, string> d, string k, string def)
        {
            string v;
            return d.TryGetValue(k, out v) ? v : def;
        }

        static int I(Dictionary<string, string> d, string k, int def)
        {
            string v; int r;
            return d.TryGetValue(k, out v) && int.TryParse(v, out r) ? r : def;
        }

        static bool B(Dictionary<string, string> d, string k, bool def)
        {
            string v; bool r;
            return d.TryGetValue(k, out v) && bool.TryParse(v, out r) ? r : def;
        }
    }
}
