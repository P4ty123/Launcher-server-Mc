using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MineServer
{
    /// <summary>
    /// Gestisce le priorità: alza il server Minecraft (CPU, disco, RAM) e abbassa tutti gli altri processi.
    /// Salva i valori originali e li ripristina quando si spegne l'interruttore o si chiude il programma.
    /// </summary>
    public class PriorityManager
    {
        // Classi di priorità CPU
        const uint IDLE = 0x40, BELOW = 0x4000, NORMAL = 0x20, ABOVE = 0x8000, HIGH = 0x80, REALTIME = 0x100;
        // Diritti di accesso ai processi
        const uint Q_INFO = 0x0400, Q_LIMITED = 0x1000, SET_INFO = 0x0200, SET_QUOTA = 0x0100;
        // NtSetInformationProcess: priorità I/O (0 = molto bassa, 1 = bassa, 2 = normale, 3 = alta)
        const int ProcessIoPriority = 33;
        // SetProcessInformation: priorità memoria (1 = molto bassa ... 5 = normale)
        const int ProcessMemoryPriority = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attributes; }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetPriorityClass(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetPriorityClass(IntPtr h, uint cls);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessInformation(IntPtr h, int cls, ref MEMORY_PRIORITY_INFORMATION info, int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessInformation(IntPtr h, int cls, ref MEMORY_PRIORITY_INFORMATION info, int size);
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref int info, int len, out int retLen);
        [DllImport("ntdll.dll")] static extern int NtSetInformationProcess(IntPtr h, int cls, ref int info, int len);
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);

        /// <summary>Processi vitali di Windows: non vengono mai toccati.</summary>
        static readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Secure System", "MemCompression", "smss", "csrss", "wininit",
            "winlogon", "services", "lsass", "LsaIso", "dwm", "audiodg", "fontdrvhost", "MsMpEng", "NisSrv",
            "SecurityHealthService", "Taskmgr", "MineServerManager", "LogonUI", "WUDFHost", "dasHost",
            "playitd"  // il tunnel per gli amici: deve restare veloce come il server
        };

        /// <summary>Desktop, input e servizi: anche in modalità Massima scendono solo a "sotto il normale".</summary>
        static readonly HashSet<string> Capped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "sihost", "ctfmon", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost",
            "SearchHost", "svchost", "RuntimeBroker", "conhost", "rdpclip", "spoolsv", "WmiPrvSE"
        };

        class Orig
        {
            public string Name;
            public uint Cls;
            public int Io = -1;
            public uint Mem;
            public bool HasMem;
        }

        readonly Dictionary<int, Orig> changed = new Dictionary<int, Orig>();
        readonly HashSet<int> skipped = new HashSet<int>();
        readonly object sync = new object();
        readonly object passLock = new object();

        public volatile bool Enabled;
        public volatile bool Aggressive;

        public int LoweredCount { get { lock (sync) return changed.Count; } }
        public int SkippedCount { get { lock (sync) return skipped.Count; } }

        // ------------------------------------------------------------ permessi

        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        /// <summary>Attiva i privilegi che servono per "Tempo reale", I/O alto e per modificare i servizi di sistema.</summary>
        public static void EnablePrivileges()
        {
            foreach (var name in new[] { "SeIncreaseBasePriorityPrivilege", "SeDebugPrivilege", "SeIncreaseQuotaPrivilege" })
            {
                IntPtr tok;
                if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out tok)) continue;
                try
                {
                    long luid;
                    if (!LookupPrivilegeValue(null, name, out luid)) continue;
                    var tp = new TOKEN_PRIVILEGES();
                    tp.Count = 1;
                    tp.Luid = luid;
                    tp.Attributes = 2; // SE_PRIVILEGE_ENABLED
                    AdjustTokenPrivileges(tok, false, ref tp, Marshal.SizeOf(tp), IntPtr.Zero, IntPtr.Zero);
                }
                finally { CloseHandle(tok); }
            }
        }

        // ------------------------------------------------------------ server

        public static readonly string[] ServerKeys = { "Normal", "AboveNormal", "High", "RealTime" };
        public static readonly string[] ServerLabels = { "Normale", "Sopra il normale", "Alta (consigliata)", "Tempo reale (rischioso)" };

        static uint ClassFor(string key)
        {
            switch (key)
            {
                case "Normal": return NORMAL;
                case "High": return HIGH;
                case "RealTime": return REALTIME;
                default: return ABOVE;
            }
        }

        public static string ClassName(uint c)
        {
            switch (c)
            {
                case IDLE: return "Bassa";
                case BELOW: return "Sotto il normale";
                case NORMAL: return "Normale";
                case ABOVE: return "Sopra il normale";
                case HIGH: return "Alta";
                case REALTIME: return "Tempo reale";
                default: return "?";
            }
        }

        static int Rank(uint c)
        {
            switch (c)
            {
                case IDLE: return 0;
                case BELOW: return 1;
                case NORMAL: return 2;
                case ABOVE: return 3;
                case HIGH: return 4;
                case REALTIME: return 5;
                default: return 2;
            }
        }

        /// <summary>Imposta la priorità del server. Con boost: anche disco alto e RAM al massimo. Ritorna un avviso o null.</summary>
        public static string ApplyToServer(int pid, string key, bool boost)
        {
            if (pid <= 0) return null;
            IntPtr h = OpenProcess(SET_INFO | Q_LIMITED | Q_INFO, false, pid);
            if (h == IntPtr.Zero) return "Non riesco ad accedere al processo del server.";
            try
            {
                uint want = ClassFor(key);
                SetPriorityClass(h, want);

                int io = boost ? 3 : 2;
                if (NtSetInformationProcess(h, ProcessIoPriority, ref io, 4) != 0 && boost)
                {
                    io = 2;
                    NtSetInformationProcess(h, ProcessIoPriority, ref io, 4);
                }
                var mp = new MEMORY_PRIORITY_INFORMATION();
                mp.MemoryPriority = 5;
                SetProcessInformation(h, ProcessMemoryPriority, ref mp, Marshal.SizeOf(mp));

                uint got = GetPriorityClass(h);
                if (got != want)
                    return "Windows ha concesso la priorità '" + ClassName(got) + "' invece di '" + ClassName(want) +
                           "': servono i permessi di amministratore (scheda Prestazioni).";
                return null;
            }
            finally { CloseHandle(h); }
        }

        public static string CurrentClassName(int pid)
        {
            if (pid <= 0) return "";
            IntPtr h = OpenProcess(Q_LIMITED, false, pid);
            if (h == IntPtr.Zero) return "?";
            try { return ClassName(GetPriorityClass(h)); }
            finally { CloseHandle(h); }
        }

        // ------------------------------------------------------------ tutti gli altri processi

        /// <summary>Abbassa tutti i processi non ancora toccati. Si può chiamare spesso: lavora solo sui nuovi.</summary>
        public void ApplyAll(Func<int> serverPid, bool trim)
        {
            lock (passLock)
            {
                if (!Enabled) return;
                int self = Process.GetCurrentProcess().Id;
                var alive = new HashSet<int>();
                Process[] procs;
                try { procs = Process.GetProcesses(); }
                catch { return; }

                foreach (var p in procs)
                {
                    int pid;
                    string name;
                    try { pid = p.Id; name = p.ProcessName; }
                    catch { p.Dispose(); continue; }
                    p.Dispose();
                    alive.Add(pid);

                    if (!Enabled) break;
                    if (pid <= 4 || pid == self || pid == serverPid()) continue;
                    if (Excluded.Contains(name)) continue;
                    bool known;
                    lock (sync) known = changed.ContainsKey(pid) || skipped.Contains(pid);
                    if (known) continue;
                    LowerOne(pid, name, trim);
                }

                lock (sync)
                {
                    foreach (var k in new List<int>(changed.Keys))
                        if (!alive.Contains(k)) changed.Remove(k);
                    skipped.RemoveWhere(k => !alive.Contains(k));
                }
            }
        }

        void LowerOne(int pid, string name, bool trim)
        {
            IntPtr h = OpenProcess(Q_INFO | Q_LIMITED | SET_INFO | SET_QUOTA, false, pid);
            if (h == IntPtr.Zero) h = OpenProcess(Q_LIMITED | SET_INFO, false, pid);
            if (h == IntPtr.Zero) { lock (sync) skipped.Add(pid); return; }
            try
            {
                var o = new Orig();
                o.Name = name;
                o.Cls = GetPriorityClass(h);
                if (o.Cls == 0 || o.Cls == REALTIME) { lock (sync) skipped.Add(pid); return; }

                bool max = Aggressive && !Capped.Contains(name);
                bool any = false;

                uint target = max ? IDLE : BELOW;
                if (Rank(o.Cls) > Rank(target)) any |= SetPriorityClass(h, target);

                int io = 0, rl;
                if (NtQueryInformationProcess(h, ProcessIoPriority, ref io, 4, out rl) == 0)
                {
                    int nio = max ? 0 : 1;
                    if (nio < io && NtSetInformationProcess(h, ProcessIoPriority, ref nio, 4) == 0)
                    {
                        o.Io = io;
                        any = true;
                    }
                }

                var mp = new MEMORY_PRIORITY_INFORMATION();
                if (GetProcessInformation(h, ProcessMemoryPriority, ref mp, Marshal.SizeOf(mp)))
                {
                    var nm = new MEMORY_PRIORITY_INFORMATION();
                    nm.MemoryPriority = (uint)(max ? 1 : 2);
                    if (nm.MemoryPriority < mp.MemoryPriority &&
                        SetProcessInformation(h, ProcessMemoryPriority, ref nm, Marshal.SizeOf(nm)))
                    {
                        o.Mem = mp.MemoryPriority;
                        o.HasMem = true;
                        any = true;
                    }
                }

                if (trim) EmptyWorkingSet(h);

                lock (sync)
                {
                    if (any) changed[pid] = o;
                    else skipped.Add(pid);
                }
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Toglie un processo dall'elenco (es. il nuovo server appena avviato).</summary>
        public void Forget(int pid)
        {
            lock (sync) { changed.Remove(pid); skipped.Remove(pid); }
        }

        /// <summary>Riporta tutti i processi modificati ai valori originali.</summary>
        public void RestoreAll()
        {
            lock (passLock)
            {
                List<KeyValuePair<int, Orig>> list;
                lock (sync)
                {
                    list = new List<KeyValuePair<int, Orig>>(changed);
                    changed.Clear();
                    skipped.Clear();
                }
                foreach (var kv in list)
                {
                    try
                    {
                        using (var p = Process.GetProcessById(kv.Key))
                            if (!string.Equals(p.ProcessName, kv.Value.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    catch { continue; }

                    IntPtr h = OpenProcess(SET_INFO | Q_LIMITED, false, kv.Key);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        SetPriorityClass(h, kv.Value.Cls);
                        if (kv.Value.Io >= 0)
                        {
                            int io = kv.Value.Io;
                            NtSetInformationProcess(h, ProcessIoPriority, ref io, 4);
                        }
                        if (kv.Value.HasMem)
                        {
                            var mp = new MEMORY_PRIORITY_INFORMATION();
                            mp.MemoryPriority = kv.Value.Mem;
                            SetProcessInformation(h, ProcessMemoryPriority, ref mp, Marshal.SizeOf(mp));
                        }
                    }
                    finally { CloseHandle(h); }
                }
            }
        }
    }
}
