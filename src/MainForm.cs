using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MineServer
{
    public class MainForm : Form
    {
        readonly GlobalConfig gcfg;
        readonly List<ServerSlot> slots = new List<ServerSlot>();
        ServerSlot cur;
        /// <summary>Il server scelto nel menu in alto: tutte le schede lavorano su questo.</summary>
        AppConfig cfg { get { return cur.Cfg; } }
        ServerProcess server { get { return cur.Proc; } }
        readonly List<ServerSlot> startQueue = new List<ServerSlot>();   // accensione scaglionata
        DateTime nextQueuedStart = DateTime.MinValue;
        readonly bool autostartMode, startServerFlag;
        readonly PriorityManager prio = new PriorityManager();
        volatile bool boostRunning;
        bool suppress;
        bool loadingServer;   // true mentre riempio le schede con i dati di un altro server (niente salvataggi)
        bool reallyExit, busy, trayTipShown;
        int tickCount, loadToken;
        string lastMinute = "";
        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);
        readonly Dictionary<TabPage, Action> layouts = new Dictionary<TabPage, Action>();
        Timer timer;
        NotifyIcon tray;
        TabControl tabs;

        // Installa
        ComboBox cbType, cbVersion;
        TextBox txtDir, txtInstallLog;
        CheckBox chkOptim, chkGeyser, chkEula;
        Button btnInstall, btnRefreshVers, btnModpack, btnClientMods, btnDisabledMods;
        ProgressBar progress;
        Label lblStatus, lblInstalled;
        string lastDefaultDir = "";

        // Console
        Label lblState, lblStats;
        Button btnStart, btnStop, btnRestart;
        TextBox txtLog, txtCmd;
        CheckBox chkAutoRestart;
        NumericUpDown numRadius;

        // Impostazioni
        DataGridView grid;
        NumericUpDown numRam;
        Label lblPropsInfo;
        CheckBox chkAllProps;

        // Mod
        Label lblAddon;
        TextBox txtSearch;
        ListView lvResults;
        ListBox lbInstalled;
        readonly List<Control> modControls = new List<Control>();
        List<ModHit> lastHits = new List<ModHit>();

        // Backup
        CheckBox chkBackup, chkRestart;
        DateTimePicker dtBackup, dtRestart;
        NumericUpDown numKeep;
        ListBox lbBackups;

        // Sistema
        CheckBox chkPower, chkFirewall, chkAutostartWin;
        Label lblIps;
        CheckBox chkAutostartAdmin, chkAutoUpdate;
        Label lblUpdate;
        Button btnUpdate;
        UpdateInfo pendingUpdate;
        bool updateBusy;

        // Prestazioni
        Label lblAdmin, lblPrioNow, lblBoost;
        Button btnElevate;
        ComboBox cbPrio, cbLevel;
        CheckBox chkBoost, chkTrim;

        // Pausa automatica e piani energetici
        readonly object powerLock = new object();
        string powerNow;
        CheckBox chkIdle, chkPowerAuto, chkSaverPause;
        NumericUpDown numIdle;
        Label lblIdle;

        // Personalizza
        TextBox txtMotd1, txtMotd2, txtWelcome, txtLeave, txtStartMsg, txtPauseMotd, txtWakeMsg, lastTextFocus;
        int maxPlayers = 20;   // dalla tabella di server.properties (scheda 3)
        PictureBox picIcon;
        ServerListPreview listPreview;
        ChatPreview chatPreview;
        Bitmap pendingIcon;
        bool removeIcon, personalizeLoaded;
        Label lblPersInfo;
        static readonly Regex JoinRx = new Regex(@"\]: ([.*]?[A-Za-z0-9_]{1,16}) joined the game\s*$", RegexOptions.Compiled);
        static readonly Regex LeftRx = new Regex(@"\]: ([.*]?[A-Za-z0-9_]{1,16}) left the game\s*$", RegexOptions.Compiled);

        static readonly string[] TypeLabels =
        {
            "Vanilla  —  server ufficiale Mojang, nessuna modifica",
            "Paper  —  vanilla ottimizzato + plugin  (consigliato per il mini PC)",
            "Fabric  —  mod, ottime mod di performance",
            "Forge  —  mod classiche e modpack (1.18 - 1.20.1)",
            "NeoForge  —  modpack recenti (1.20.2 e successive)"
        };

        static readonly Dictionary<string, string> PropHelp = new Dictionary<string, string>
        {
            { "motd", "Descrizione nella lista server: si cambia nella scheda 7. Personalizza" },
            { "max-players", "Numero massimo di giocatori" },
            { "view-distance", "Distanza visiva in chunk. Più bassa = più leggero (consigliato 6-8)" },
            { "simulation-distance", "Distanza in cui il mondo 'vive' (mob, piante). Consigliato 4-6" },
            { "difficulty", "peaceful, easy, normal, hard" },
            { "gamemode", "survival, creative, adventure, spectator" },
            { "pvp", "true/false: i giocatori possono colpirsi" },
            { "online-mode", "true = solo account Minecraft originali" },
            { "white-list", "true = entrano solo i giocatori in whitelist (comando: whitelist add Nome)" },
            { "server-port", "Porta del server (di solito 25565)" },
            { "spawn-protection", "Raggio protetto attorno allo spawn (0 = nessuno)" },
            { "level-name", "Nome della cartella del mondo" },
            { "level-seed", "Seed del mondo (vale solo per mondi nuovi)" },
            { "hardcore", "true = una sola vita" },
            { "allow-flight", "true = evita kick ingiusti per 'volo' quando c'è lag" },
            { "sync-chunk-writes", "false = salvataggi più veloci (consigliato su SSD)" },
            { "network-compression-threshold", "256 = meno lavoro per la CPU" },
            { "entity-broadcast-range-percentage", "Distanza a cui si vedono le entità (%). 75 = più leggero" },
            { "enable-command-block", "true/false: blocchi comandi attivi" },
            { "max-tick-time", "-1 = il server non si chiude da solo se va in lag forte" }
        };

        public MainForm(bool autostart, bool startServer)
        {
            autostartMode = autostart;
            startServerFlag = startServer;
            gcfg = GlobalConfig.Load();
            foreach (var c in gcfg.Servers) AddSlot(c);
            cur = slots.FirstOrDefault(x => x.Cfg.Id == gcfg.CurrentId) ?? slots[0];

            Text = "MineServer Manager " + AppInfo.Version + "  -  " + cur.Cfg.DisplayName;
            Size = new Size(960, 720);
            MinimumSize = new Size(880, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            Icon = AppIcon();

            tabs = new ThemedTabControl();
            tabs.Dock = DockStyle.Fill;
            Controls.Add(tabs);
            BuildServerBar();

            BuildServersTab();
            BuildInstallTab();
            BuildConsoleTab();
            BuildSettingsTab();
            BuildModsTab();
            BuildAutoTab();
            BuildPlayersTab();
            BuildCustomizeTab();
            BuildPcTab();
            BuildTray();

            tabs.SelectedIndexChanged += (s, e) => OnTabChanged();

            timer = new Timer();
            timer.Interval = 250;
            timer.Tick += (s, e) => OnTick();
            timer.Start();
        }

        // ================================================================== utilità UI

        void UI(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(a); } catch { }
            }
            else a();
        }

        void Msg(string s)
        {
            MessageBox.Show(this, s, "MineServer Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        bool Ask(string s)
        {
            return MessageBox.Show(this, s, "MineServer Manager", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        TabPage NewTab(string title)
        {
            var p = new TabPage(title);
            p.UseVisualStyleBackColor = true;
            tabs.TabPages.Add(p);
            return p;
        }

        static T Put<T>(Control parent, T c, int x, int y, int w, int h) where T : Control
        {
            c.Location = new Point(x, y);
            if (w > 0) c.Width = w;
            if (h > 0) c.Height = h;
            parent.Controls.Add(c);
            return c;
        }

        static Label Lbl(Control p, string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            return Put(p, l, x, y + 3, 0, 0);
        }

        static Button Btn(Control p, string text, int x, int y, int w, EventHandler click)
        {
            var b = new Button();
            b.Text = text;
            b.Click += click;
            return Put(p, b, x, y, w, 28);
        }

        static CheckBox Chk(Control p, string text, int x, int y, bool value)
        {
            var c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Checked = value;
            return Put(p, c, x, y, 0, 0);
        }

        void RegisterLayout(TabPage p, Action a)
        {
            layouts[p] = a;
            p.Resize += (s, e) => a();
        }

        void LayoutAll()
        {
            foreach (var a in layouts.Values) a();
        }

        static Icon AppIcon()
        {
            try
            {
                var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ic != null) return ic;
            }
            catch { }
            return MakeIcon();
        }

        static Icon MakeIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            using (var dirt = new SolidBrush(Color.FromArgb(134, 96, 67)))
            using (var dirt2 = new SolidBrush(Color.FromArgb(105, 74, 52)))
            using (var grass = new SolidBrush(Color.FromArgb(106, 170, 64)))
            using (var grass2 = new SolidBrush(Color.FromArgb(76, 140, 44)))
            {
                g.Clear(Color.Transparent);
                g.FillRectangle(dirt, 2, 2, 28, 28);
                g.FillRectangle(dirt2, 8, 18, 4, 4);
                g.FillRectangle(dirt2, 20, 24, 4, 4);
                g.FillRectangle(dirt2, 22, 14, 3, 3);
                g.FillRectangle(grass, 2, 2, 28, 9);
                g.FillRectangle(grass2, 6, 11, 4, 4);
                g.FillRectangle(grass2, 16, 11, 4, 6);
                g.FillRectangle(grass2, 26, 11, 4, 3);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        string SelType()
        {
            int i = cbType.SelectedIndex;
            return Installer.Types[i < 0 ? 1 : i];
        }

        string DefaultDir(string type)
        {
            return ServerTools.DefaultDir(type, gcfg.Servers, cfg);
        }

        string AddonDir()
        {
            return Path.Combine(cfg.ServerDir, Installer.AddonFolder(cfg.ServerType));
        }

        static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { }
        }

        // ================================================================== 1. INSTALLA

        void BuildInstallTab()
        {
            var p = NewTab("1. Installa");
            lblInstalled = Lbl(p, "", 12, 10);
            lblInstalled.Font = new Font(Font, FontStyle.Bold);

            Lbl(p, "Tipo di server:", 12, 45);
            cbType = new ComboBox();
            cbType.DropDownStyle = ComboBoxStyle.DropDownList;
            cbType.Items.AddRange(TypeLabels);
            Put(p, cbType, 160, 45, 460, 0);

            Lbl(p, "Versione Minecraft:", 12, 80);
            cbVersion = new ComboBox();
            cbVersion.DropDownStyle = ComboBoxStyle.DropDownList;
            cbVersion.MaxDropDownItems = 20;
            Put(p, cbVersion, 160, 80, 200, 0);
            btnRefreshVers = Btn(p, "Aggiorna elenco", 370, 78, 130, (s, e) => LoadVersions());

            Lbl(p, "Cartella del server:", 12, 115);
            txtDir = Put(p, new TextBox(), 160, 115, 460, 0);
            Btn(p, "Sfoglia...", 630, 113, 90, (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "Scegli la cartella del server (meglio se vuota)";
                    if (d.ShowDialog(this) == DialogResult.OK) txtDir.Text = d.SelectedPath;
                }
            });

            chkOptim = Chk(p, "", 160, 150, true);
            chkGeyser = Chk(p, "Fai entrare anche giocatori Bedrock (telefono, console) con Geyser", 160, 175, cfg.Geyser);
            chkEula = Chk(p, "Accetto l'EULA di Minecraft (obbligatoria per avviare un server)", 160, 200, false);
            var link = new LinkLabel();
            link.Text = "leggi l'EULA";
            link.AutoSize = true;
            link.LinkClicked += (s, e) => { try { Process.Start("https://aka.ms/MinecraftEULA"); } catch { } };
            Put(p, link, 560, 201, 0, 0);

            btnInstall = Btn(p, "Installa server", 160, 235, 200, (s, e) => DoInstall());
            btnInstall.Height = 36;
            btnInstall.Font = new Font(Font, FontStyle.Bold);
            btnModpack = Btn(p, "Importa modpack...", 370, 235, 200, (s, e) => ImportModpack());
            btnModpack.Height = 36;
            Lbl(p, "Da Modrinth o CurseForge, tutto in un colpo.", 580, 245);

            progress = Put(p, new ProgressBar(), 160, 282, 460, 18);
            lblStatus = Lbl(p, "", 630, 280);

            txtInstallLog = new TextBox();
            txtInstallLog.Multiline = true;
            txtInstallLog.ReadOnly = true;
            txtInstallLog.ScrollBars = ScrollBars.Vertical;
            txtInstallLog.BackColor = Color.White;
            Put(p, txtInstallLog, 12, 312, 800, 200);

            RegisterLayout(p, () =>
            {
                if (p.ClientSize.Width < 200) return;
                txtInstallLog.Size = new Size(p.ClientSize.Width - 24, Math.Max(60, p.ClientSize.Height - 312 - 12));
            });

            cbType.SelectedIndexChanged += (s, e) => OnTypeChanged();
            int ti = Array.IndexOf(Installer.Types, cfg.ServerType);
            cbType.SelectedIndex = ti < 0 ? 1 : ti;
            if (cfg.Installed && cfg.ServerDir != "") txtDir.Text = cfg.ServerDir;
        }

        void OnTypeChanged()
        {
            string t = SelType();
            string def = DefaultDir(t);
            if (txtDir.Text.Trim() == "" || txtDir.Text == lastDefaultDir) txtDir.Text = def;
            lastDefaultDir = def;

            if (t == "Vanilla")
            {
                chkOptim.Text = "Su Vanilla non si possono aggiungere ottimizzazioni (per quelle scegli Paper o Fabric)";
                chkOptim.Enabled = false;
            }
            else if (t == "Paper")
            {
                chkOptim.Text = "Installa il plugin Chunky (serve per pre-generare il mondo)";
                chkOptim.Enabled = true;
            }
            else
            {
                chkOptim.Text = "Installa le mod di ottimizzazione: " + string.Join(", ", Installer.Recommended(t));
                chkOptim.Enabled = true;
            }
            chkGeyser.Enabled = t == "Paper" || t == "Fabric" || t == "NeoForge";
            if (!chkGeyser.Enabled) chkGeyser.Checked = false;
            if (IsHandleCreated) LoadVersions();
        }

        void LoadVersions()
        {
            string t = SelType();
            int token = ++loadToken;
            cbVersion.Items.Clear();
            cbVersion.Items.Add("Caricamento...");
            cbVersion.SelectedIndex = 0;
            cbVersion.Enabled = false;
            Task.Run(() =>
            {
                List<string> vers = null;
                string err = null;
                try { vers = Installer.GetVersions(t); }
                catch (Exception ex) { err = ex.Message; }
                UI(() =>
                {
                    if (token != loadToken) return;
                    cbVersion.Items.Clear();
                    cbVersion.Enabled = true;
                    if (vers == null || vers.Count == 0)
                    {
                        cbVersion.Items.Add("(errore: controlla internet)");
                        cbVersion.SelectedIndex = 0;
                        AppendInstallLog("Impossibile scaricare l'elenco delle versioni: " + (err ?? "nessuna versione trovata"));
                        return;
                    }
                    cbVersion.Items.AddRange(vers.ToArray());
                    int i = cfg.ServerType == t ? vers.IndexOf(cfg.McVersion) : -1;
                    cbVersion.SelectedIndex = i >= 0 ? i : 0;
                });
            });
        }

        void AppendInstallLog(string s)
        {
            if (txtInstallLog.TextLength > 300000) txtInstallLog.Clear();
            txtInstallLog.AppendText(s + "\r\n");
        }

        void SetBusy(bool b, string status)
        {
            busy = b;
            btnInstall.Enabled = !b;
            if (btnModpack != null) btnModpack.Enabled = !b;
            cbType.Enabled = !b;
            cbVersion.Enabled = !b;
            btnRefreshVers.Enabled = !b;
            lblStatus.Text = status;
            if (!b)
            {
                progress.Style = ProgressBarStyle.Blocks;
                progress.Value = 0;
            }
        }

        void DoInstall()
        {
            if (busy) return;
            if (server.IsRunning) { Msg("Ferma il server prima di installare o aggiornare."); return; }
            if (!chkEula.Checked) { Msg("Per installare un server devi accettare l'EULA di Minecraft."); return; }
            string t = SelType();
            string ver = cbVersion.SelectedItem as string;
            if (ver == null || ver.Length == 0 || !char.IsDigit(ver[0])) { Msg("Scegli una versione di Minecraft."); return; }
            string dir = txtDir.Text.Trim();
            if (dir == "") { Msg("Scegli una cartella per il server."); return; }

            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            {
                bool same = cfg.Installed && string.Equals(Path.GetFullPath(cfg.ServerDir).TrimEnd('\\'),
                    Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && cfg.ServerType == t;
                string q = same
                    ? "In questa cartella c'è già un server " + t + ".\nLo aggiorno a " + ver + " mantenendo il mondo?\n\n(Consiglio: fai prima un backup dalla scheda 5. Backup, riavvii e pausa.)"
                    : "La cartella non è vuota.\nMettere un tipo di server diverso nella stessa cartella può creare problemi (mod incompatibili).\n\nContinuare lo stesso?";
                if (!Ask(q)) return;
            }

            bool optim = chkOptim.Enabled && chkOptim.Checked;
            bool gey = chkGeyser.Enabled && chkGeyser.Checked;
            SetBusy(true, "Installazione in corso...");

            var inst = new Installer();
            inst.Log = s => UI(() => AppendInstallLog(s));
            inst.Progress = pc => UI(() =>
            {
                if (pc < 0) progress.Style = ProgressBarStyle.Marquee;
                else
                {
                    progress.Style = ProgressBarStyle.Blocks;
                    progress.Value = Math.Max(0, Math.Min(100, pc));
                }
            });

            Task.Run(() =>
            {
                string err = null;
                try { inst.InstallServer(cfg, t, ver, dir, optim, gey); }
                catch (Exception ex) { err = ex.Message; }
                UI(() =>
                {
                    SetBusy(false, err == null ? "Fatto!" : "Errore");
                    if (err != null)
                    {
                        AppendInstallLog("ERRORE: " + err);
                        Msg("Installazione non riuscita:\n\n" + err);
                        return;
                    }
                    AppendInstallLog("=== Installazione completata ===");
                    EnsureUniquePort(cur);
                    RefreshAll();
                    Msg("Server installato!\n\nVai nella scheda Console e premi Avvia.\nIl primo avvio è più lento perché genera il mondo.");
                    tabs.SelectedIndex = 2;
                });
            });
        }

        void UpdateInstalledLabel()
        {
            lblInstalled.Text = cfg.Installed
                ? "Server installato: " + cfg.ServerType + " " + cfg.McVersion + "   →   " + cfg.ServerDir
                : "Nessun server installato: scegli tipo e versione, poi premi Installa.";
            lblInstalled.ForeColor = cfg.Installed ? Color.DarkGreen : Color.DarkOrange;
        }

        // ================================================================== 2. CONSOLE

        void BuildConsoleTab()
        {
            var p = NewTab("2. Console");
            lblState = Lbl(p, "● Fermo", 12, 12);
            lblState.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            btnStart = Btn(p, "Avvia", 150, 10, 100, (s, e) => StartServer());
            btnStop = Btn(p, "Ferma", 255, 10, 100, (s, e) => StopServer());
            btnRestart = Btn(p, "Riavvia", 360, 10, 100, (s, e) => RestartServer());
            lblStats = Lbl(p, "", 480, 14);


            txtLog = new TextBox();
            txtLog.Tag = "keep";   // la console è già scura
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.FromArgb(24, 24, 24);
            txtLog.ForeColor = Color.Gainsboro;
            txtLog.Font = new Font("Consolas", 9F);
            Put(p, txtLog, 12, 46, 800, 300);

            txtCmd = Put(p, new TextBox(), 12, 400, 500, 0);
            txtCmd.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { SendCmd(); e.SuppressKeyPress = true; }
            };
            var btnSend = Btn(p, "Invia comando", 520, 398, 110, (s, e) => SendCmd());

            var bList = Btn(p, "Chi è online?", 12, 440, 110, (s, e) => QuickCmd("list"));
            var bSave = Btn(p, "Salva mondo", 128, 440, 110, (s, e) => QuickCmd("save-all"));
            var lblR = Lbl(p, "Raggio (blocchi):", 260, 440);
            numRadius = new NumericUpDown();
            numRadius.Minimum = 500;
            numRadius.Maximum = 20000;
            numRadius.Increment = 500;
            numRadius.Value = 3000;
            Put(p, numRadius, 365, 443, 75, 0);
            var bGen = Btn(p, "Pre-genera mondo", 448, 440, 140, (s, e) => Pregen());
            var bStopGen = Btn(p, "Ferma pre-generazione", 594, 440, 160, (s, e) => StopPregen());
            var bLag = Btn(p, "Trova cosa fa lag", 760, 440, 140, (s, e) => FindLag());

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width, h = p.ClientSize.Height;
                if (w < 200 || h < 200) return;
                txtLog.Size = new Size(w - 24, h - 46 - 90);
                int y1 = h - 80;
                txtCmd.Location = new Point(12, y1 + 3);
                txtCmd.Width = w - 24 - 120;
                btnSend.Location = new Point(w - 12 - 110, y1);
                int y2 = h - 40;
                bList.Top = y2; bSave.Top = y2; bGen.Top = y2; bStopGen.Top = y2; bLag.Top = y2;
                lblR.Top = y2 + 6;
                numRadius.Top = y2 + 3;
            });
        }

        void StartServer()
        {
            StartSlot(cur, false);
        }

        /// <summary>Accende un server. quiet = all'accensione del PC / automatico: niente finestre, solo log.</summary>
        bool StartSlot(ServerSlot s, bool quiet)
        {
            if (!s.Cfg.Installed) { if (!quiet) Msg("Prima installa il server (scheda 1)."); return false; }
            if (s.Proc.IsRunning) return true;
            if (s.Sleeping)
            {
                s.Sleeping = false;
                s.Wake.Stop();
            }
            var other = ServerTools.PortConflict(s, slots);
            if (other != null)
            {
                string m = "'" + s.Cfg.DisplayName + "' usa la stessa porta di '" + other.Cfg.DisplayName + "', che è già acceso.\n" +
                           "Cambia la porta (server-port) nella scheda Impostazioni di uno dei due.";
                s.Add("[Manager] Non avviato: " + m.Replace("\n", " "));
                if (!quiet) Msg(m);
                RefreshServerList();
                return false;
            }
            int need = s.Cfg.RamGB, used = slots.Where(x => x != s && x.IsActive).Sum(x => x.Cfg.RamGB);
            int total = ServerTools.TotalRamGB();
            if (total > 0 && used > 0 && need + used > total - 2)
            {
                string m = "RAM insufficiente: '" + s.Cfg.DisplayName + "' chiede " + need + " GB, gli altri server ne usano già " + used +
                           " GB e il PC ne ha " + total + ".";
                if (quiet) { s.Add("[Manager] Non avviato: " + m); RefreshServerList(); return false; }
                if (!Ask(m + "\n\nWindows diventerebbe molto lento. Accendere lo stesso?\n(Consiglio: abbassa la RAM nella scheda Impostazioni o spegni un altro server.)"))
                    return false;
            }
            if (s.Cfg.Geyser)
            {
                foreach (var f in ServerTools.GeyserConfigs(s.Cfg.ServerDir))
                {
                    try { if (ServerTools.SetGeyserPort(f, ServerTools.BedrockPort(s.Cfg))) s.Add("[Manager] Porta Bedrock impostata a " + ServerTools.BedrockPort(s.Cfg) + "."); }
                    catch { }
                }
            }
            s.EmptySince = DateTime.MinValue;
            s.LastPlayers = -1;
            try { s.Proc.Start(); }
            catch (Exception ex)
            {
                s.Add("[Manager] Impossibile avviare il server: " + ex.Message);
                if (!quiet) Msg("Impossibile avviare '" + s.Cfg.DisplayName + "':\n" + ex.Message);
                return false;
            }
            if (s == cur) UpdateState();
            RefreshServerList();
            return true;
        }

        void StopServer()
        {
            StopSlot(cur);
        }

        void StopSlot(ServerSlot s)
        {
            if (s.Sleeping)
            {
                s.Sleeping = false;
                s.Wake.Stop();
                s.Add("[Pausa] Pausa annullata: il server resta spento e non si riaccenderà da solo.");
                if (s == cur) UpdateState();
                RefreshServerList();
                Task.Run(() => ApplyPowerPlan());
                return;
            }
            if (!s.Proc.IsRunning) return;
            if (s == cur)
            {
                lblState.Text = "● Arresto in corso...";
                lblState.ForeColor = Color.DarkOrange;
            }
            Task.Run(() => s.Proc.Stop());
            RefreshServerList();
        }

        void RestartServer()
        {
            RestartSlot(cur);
        }

        void RestartSlot(ServerSlot s)
        {
            if (!s.Cfg.Installed) return;
            if (s == cur)
            {
                lblState.Text = "● Riavvio in corso...";
                lblState.ForeColor = Color.DarkOrange;
            }
            Task.Run(() =>
            {
                try { s.Proc.Restart(); }
                catch (Exception ex) { s.Add("[Manager] Errore nel riavvio: " + ex.Message); }
            });
        }

        /// <summary>Messaggio nella console del server scelto.</summary>
        void Log(string line)
        {
            cur.Add(line);
        }

        void SendCmd()
        {
            string c = txtCmd.Text.Trim();
            if (c == "") return;
            if (c.StartsWith("/")) c = c.Substring(1);
            if (server.SendCommand(c)) Log("> " + c);
            else Log("[Manager] Il server non è acceso.");
            txtCmd.Clear();
        }

        void QuickCmd(string c)
        {
            if (server.SendCommand(c)) Log("> " + c);
            else Msg("Il server non è acceso.");
        }

        bool HasAddon(string name)
        {
            string f = AddonDir();
            return Directory.Exists(f) &&
                   Directory.GetFiles(f, "*.jar").Any(x => Path.GetFileName(x).ToLower().Contains(name));
        }

        void Pregen()
        {
            if (cfg.ServerType == "Vanilla") { Msg("Su Vanilla la pre-generazione non è disponibile: serve Paper, Fabric o Forge con Chunky."); return; }
            if (!server.IsRunning) { Msg("Avvia prima il server."); return; }
            if (!HasAddon("chunky")) { Msg("Serve Chunky: installalo dalla scheda 'Mod / Plugin' (cerca \"Chunky\"), poi riavvia il server."); return; }
            int r = (int)numRadius.Value;
            server.SendCommand("chunky radius " + r);
            server.SendCommand("chunky start");
            Log("[Manager] Pre-generazione avviata (raggio " + r + " blocchi). Sull'N95 può richiedere qualche ora: puoi giocare intanto, ma è meglio lasciarlo lavorare da solo.");
        }

        /// <summary>
        /// Ferma la pre-generazione di Chunky. Chunky chiede di confermare con "chunky confirm":
        /// lo mando subito io, così non bisogna scriverlo mentre la console scorre.
        /// </summary>
        void StopPregen()
        {
            if (!server.IsRunning) { Msg("Il server non è acceso."); return; }
            server.SendCommand("chunky cancel");
            Log("> chunky cancel");
            server.SendCommand("chunky confirm");
            Log("> chunky confirm");
            cur.LastChunky = DateTime.MinValue;   // la pausa automatica torna a funzionare subito
            Log("[Manager] Pre-generazione fermata. I pezzi di mondo già generati restano salvati; se la riavvii, Chunky li salta in fretta.");
        }

        void UpdateState()
        {
            bool r = server.IsRunning;
            if (!r && cur.Sleeping)
            {
                lblState.Text = "● In pausa";
                lblState.ForeColor = Color.SteelBlue;
            }
            else if (!(lblState.Text.Contains("in corso") && r))
            {
                lblState.Text = r ? "● Acceso" : "● Fermo";
                lblState.ForeColor = r ? Color.ForestGreen : Color.Firebrick;
            }
            btnStart.Enabled = !r && cfg.Installed;
            btnStop.Enabled = r || cur.Sleeping;
            btnRestart.Enabled = cfg.Installed;
            if (tray != null)
            {
                int on = slots.Count(x => x.IsRunning), zz = slots.Count(x => x.Sleeping);
                string t = "MineServer Manager - " + (on == 0 ? "nessun server acceso" : on == 1 ? "1 server acceso" : on + " server accesi") +
                           (zz > 0 ? ", " + zz + " in pausa" : "");
                tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            }
            UpdateServerBarState();
        }

        void UpdateStats()
        {
            long ram; double cpu; TimeSpan up;
            if (server.GetStats(out ram, out cpu, out up))
            {
                string upS = up.TotalHours >= 1
                    ? ((int)up.TotalHours) + "h " + up.Minutes + "m"
                    : up.Minutes + "m " + up.Seconds + "s";
                lblStats.Text = "RAM usata: " + ram + " MB    CPU: " + cpu.ToString("0") + "%    Acceso da: " + upS +
                                "    Priorità: " + PriorityManager.CurrentClassName(server.Pid);
                if (lblState.Text.Contains("in corso") && (DateTime.Now - server.StartedAt).TotalSeconds > 5 && !server.StopRequested)
                    lblState.Text = "● Acceso";
                if (cfg.IdleSleep && cur.EmptySince != DateTime.MinValue)
                    lblStats.Text += "    Vuoto da " + (int)(DateTime.Now - cur.EmptySince).TotalMinutes + " min";
            }
            else lblStats.Text = cur.Sleeping ? "Si riaccende da solo appena un giocatore prova a entrare." : "";
            if (lblPrioNow != null)
                lblPrioNow.Text = server.IsRunning ? "Priorità attuale: " + PriorityManager.CurrentClassName(server.Pid) : "(server spento)";
            UpdateState();
        }

        void FlushLog()
        {
            foreach (var o in slots) if (o != cur) o.DropPending();   // le altre console si rileggono dalla cronologia
            if (!cur.HasPending) return;
            var sb = new StringBuilder();
            string line;
            int n = 0;
            while (n < 400 && cur.TryTake(out line))
            {
                sb.Append(Ansi.Replace(line, "")).Append("\r\n");
                n++;
            }
            if (txtLog.TextLength > 400000) txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 150000);
            txtLog.AppendText(sb.ToString());
        }

        // ================================================================== 3. IMPOSTAZIONI

        void BuildSettingsTab()
        {
            var p = NewTab("3. Impostazioni");
            Lbl(p, "RAM dedicata al server (GB):", 12, 12);
            numRam = new NumericUpDown();
            numRam.Minimum = 2;
            numRam.Maximum = 14;
            numRam.Value = Math.Max(2, Math.Min(14, cfg.RamGB));
            Put(p, numRam, 200, 12, 60, 0);
            Lbl(p, "Con 16 GB totali l'ideale è 8-10 GB. Vale dal prossimo avvio.", 270, 12);
            numRam.ValueChanged += (s, e) => { if (loadingServer) return; cfg.RamGB = (int)numRam.Value; cfg.Save(); RefreshServerList(); };

            lblPropsInfo = Lbl(p, "Impostazioni del mondo: clicca su un valore per cambiarlo, poi Salva e riavvia il server.", 12, 46);
            chkAllProps = Chk(p, "Mostra anche quelle tecniche", 600, 46, false);
            chkAllProps.CheckedChanged += (s, e) => ShowAllProps();

            grid = new DataGridView();
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = SystemColors.Window;
            grid.EditMode = DataGridViewEditMode.EditOnEnter;
            grid.Columns.Add("k", "Impostazione");
            grid.Columns.Add("v", "Valore");
            grid.Columns.Add("d", "A cosa serve");
            grid.Columns[0].ReadOnly = true;
            grid.Columns[2].ReadOnly = true;
            grid.Columns[0].FillWeight = 28;
            grid.Columns[1].FillWeight = 25;
            grid.Columns[2].FillWeight = 60;
            Put(p, grid, 12, 72, 800, 300);

            var bSave = Btn(p, "Salva", 12, 0, 100, (s, e) => SaveProps());
            var bReload = Btn(p, "Ricarica", 118, 0, 100, (s, e) => LoadProps());
            var bPreset = Btn(p, "Ottimizza automaticamente...", 224, 0, 200, (s, e) => OptimizeDialog());
            bPreset.Font = new Font(Font, FontStyle.Bold);
            var bUnopt = Btn(p, "Ripristina originali", 430, 0, 150, (s, e) => RestoreOptimize());
            var bOpen = Btn(p, "Apri cartella server", 586, 0, 160, (s, e) => OpenFolder(cfg.ServerDir));

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width, h = p.ClientSize.Height;
                if (w < 200 || h < 200) return;
                grid.Size = new Size(w - 24, h - 72 - 50);
                int y = h - 40;
                bSave.Top = y; bReload.Top = y; bPreset.Top = y; bUnopt.Top = y; bOpen.Top = y;
            });
        }

        void LoadProps()
        {
            grid.Rows.Clear();
            if (!cfg.Installed) return;
            var props = Props.Read(cfg.ServerDir);
            var dict = new Dictionary<string, string>();
            foreach (var kv in props) dict[kv.Key] = kv.Value;
            foreach (var key in PropHelp.Keys)
            {
                if (!dict.ContainsKey(key)) continue;
                int i = grid.Rows.Add(key, dict[key], PropHelp[key]);
                if (key == "motd")
                {
                    // il testo con i colori è scritto in un formato difficile da leggere: si cambia nella scheda Personalizza
                    grid.Rows[i].Cells[1].ReadOnly = true;
                    grid.Rows[i].Cells[1].Style.ForeColor = SystemColors.GrayText;
                }
            }
            // le altre (tecniche) restano nella tabella, nascoste: così Salva le conserva tutte
            foreach (var kv in props.OrderBy(x => x.Key))
                if (!PropHelp.ContainsKey(kv.Key))
                {
                    int i = grid.Rows.Add(kv.Key, kv.Value, "");
                    grid.Rows[i].Tag = "tecnica";
                }
            ShowAllProps();
        }

        void ShowAllProps()
        {
            grid.CurrentCell = null;
            foreach (DataGridViewRow r in grid.Rows)
                if ("tecnica".Equals(r.Tag)) r.Visible = chkAllProps.Checked;
        }

        void SetGridValue(string key, string value)
        {
            foreach (DataGridViewRow r in grid.Rows)
                if ((string)r.Cells[0].Value == key) { r.Cells[1].Value = value; return; }
            string help;
            PropHelp.TryGetValue(key, out help);
            grid.Rows.Add(key, value, help ?? "");
        }

        void ApplyPreset()
        {
            if (!cfg.Installed) { Msg("Prima installa il server."); return; }
            SetGridValue("view-distance", "7");
            SetGridValue("simulation-distance", "5");
            SetGridValue("sync-chunk-writes", "false");
            SetGridValue("network-compression-threshold", "256");
            SetGridValue("entity-broadcast-range-percentage", "75");
            SetGridValue("allow-flight", "true");
            SetGridValue("max-tick-time", "-1");
            Msg("Preset applicato nella tabella. Premi Salva e poi riavvia il server.");
        }

        void SaveProps()
        {
            if (!cfg.Installed) return;
            grid.EndEdit();
            var list = new List<KeyValuePair<string, string>>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                string k = r.Cells[0].Value as string;
                if (string.IsNullOrEmpty(k)) continue;
                string v = r.Cells[1].Value == null ? "" : r.Cells[1].Value.ToString();
                list.Add(new KeyValuePair<string, string>(k, v));
            }
            try
            {
                Props.Write(cfg.ServerDir, list);
                Msg(server.IsRunning ? "Salvato! Riavvia il server per applicare le modifiche." : "Salvato!");
            }
            catch (Exception ex) { Msg("Errore nel salvataggio: " + ex.Message); }
        }

        // ================================================================== 4. MOD / PLUGIN

        void BuildModsTab()
        {
            var p = NewTab("4. Mod / Plugin");
            lblAddon = Lbl(p, "", 12, 10);
            lblAddon.Font = new Font(Font, FontStyle.Bold);

            modControls.Add(Lbl(p, "Cerca su Modrinth:", 12, 44));
            txtSearch = Put(p, new TextBox(), 130, 44, 300, 0);
            txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { DoSearch(); e.SuppressKeyPress = true; }
            };
            modControls.Add(txtSearch);
            modControls.Add(Btn(p, "Cerca", 440, 42, 80, (s, e) => DoSearch()));
            modControls.Add(Btn(p, "Installa pacchetto ottimizzazione", 530, 42, 230, (s, e) => InstallRecommended()));

            lvResults = new ListView();
            lvResults.View = View.Details;
            lvResults.FullRowSelect = true;
            lvResults.MultiSelect = false;
            lvResults.HideSelection = false;
            lvResults.Columns.Add("Nome", 220);
            lvResults.Columns.Add("Download", 90);
            lvResults.Columns.Add("Descrizione", 480);
            lvResults.DoubleClick += (s, e) => InstallSelectedMod();
            Put(p, lvResults, 12, 76, 800, 210);
            modControls.Add(lvResults);

            var bInst = Btn(p, "Installa selezionata", 12, 292, 160, (s, e) => InstallSelectedMod());
            modControls.Add(bInst);
            btnClientMods = Btn(p, "Controlla mod solo client", 182, 292, 190, (s, e) => CheckClientMods());
            btnDisabledMods = Btn(p, "Mod disattivate...", 378, 292, 140, (s, e) => ShowDisabledMods());
            var lInst = Lbl(p, "Installate (dentro la cartella del server):", 12, 330);
            lbInstalled = new ListBox();
            lbInstalled.IntegralHeight = false;
            Put(p, lbInstalled, 12, 354, 800, 150);
            modControls.Add(lbInstalled);

            var bRem = Btn(p, "Rimuovi selezionata", 12, 0, 160, (s, e) => RemoveMod());
            var bOpen = Btn(p, "Apri cartella", 178, 0, 120, (s, e) => OpenFolder(AddonDir()));
            var bRef = Btn(p, "Aggiorna elenco", 304, 0, 130, (s, e) => RefreshMods());
            var lHint = Lbl(p, "Puoi anche copiare a mano i file .jar nella cartella (es. mod da CurseForge).", 445, 0);
            modControls.Add(bRem); modControls.Add(bOpen); modControls.Add(bRef);

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width, h = p.ClientSize.Height;
                if (w < 200 || h < 200) return;
                lvResults.Width = w - 24;
                lvResults.Columns[2].Width = Math.Max(200, w - 24 - 320 - 25);
                lbInstalled.Size = new Size(w - 24, Math.Max(60, h - 354 - 50));
                int y = h - 40;
                bRem.Top = y; bOpen.Top = y; bRef.Top = y; lHint.Top = y + 6;
            });
        }

        bool ModsUsable()
        {
            return cfg.Installed && cfg.ServerType != "Vanilla";
        }

        void RefreshMods()
        {
            bool ok = ModsUsable();
            if (!cfg.Installed) lblAddon.Text = "Installa prima un server (scheda 1).";
            else if (cfg.ServerType == "Vanilla")
                lblAddon.Text = "Vanilla non supporta mod né plugin. Per usarli installa Paper (plugin), Fabric o Forge (mod).";
            else
                lblAddon.Text = (cfg.ServerType == "Paper" ? "Plugin" : "Mod") + " per " + cfg.ServerType + " " + cfg.McVersion +
                                "  —  cartella: " + AddonDir();
            foreach (var c in modControls) c.Enabled = ok;
            bool modLoader = ok && cfg.ServerType != "Paper";   // i plugin di Paper non hanno mod "solo client"
            btnClientMods.Enabled = modLoader;
            btnDisabledMods.Enabled = modLoader;
            lbInstalled.Items.Clear();
            if (!ok) return;
            string dir = AddonDir();
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.jar").OrderBy(x => x))
                    lbInstalled.Items.Add(Path.GetFileName(f));
        }

        void DoSearch()
        {
            if (!ModsUsable()) return;
            string q = txtSearch.Text.Trim();
            string t = cfg.ServerType, v = cfg.McVersion;
            lvResults.Items.Clear();
            lvResults.Items.Add(new ListViewItem("Ricerca in corso..."));
            Task.Run(() =>
            {
                List<ModHit> hits = null;
                string err = null;
                try { hits = Installer.Search(q, t, v); }
                catch (Exception ex) { err = ex.Message; }
                UI(() =>
                {
                    lvResults.Items.Clear();
                    lastHits = hits ?? new List<ModHit>();
                    if (err != null) { lvResults.Items.Add(new ListViewItem("Errore: " + err)); return; }
                    if (lastHits.Count == 0) { lvResults.Items.Add(new ListViewItem("Nessun risultato compatibile con " + t + " " + v)); return; }
                    foreach (var h in lastHits)
                    {
                        var it = new ListViewItem(h.Title);
                        it.SubItems.Add(h.Downloads.ToString("N0"));
                        it.SubItems.Add(h.Description);
                        it.Tag = h;
                        lvResults.Items.Add(it);
                    }
                });
            });
        }

        void InstallMods(string[] ids, string what)
        {
            if (!ModsUsable()) return;
            if (server.IsRunning) Log("[Mod] Nota: le nuove mod/plugin funzioneranno dopo il riavvio del server.");
            string t = cfg.ServerType, v = cfg.McVersion, dir = cfg.ServerDir;
            lblAddon.Text = "Installo " + what + "...";
            Task.Run(() =>
            {
                var inst = new Installer();
                var lines = new List<string>();
                inst.Log = s => { lock (lines) lines.Add(s); Log("[Mod] " + s); };
                var visited = new HashSet<string>();
                foreach (var id in ids)
                {
                    try { inst.InstallFromModrinth(id, t, v, dir, visited); }
                    catch (Exception ex) { inst.Log(id + ": errore " + ex.Message); }
                }
                UI(() =>
                {
                    RefreshMods();
                    string summary;
                    lock (lines) summary = string.Join("\n", lines.Take(25));
                    Msg("Fatto.\n\n" + summary + (server.IsRunning ? "\n\nRiavvia il server per attivarle." : ""));
                });
            });
        }

        void InstallSelectedMod()
        {
            if (lvResults.SelectedItems.Count == 0) { Msg("Seleziona una mod dall'elenco dei risultati."); return; }
            var h = lvResults.SelectedItems[0].Tag as ModHit;
            if (h == null) return;
            InstallMods(new[] { h.Id }, h.Title);
        }

        void InstallRecommended()
        {
            var rec = Installer.Recommended(cfg.ServerType);
            if (rec.Length == 0) return;
            InstallMods(rec, "pacchetto ottimizzazione");
        }

        void RemoveMod()
        {
            var name = lbInstalled.SelectedItem as string;
            if (name == null) return;
            if (server.IsRunning) { Msg("Ferma il server prima di rimuovere una mod o un plugin."); return; }
            if (!Ask("Rimuovo " + name + "?")) return;
            try { File.Delete(Path.Combine(AddonDir(), name)); }
            catch (Exception ex) { Msg("Impossibile rimuovere: " + ex.Message); }
            RefreshMods();
        }

        // ================================================================== 5. BACKUP

        static DateTime TimeOf(string hhmm)
        {
            TimeSpan ts;
            if (!TimeSpan.TryParse(hhmm, out ts)) ts = new TimeSpan(4, 0, 0);
            return DateTime.Today + ts;
        }

        static DateTimePicker TimePicker(DateTime value)
        {
            var d = new DateTimePicker();
            d.Format = DateTimePickerFormat.Custom;
            d.CustomFormat = "HH:mm";
            d.ShowUpDown = true;
            d.Value = value;
            return d;
        }

        static string FmtSize(long bytes)
        {
            if (bytes >= 1073741824) return (bytes / 1073741824.0).ToString("0.0") + " GB";
            if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.0") + " MB";
            return Math.Max(1, bytes / 1024) + " KB";
        }

        void RefreshBackups()
        {
            lbBackups.Items.Clear();
            foreach (var f in Backup.List(cfg))
            {
                lbBackups.Items.Add(Path.GetFileName(f) + "   (" + FmtSize(new FileInfo(f).Length) + ")");
            }
        }

        void RunBackup(bool auto)
        {
            RunBackup(cur, auto);
        }

        void RunBackup(ServerSlot sl, bool auto)
        {
            if (sl.BackupRunning) { if (!auto) Msg("C'è già un backup in corso."); return; }
            if (!sl.Cfg.Installed) { if (!auto) Msg("Prima installa il server."); return; }
            sl.BackupRunning = true;
            sl.Add("[Backup] Avvio backup...");
            Task.Run(() =>
            {
                try
                {
                    string path = Backup.Run(sl.Cfg, sl.Proc, s => sl.Add("[Backup] " + s));
                    sl.Add("[Backup] Completato: " + Path.GetFileName(path) + " (" + FmtSize(new FileInfo(path).Length) + ")");
                    if (!auto) UI(() => Msg("Backup completato: " + Path.GetFileName(path)));
                }
                catch (Exception ex)
                {
                    sl.Add("[Backup] Errore: " + ex.Message);
                    if (!auto) UI(() => Msg("Backup non riuscito: " + ex.Message));
                }
                finally
                {
                    sl.BackupRunning = false;
                    UI(RefreshBackups);
                }
            });
        }

        void RestoreBackup()
        {
            var item = lbBackups.SelectedItem as string;
            if (item == null) { Msg("Seleziona un backup dall'elenco."); return; }
            if (server.IsRunning) { Msg("Ferma il server prima di ripristinare un backup."); return; }
            string name = item.Split(new[] { "   (" }, StringSplitOptions.None)[0];
            string path = Path.Combine(cfg.BackupDir, name);
            if (!Ask("Ripristino " + name + "?\n\nIl mondo attuale NON viene cancellato: viene rinominato in '..._prima_del_ripristino_...'.")) return;
            try
            {
                Backup.Restore(cfg, path);
                Msg("Ripristino completato. Ora puoi avviare il server.");
            }
            catch (Exception ex) { Msg("Ripristino non riuscito: " + ex.Message); }
        }

        // ================================================================== 6. SISTEMA

        int ServerPort()
        {
            return ServerTools.JavaPort(cfg);
        }

        void RefreshIps()
        {
            var ips = SystemTweaks.LocalIPs();
            int port = ServerPort();
            var sb = new StringBuilder();
            sb.AppendLine("Dallo stesso PC:  localhost:" + port);
            if (ips.Count == 0) sb.AppendLine("Nessuna rete trovata.");
            foreach (var ip in ips.Take(3))
                sb.AppendLine("In casa (stessa rete Wi-Fi/cavo):  " + ip.Split(' ')[0] + ":" + port + "      " + ip.Substring(ip.IndexOf(' ')).Trim());
            lblIps.Text = sb.ToString();
        }

        void ApplyTweaks()
        {
            if (!chkPower.Checked && !chkFirewall.Checked) return;
            bool power = chkPower.Checked, fw = chkFirewall.Checked;
            var jports = slots.Where(x => x.Cfg.Installed).Select(x => ServerTools.JavaPort(x.Cfg)).ToList();
            var bports = slots.Where(x => x.Cfg.Installed).Select(x => ServerTools.BedrockPort(x.Cfg)).ToList();
            if (jports.Count == 0) { jports.Add(25565); bports.Add(19132); }
            Task.Run(() =>
            {
                string err = SystemTweaks.ApplyElevated(power, fw, jports, bports);
                if (power) { powerNow = null; ApplyPowerPlan(); }
                UI(() => Msg(err == null ? "Ottimizzazioni applicate." : "Non applicate: " + err));
            });
        }

        void ApplyAutostart()
        {
            bool on = chkAutostartWin.Checked, admin = chkAutostartAdmin.Checked;
            Task.Run(() =>
            {
                string err = SystemTweaks.SetAutostart(on, admin);
                if (err != null) UI(() => Msg("Avvio automatico non impostato: " + err));
            });
        }

        void AutoLoginHelp()
        {
            Msg("Si aprirà la finestra 'Account utente'.\n\n" +
                "1. Togli la spunta da 'Per usare questo computer è necessario che l'utente immetta il nome e la password'.\n" +
                "2. Premi OK e inserisci la password del tuo account.\n\n" +
                "Se quella spunta non c'è: Impostazioni > Account > Opzioni di accesso > disattiva " +
                "'Per una maggiore sicurezza, consenti solo l'accesso a Windows Hello...', poi riprova.");
            try { Process.Start("netplwiz"); }
            catch (Exception ex) { Msg("Impossibile aprire netplwiz: " + ex.Message); }
        }

        // ================================================================== ANTEPRIMA (screenshot automatici)

        static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(30);
            }
        }

        void Capture(string path)
        {
            using (var bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        /// <summary>Usato da GitHub Actions: apre ogni scheda con il server acceso e salva uno screenshot.</summary>
        public void ScreenshotTour(string outDir)
        {
            Directory.CreateDirectory(outDir);
            Pump(4000);
            if (cfg.Installed)
            {
                StartServer();
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 180 && !txtLog.Text.Contains("Done ("))
                    Pump(500);
                Pump(3000);
                QuickCmd("list");
                RunBackup(true);
                Pump(6000);
            }
            string[] names = { "0-server", "1-installa", "2-console", "3-impostazioni", "4-mod-plugin", "5-backup-riavvii-pausa", "6-giocatori", "7-personalizza", "8-pc" };
            for (int i = 0; i < tabs.TabCount && i < names.Length; i++)
            {
                tabs.SelectedIndex = i;
                if (i == 4 && ModsUsable())
                {
                    txtSearch.Text = "chunky";
                    DoSearch();
                }
                Pump(3000);
                Capture(Path.Combine(outDir, names[i] + ".png"));
            }
            // due schede anche in modalità notte
            SetTheme("dark");
            foreach (int i in new[] { 0, 2, 7 })
            {
                tabs.SelectedIndex = i;
                Pump(2000);
                Capture(Path.Combine(outDir, names[i] + "-notte.png"));
            }
            SetTheme("auto");
            foreach (var sl in slots) if (sl.Proc.IsRunning) sl.Proc.Stop();
            Pump(1000);
            reallyExit = true;
            Close();
        }

        // ================================================================== AGGIORNAMENTI

        void OnUpdateClick()
        {
            if (pendingUpdate != null) AskAndUpdate(pendingUpdate);
            else CheckUpdates(false);
        }

        void CheckUpdates(bool silent)
        {
            if (updateBusy) return;
            updateBusy = true;
            lblUpdate.Text = "Controllo in corso...";
            Task.Run(() =>
            {
                UpdateInfo u = null;
                string err = null;
                try { u = Updater.CheckLatest(); }
                catch (System.Net.WebException ex)
                {
                    var r = ex.Response as System.Net.HttpWebResponse;
                    err = r != null && r.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? "Non è ancora stata pubblicata nessuna versione."
                        : "Impossibile controllare: " + ex.Message;
                }
                catch (Exception ex) { err = "Impossibile controllare: " + ex.Message; }

                UI(() =>
                {
                    updateBusy = false;
                    if (err != null)
                    {
                        lblUpdate.Text = err;
                        if (!silent) Msg(err);
                        return;
                    }
                    if (!Updater.IsNewer(u.Version))
                    {
                        lblUpdate.Text = "Hai già l'ultima versione (" + AppInfo.Version + ").";
                        if (!silent) Msg("Hai già l'ultima versione.");
                        return;
                    }
                    pendingUpdate = u;
                    btnUpdate.Text = "Aggiorna alla " + u.Version;
                    btnUpdate.Font = new Font(Font, FontStyle.Bold);
                    lblUpdate.Text = "È disponibile la versione " + u.Version + ".";
                    if (silent)
                        tray.ShowBalloonTip(5000, "MineServer Manager", "È disponibile la versione " + u.Version +
                                                  ". Aggiorna dalla scheda 8. PC e programma.", ToolTipIcon.Info);
                    else AskAndUpdate(u);
                });
            });
        }

        void AskAndUpdate(UpdateInfo u)
        {
            if (updateBusy) return;
            if (u.ExeUrl == null && u.SetupUrl == null) { Msg("Questa versione non contiene ancora i file da scaricare. Riprova tra qualche minuto."); return; }
            string notes = (u.Notes ?? "").Trim();
            if (notes.Length > 900) notes = notes.Substring(0, 900) + "...";
            string q = "È disponibile la versione " + u.Version + " (tu hai la " + AppInfo.Version + ").\n\n" +
                       (notes.Length > 0 ? "Novità:\n" + notes + "\n\n" : "") + "Aggiorno adesso?";
            if (slots.Any(x => x.IsActive)) q += "\n\nI server accesi verranno fermati (salvando il mondo) e riaccesi da soli dopo l'aggiornamento.";
            if (Ask(q)) DoUpdate(u);
        }

        void DoUpdate(UpdateInfo u)
        {
            updateBusy = true;
            btnUpdate.Enabled = false;
            var active = slots.Where(x => x.IsActive).ToList();
            bool wasRunning = active.Count > 0;
            string resumeIds = string.Join(",", active.Select(x => x.Cfg.Id.ToString()));
            Action<int> prog = pc => UI(() => lblUpdate.Text = pc < 0 ? "Scarico..." : "Scarico... " + pc + "%");
            Task.Run(() =>
            {
                string err = null, setupPath = null;
                bool viaSetup = false;
                try
                {
                    string newExe = u.ExeUrl != null ? Updater.DownloadExe(u, prog) : null;
                    if (wasRunning)
                    {
                        foreach (var a in active)
                        {
                            a.Add("[Aggiornamento] Fermo il server prima di aggiornare...");
                            a.Sleeping = false;
                            a.Wake.Stop();
                            a.Proc.Stop();
                        }
                    }
                    if (newExe != null)
                    {
                        try { Updater.Swap(newExe); }
                        catch (UnauthorizedAccessException) { viaSetup = true; }
                        catch (IOException) { viaSetup = true; }
                    }
                    else viaSetup = true;

                    if (viaSetup)
                    {
                        if (u.SetupUrl == null) throw new Exception("La cartella del programma è protetta e la release non contiene l'installer.");
                        setupPath = Updater.DownloadSetup(u, prog);
                    }
                    Updater.SetStartServerFlag(resumeIds);
                }
                catch (Exception ex) { err = ex.Message; }

                UI(() =>
                {
                    if (err == null)
                    {
                        try
                        {
                            if (viaSetup) Updater.RunSetup(setupPath);
                            else Updater.StartNew();
                        }
                        catch (Exception ex) { err = ex.Message; }
                    }
                    if (err != null)
                    {
                        updateBusy = false;
                        btnUpdate.Enabled = true;
                        Updater.SetStartServerFlag(null);
                        lblUpdate.Text = "Aggiornamento non riuscito.";
                        Msg("Aggiornamento non riuscito:\n" + err);
                        foreach (var a in active) if (!a.Proc.IsRunning) StartSlot(a, true);
                        return;
                    }
                    lblUpdate.Text = "Riavvio con la nuova versione...";
                    reallyExit = true;
                    Close();
                });
            });
        }

        // ================================================================== 8. AMICI (playit.gg + whitelist)

        readonly PlayitAgent playit = new PlayitAgent();
        volatile bool playitBusy, playitCancel;
        TextBox txtAddress, txtFriend;
        Label lblPlayit, lblBedrockAddr, lblWl;
        Button btnPlayit, btnPlayitStop;

        bool QuickCmdOk(string c)
        {
            if (!server.IsRunning) { Msg("Accendi prima il server (scheda 2. Console)."); return false; }
            QuickCmd(c);
            return true;
        }

        void Whitelist(string action)
        {
            string name = txtFriend.Text.Trim();
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$")) { Msg("Scrivi il nome Minecraft esatto (3-16 caratteri: lettere, numeri, _)."); return; }
            if (QuickCmdOk("whitelist " + action + " " + name))
            {
                lblWl.Text = action == "add" ? name + " aggiunto: ora può entrare." : name + " tolto dalla whitelist.";
                txtFriend.Clear();
            }
        }

        void RefreshFriends()
        {
            if (lblPlayit == null) return;
            string addr = PlayitAgent.SavedAddressFor(ServerPort());
            txtAddress.Text = addr ?? "";
            if (playitBusy) return;
            if (playit.IsRunning && addr != null) { lblPlayit.Text = "● Attivo: gli amici possono collegarsi."; lblPlayit.ForeColor = Color.ForestGreen; }
            else if (PlayitAgent.IsSetUp) { lblPlayit.Text = "● Spento: premi 'Attiva accesso da internet'."; lblPlayit.ForeColor = Color.Firebrick; }
            else { lblPlayit.Text = "Non ancora configurato."; lblPlayit.ForeColor = SystemColors.ControlText; }
            btnPlayit.Text = PlayitAgent.IsSetUp ? "Attiva accesso da internet" : "Configura e attiva (1 clic nel browser)";
            btnPlayitStop.Enabled = playit.IsRunning;
            string wl = cfg.Installed ? Props.Get(cfg.ServerDir, "white-list") : null;
            if (lblWl.Text == "" || lblWl.Text.StartsWith("Whitelist:"))
                lblWl.Text = wl == "true" ? "Whitelist: ATTIVA (entrano solo i nomi aggiunti)" :
                             wl == "false" ? "Whitelist: disattivata (entra chiunque abbia l'indirizzo)" : "Whitelist: -";
        }

        void PlayitSetup()
        {
            if (playitBusy)
            {
                if (Ask("Sto aspettando l'approvazione nel browser. Vuoi annullare?")) playitCancel = true;
                return;
            }
            if (!cfg.Installed) { Msg("Prima installa il server (scheda 1)."); return; }
            playitBusy = true;
            playitCancel = false;
            btnPlayit.Text = "In corso... (clic per annullare)";
            lblPlayit.ForeColor = Color.DarkOrange;
            int port = ServerPort();
            bool bedrock = cfg.Geyser;
            int bport = ServerTools.BedrockPort(cfg);
            Action<string> status = s => UI(() => { if (lblPlayit != null) lblPlayit.Text = s; });
            var oldLog = playit.Log;
            playit.Log = s => { oldLog(s); status(s); };

            Task.Run(() =>
            {
                string err = null, addr = null, bed = null;
                try
                {
                    playit.EnsureBinary(pc => status(pc < 0 ? "Scarico playit..." : "Scarico playit... " + pc + "%"));
                    if (Secret_Missing()) playit.Claim(() => playitCancel);
                    status("Preparo il tunnel Minecraft...");
                    addr = playit.EnsureTunnels(port, bedrock, bport, out bed);
                    playit.Start();
                }
                catch (Exception ex) { err = ex.Message; }
                playit.Log = oldLog;

                UI(() =>
                {
                    playitBusy = false;
                    if (err != null)
                    {
                        lblPlayit.Text = "Non riuscito: " + err;
                        lblPlayit.ForeColor = Color.Firebrick;
                        btnPlayit.Text = PlayitAgent.IsSetUp ? "Attiva accesso da internet" : "Configura e attiva (1 clic nel browser)";
                        if (!playitCancel) Msg("playit.gg non è stato attivato:\n\n" + err);
                        return;
                    }
                    gcfg.PlayitEnabled = true;
                    gcfg.Save();
                    lblBedrockAddr.Text = bed != null ? "Amici da telefono/console (Bedrock):  " + bed : "";
                    RefreshFriends();
                    Msg("Fatto! Manda questo indirizzo ai tuoi amici:\n\n" + addr +
                        "\n\nIn Minecraft: Multigiocatore > Aggiungi server > incollano l'indirizzo.\n" +
                        "Da ora playit si accende da solo insieme al programma.");
                });
            });
        }

        static bool Secret_Missing()
        {
            return !File.Exists(Path.Combine(PlayitAgent.Dir, "secret.txt"));
        }

        void PlayitDisable()
        {
            playit.Stop();
            gcfg.PlayitEnabled = false;
            gcfg.Save();
            RefreshFriends();
        }

        void PlayitReset()
        {
            if (!Ask("Scollego questo PC dal tuo account playit.gg?\nPer riattivarlo servirà di nuovo il clic nel browser.\n" +
                     "(Il tunnel resta sul tuo account: se vuoi lo cancelli dal sito playit.gg.)")) return;
            playit.Reset();
            gcfg.PlayitEnabled = false;
            gcfg.Save();
            lblBedrockAddr.Text = "";
            RefreshFriends();
        }

        // ================================================================== 7. PRESTAZIONI

        static readonly bool IsAdminNow = PriorityManager.IsAdmin();

        void OnPrioChanged()
        {
            if (suppress) return;
            string key = PriorityManager.ServerKeys[Math.Max(0, cbPrio.SelectedIndex)];
            if (key == "RealTime")
            {
                bool ok = Ask("Attenzione: con 'Tempo reale' il server passa davanti anche a mouse, tastiera, rete e desktop remoto.\n" +
                              "Se il server va sotto sforzo (es. pre-generazione del mondo) il PC può sembrare bloccato.\n\n" +
                              "Usalo solo se il mini PC fa SOLO da server. Continuare?");
                if (!ok)
                {
                    suppress = true;
                    int i = Array.IndexOf(PriorityManager.ServerKeys, gcfg.ServerPriority);
                    cbPrio.SelectedIndex = i < 0 ? 1 : i;
                    suppress = false;
                    return;
                }
                if (!IsAdminNow) Msg("Senza permessi di amministratore Windows trasforma 'Tempo reale' in 'Alta'.\nPremi 'Riavvia come amministratore' in questa scheda.");
            }
            gcfg.ServerPriority = key;
            gcfg.Save();
            Task.Run(() => ReapplyPriorityAll());
        }

        // ------------------------------------------------------------ pausa automatica

        /// <summary>Ogni minuto: chiede al server quanti giocatori ci sono; se è vuoto da troppo, lo mette in pausa.</summary>
        /// <summary>
        /// Ogni minuto, per ogni server acceso: legge quanti giocatori ci sono (per la scheda 0)
        /// e, se la pausa automatica è attiva e il server è vuoto da troppo, lo mette in pausa.
        /// </summary>
        void CheckIdle(ServerSlot s)
        {
            if (s.Sleeping || s.IdleQueryBusy || !s.Proc.IsRunning || s.Proc.StopRequested) return;
            if ((DateTime.Now - s.Proc.StartedAt).TotalMinutes < 1) return;            // sta ancora partendo
            int port = ServerTools.JavaPort(s.Cfg);
            s.IdleQueryBusy = true;
            Task.Run(() =>
            {
                int n = McProto.QueryPlayers(port, 5000);
                UI(() =>
                {
                    s.IdleQueryBusy = false;
                    if (n < 0 || !s.Proc.IsRunning || s.Sleeping) return;          // non risponde: non decido nulla
                    if (n != s.LastPlayers) { s.LastPlayers = n; RefreshServerList(); }
                    if (!s.Cfg.IdleSleep || busy || s.BackupRunning || updateBusy) { s.EmptySince = DateTime.MinValue; return; }
                    if ((DateTime.Now - s.Proc.StartedAt).TotalMinutes < 3) return;
                    if ((DateTime.Now - s.LastChunky).TotalMinutes < 5) { s.EmptySince = DateTime.MinValue; return; }   // pre-generazione
                    if (n > 0) { s.EmptySince = DateTime.MinValue; return; }
                    if (s.EmptySince == DateTime.MinValue) { s.EmptySince = DateTime.Now; return; }
                    if ((DateTime.Now - s.EmptySince).TotalMinutes >= s.Cfg.IdleMinutes) GoToSleep(s);
                });
            });
        }

        void GoToSleep(ServerSlot s)
        {
            s.Sleeping = true;
            s.EmptySince = DateTime.MinValue;
            s.Add("[Pausa] Nessun giocatore da " + s.Cfg.IdleMinutes + " minuti: spengo il server (salvando il mondo). Si riaccenderà appena qualcuno prova a entrare.");
            if (s == cur)
            {
                lblState.Text = "● Pausa in corso...";
                lblState.ForeColor = Color.DarkOrange;
            }
            Task.Run(() =>
            {
                s.Proc.Stop();
                UI(() => StartWakeListener(s));
            });
        }

        void StartWakeListener(ServerSlot s)
        {
            if (!s.Sleeping || s.Proc.IsRunning) return;
            int port = ServerTools.JavaPort(s.Cfg);
            try
            {
                ApplyWakeTexts(s);
                s.Wake.Start(port, s.Cfg.Geyser ? ServerTools.BedrockPort(s.Cfg) : 0);
                s.Add("[Pausa] Server in pausa: in attesa di giocatori sulla porta " + port + ".");
                Task.Run(() => ApplyPowerPlan());   // in pausa: Risparmio di energia
            }
            catch (Exception ex)
            {
                s.Add("[Pausa] " + ex.Message + " Riaccendo il server.");
                s.Sleeping = false;
                StartSlot(s, true);
            }
            if (s == cur) UpdateState();
            RefreshServerList();
        }

        void WakeUp(ServerSlot s, string who)
        {
            if (!s.Sleeping) return;
            s.Add("[Pausa] " + who + " vuole entrare: riaccendo il server.");
            if (tray != null) tray.ShowBalloonTip(4000, "MineServer Manager", who + " vuole entrare in '" + s.Cfg.DisplayName + "': lo riaccendo.", ToolTipIcon.Info);
            s.Wake.Stop();
            Task.Run(() =>
            {
                System.Threading.Thread.Sleep(1500);   // lascio a Windows il tempo di liberare la porta
                UI(() => { if (s.Sleeping) StartSlot(s, true); });
            });
        }

        /// <summary>Con la whitelist attiva, solo chi è nella whitelist può svegliare il server.</summary>
        static bool CanWake(AppConfig c, string name)
        {
            try
            {
                if (!"true".Equals(Props.Get(c.ServerDir, "white-list"), StringComparison.OrdinalIgnoreCase)) return true;
                if (string.IsNullOrEmpty(name)) return false;
                string f = Path.Combine(c.ServerDir, "whitelist.json");
                if (!File.Exists(f)) return false;
                var arr = J.A(new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(f)));
                if (arr == null) return false;
                foreach (var o in arr)
                    if (name.Equals(J.S(J.P(o, "name")), StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return true; }
        }

        // ------------------------------------------------------------ piani energetici

        void TogglePowerAuto()
        {
            gcfg.AutoPowerPlan = chkPowerAuto.Checked;
            gcfg.Save();
            Task.Run(() =>
            {
                if (gcfg.AutoPowerPlan)
                {
                    RememberOriginalPlan();
                    powerNow = null;
                    ApplyPowerPlan();
                }
                else RestoreOriginalPlan();
            });
        }

        void RememberOriginalPlan()
        {
            if (!string.IsNullOrEmpty(gcfg.PowerOriginalGuid)) return;
            string a = PowerPlan.Active();
            if (a == null || a == gcfg.PowerPerfGuid || a == gcfg.PowerSaverGuid) return;
            gcfg.PowerOriginalGuid = a;
            gcfg.Save();
        }

        void RestoreOriginalPlan()
        {
            lock (powerLock)
            {
                string g = gcfg.PowerOriginalGuid;
                if (string.IsNullOrEmpty(g)) return;
                if (PowerPlan.Exists(g) && PowerPlan.SetActive(g))
                    Log("[Energia] Rimesso il piano energetico di prima: " + PowerPlan.NameOf(g) + ".");
                gcfg.PowerOriginalGuid = "";
                gcfg.Save();
                powerNow = null;
            }
        }

        /// <summary>
        /// Ogni 2 minuti: se qualcun altro (Windows, ReviOS, un altro programma) ha cambiato il piano energetico,
        /// rimetto quello giusto.
        /// </summary>
        void CheckPowerPlan()
        {
            if (!gcfg.AutoPowerPlan || powerNow == null) return;
            Task.Run(() =>
            {
                try
                {
                    string a = PowerPlan.Active();
                    if (a == null || a == powerNow) return;
                    Log("[Energia] Il piano energetico era stato cambiato da fuori: lo rimetto.");
                    powerNow = null;
                    ApplyPowerPlan();
                }
                catch { }
            });
        }

        /// <summary>Server acceso: Prestazioni eccellenti. Spento o in pausa: Risparmio di energia.</summary>
        void ApplyPowerPlan()
        {
            if (!gcfg.AutoPowerPlan) return;
            lock (powerLock)
            {
                try
                {
                    bool running = slots.Any(x => x.Proc.IsRunning);
                    bool paused = !running && slots.Any(x => x.Sleeping);
                    // in pausa: Risparmio di energia (salvo se l'utente ha tolto la spunta)
                    bool on = running || (paused && !gcfg.SaverWhenPaused);
                    string why = running ? " (server acceso)" : paused ? " (server in pausa)" : " (server spento)";
                    Action<string> log = s => Log("[Energia] " + s);
                    string g = on ? PowerPlan.EnsurePerformance(gcfg, log) : PowerPlan.EnsureSaver(gcfg, log);
                    if (g == null) { log("Nessun piano energetico adatto trovato."); return; }
                    if (g == powerNow) return;
                    if (PowerPlan.SetActive(g))
                    {
                        powerNow = g;
                        log("Piano energetico: " + PowerPlan.NameOf(g) + why);
                    }
                    else log("Windows non ha permesso di cambiare il piano energetico.");
                }
                catch (Exception ex) { Log("[Energia] " + ex.Message); }
            }
        }

        // ================================================================== 0. I MIEI SERVER (più server)

        Panel serverBar;
        Button btnTheme;
        ComboBox cbTheme;
        readonly ToolTip themeTip = new ToolTip();
        bool themeLoading;
        ComboBox cbServer;
        ListView lvServers;
        ProgressBar pbRam;
        Label lblRam, lblAutoHint;
        CheckBox chkStagger;
        bool syncingCombo, refreshingList;

        ServerSlot AddSlot(AppConfig c)
        {
            var sl = new ServerSlot(c);
            sl.Proc.Output += line =>
            {
                sl.Add(line);
                if (line.IndexOf("[Chunky]", StringComparison.OrdinalIgnoreCase) >= 0) sl.LastChunky = DateTime.Now;
                if (line.IndexOf(" the game", StringComparison.Ordinal) > 0)
                {
                    string clean = Ansi.Replace(line, "");
                    var m = JoinRx.Match(clean);
                    if (m.Success) OnPlayerEvent(sl, m.Groups[1].Value, true);
                    else if ((m = LeftRx.Match(clean)).Success) OnPlayerEvent(sl, m.Groups[1].Value, false);
                }
            };
            sl.Proc.StateChanged += () => UI(() =>
            {
                if (sl == cur) UpdateState();
                RefreshServerList();
                // chiuso da solo (non fermato da noi) nei primi 10 minuti: forse una mod solo client
                if (!sl.Proc.IsRunning && !sl.Proc.StopRequested && !sl.Sleeping && (DateTime.Now - sl.Proc.StartedAt).TotalMinutes < 10)
                    CheckCrashForClientMods(sl);
            });
            sl.Proc.StateChanged += () => Task.Run(() => ApplyPowerPlan());
            sl.Proc.Started += pid => OnServerStarted(sl, pid);
            sl.Wake.Log = m => sl.Add("[Pausa] " + m);
            sl.Wake.CanWake = n => CanWake(sl.Cfg, n);
            sl.Wake.WakeRequested = who => UI(() => WakeUp(sl, who));
            ApplyWakeTexts(sl);
            slots.Add(sl);
            return sl;
        }

        void BuildServerBar()
        {
            serverBar = new Panel();
            serverBar.Dock = DockStyle.Top;
            serverBar.Height = 42;
            serverBar.BackColor = Color.FromArgb(233, 238, 244);
            Controls.Add(serverBar);

            var l = Lbl(serverBar, "Server:", 10, 10);
            l.Font = new Font(Font, FontStyle.Bold);
            cbServer = new ComboBox();
            cbServer.DropDownStyle = ComboBoxStyle.DropDownList;
            cbServer.DrawMode = DrawMode.OwnerDrawFixed;
            cbServer.ItemHeight = 20;
            cbServer.Font = new Font("Segoe UI", 10F);
            Put(serverBar, cbServer, 68, 8, 290, 0);
            cbServer.DrawItem += DrawServerItem;
            cbServer.SelectedIndexChanged += (s, e) =>
            {
                if (syncingCombo) return;
                SwitchTo(cbServer.SelectedItem as ServerSlot);
            };
            Btn(serverBar, "+ Nuovo server", 368, 7, 120, (s, e) => NewServer());
            Btn(serverBar, "Aggiungi server esistente...", 494, 7, 178, (s, e) => AddExistingServer());
            Btn(serverBar, "Rinomina", 678, 7, 80, (s, e) => RenameServer());
            Btn(serverBar, "Elimina", 764, 7, 72, (s, e) => DeleteServer());
            btnTheme = Btn(serverBar, "Notte", 842, 7, 86, (s, e) => SetTheme(Theme.Dark ? "light" : "dark"));
            themeTip.SetToolTip(btnTheme, "Modalità notte (si sceglie anche \"Automatica\" nella scheda 8. PC e programma)");
            RebuildCombo();
        }

        static Color StateColor(ServerSlot s)
        {
            if (s.Proc.IsRunning) return Color.FromArgb(46, 158, 68);
            if (s.Sleeping) return Color.SteelBlue;
            return s.Cfg.Installed ? Color.FromArgb(179, 58, 58) : Color.Gray;
        }

        void DrawServerItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= cbServer.Items.Count) return;
            var sl = cbServer.Items[e.Index] as ServerSlot;
            if (sl == null) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(StateColor(sl)))
                g.FillEllipse(b, e.Bounds.X + 4, e.Bounds.Y + (e.Bounds.Height - 10) / 2, 10, 10);
            bool sel = (e.State & DrawItemState.Selected) != 0;
            string txt = sl.Cfg.DisplayName + "   (" + sl.StateText().ToLowerInvariant() + ")";
            TextRenderer.DrawText(g, txt, e.Font, new Rectangle(e.Bounds.X + 20, e.Bounds.Y, e.Bounds.Width - 20, e.Bounds.Height),
                e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        void RebuildCombo()
        {
            if (cbServer == null) return;
            syncingCombo = true;
            try
            {
                cbServer.Items.Clear();
                foreach (var sl in slots) cbServer.Items.Add(sl);
                cbServer.SelectedItem = cur;
            }
            finally { syncingCombo = false; }
        }

        void SyncCombo()
        {
            if (cbServer == null) return;
            syncingCombo = true;
            try { cbServer.SelectedItem = cur; }
            finally { syncingCombo = false; }
            cbServer.Invalidate();
        }

        void UpdateServerBarState()
        {
            if (cbServer != null) cbServer.Invalidate();
        }

        void BuildServersTab()
        {
            var p = NewTab("0. I miei server");

            var g1 = new GroupBox();
            g1.Text = "Tutti i tuoi server  (spunta = si accende da solo all'accensione del PC)";
            Put(p, g1, 12, 10, 800, 290);
            lvServers = new ListView();
            lvServers.View = View.Details;
            lvServers.CheckBoxes = true;
            lvServers.FullRowSelect = true;
            lvServers.HideSelection = false;
            lvServers.MultiSelect = false;
            lvServers.Columns.Add("Nome", 200);
            lvServers.Columns.Add("Tipo", 120);
            lvServers.Columns.Add("Stato", 150);
            lvServers.Columns.Add("Porta", 60);
            lvServers.Columns.Add("RAM", 55);
            lvServers.Columns.Add("Indirizzo per gli amici", 190);
            Put(g1, lvServers, 15, 24, 770, 210);
            lvServers.ItemChecked += (s, e) =>
            {
                if (refreshingList) return;
                var sl = e.Item.Tag as ServerSlot;
                if (sl == null) return;
                sl.Cfg.AutoStart = e.Item.Checked;
                sl.Cfg.Save();
                UpdateAutostartHint();
            };
            lvServers.DoubleClick += (s, e) =>
            {
                var sl = SelectedListSlot();
                if (sl == null) return;
                SwitchTo(sl);
                tabs.SelectedIndex = sl.Cfg.Installed ? 2 : 1;
            };
            var bStart = Btn(g1, "Avvia", 15, 244, 90, (s, e) => { var sl = SelectedListSlot(); if (sl != null) StartSlot(sl, false); });
            var bStop = Btn(g1, "Ferma", 111, 244, 90, (s, e) => { var sl = SelectedListSlot(); if (sl != null) StopSlot(sl); });
            var bOpen = Btn(g1, "Apri nelle altre schede", 207, 244, 170, (s, e) =>
            {
                var sl = SelectedListSlot();
                if (sl == null) return;
                SwitchTo(sl);
                tabs.SelectedIndex = sl.Cfg.Installed ? 2 : 1;
            });
            var hint = Lbl(g1, "Doppio clic su una riga = la apri. Porte e cartelle le sceglie il programma, diverse per ogni server.", 385, 249);
            hint.ForeColor = Color.DimGray;

            var g2 = new GroupBox();
            g2.Text = "Memoria (RAM) del PC";
            Put(p, g2, 12, 310, 395, 120);
            pbRam = Put(g2, new ProgressBar(), 15, 28, 360, 18);
            lblRam = Lbl(g2, "", 15, 54);
            var ramHint = Lbl(g2, "La RAM di ogni server si cambia nella scheda 3. Impostazioni.\nSe non basta per accenderne un altro, te lo dico prima.", 15, 78);
            ramHint.ForeColor = Color.DimGray;

            var g3 = new GroupBox();
            g3.Text = "Accensione del PC";
            Put(p, g3, 417, 310, 395, 120);
            Lbl(g3, "Si accendono da soli i server con la spunta nell'elenco qui sopra.", 15, 24);
            chkStagger = Chk(g3, "Uno alla volta, a 1 minuto di distanza (il mini PC non si blocca)", 15, 48, gcfg.StaggerStart);
            chkStagger.CheckedChanged += (s, e) => { gcfg.StaggerStart = chkStagger.Checked; gcfg.Save(); };
            lblAutoHint = Lbl(g3, "", 15, 76);
            lblAutoHint.ForeColor = Color.DarkOrange;

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width;
                if (w < 300) return;
                g1.Width = w - 24;
                lvServers.Width = g1.Width - 30;
                // l'ultima colonna riempie tutto lo spazio (niente riquadro vuoto a destra nell'intestazione)
                int rest = lvServers.ClientSize.Width - 200 - 120 - 150 - 60 - 55;
                lvServers.Columns[5].Width = Math.Max(150, rest);
                int half = (w - 34) / 2;
                g2.Width = half;
                g3.Left = 12 + half + 10;
                g3.Width = half;
                pbRam.Width = half - 30;
            });
        }

        ServerSlot SelectedListSlot()
        {
            if (lvServers.SelectedItems.Count == 0)
            {
                Msg("Seleziona prima un server nell'elenco.");
                return null;
            }
            return lvServers.SelectedItems[0].Tag as ServerSlot;
        }

        void UpdateAutostartHint()
        {
            if (lblAutoHint == null) return;
            bool any = slots.Any(x => x.Cfg.AutoStart);
            lblAutoHint.Text = any && !SystemTweaks.IsAutostart()
                ? "Attenzione: attiva anche \"Apri il programma all'accensione del PC\" (scheda 8)."
                : "";
        }

        void RefreshServerList()
        {
            if (lvServers == null || IsDisposed) return;
            if (InvokeRequired) { UI(RefreshServerList); return; }
            refreshingList = true;
            try
            {
                lvServers.BeginUpdate();
                if (lvServers.Items.Count != slots.Count)
                {
                    lvServers.Items.Clear();
                    foreach (var sl in slots) lvServers.Items.Add(new ListViewItem(new[] { "", "", "", "", "", "" }));
                }
                for (int i = 0; i < slots.Count; i++)
                {
                    var sl = slots[i];
                    var it = lvServers.Items[i];
                    it.Tag = sl;
                    int port = ServerTools.JavaPort(sl.Cfg);
                    string addr = sl.Cfg.Installed ? PlayitAgent.SavedAddressFor(port) : null;
                    it.Text = sl.Cfg.DisplayName + (sl == cur ? "   ◄" : "");
                    it.SubItems[1].Text = sl.Cfg.TypeLabel;
                    it.SubItems[2].Text = "● " + sl.StateText();
                    it.SubItems[3].Text = sl.Cfg.Installed ? port.ToString() : "-";
                    it.SubItems[4].Text = sl.Cfg.RamGB + " GB";
                    it.SubItems[5].Text = addr ?? "— (non attivato)";
                    it.UseItemStyleForSubItems = false;
                    it.SubItems[2].ForeColor = Theme.Readable(StateColor(sl));
                    if (it.Checked != sl.Cfg.AutoStart) it.Checked = sl.Cfg.AutoStart;
                }
                lvServers.EndUpdate();

                int total = ServerTools.TotalRamGB();
                int used = slots.Where(x => x.IsActive).Sum(x => x.Cfg.RamGB);
                pbRam.Maximum = Math.Max(1, total);
                pbRam.Value = Math.Max(0, Math.Min(pbRam.Maximum, used));
                lblRam.Text = total > 0
                    ? "Server accesi o in pausa: " + used + " GB su " + total + " GB   ·   restano " + Math.Max(0, total - used) + " GB a Windows"
                    : "Server accesi o in pausa: " + used + " GB";
                lblRam.ForeColor = total > 0 && used > total - 2 ? Color.Firebrick : SystemColors.ControlText;
            }
            finally { refreshingList = false; }
            UpdateServerBarState();
        }

        /// <summary>Passa a un altro server: tutte le schede mostrano i suoi dati.</summary>
        void SwitchTo(ServerSlot s)
        {
            if (s == null || s == cur) { SyncCombo(); return; }
            if (busy) { Msg("Aspetta che finisca l'installazione in corso."); SyncCombo(); return; }
            cur = s;
            gcfg.CurrentId = s.Cfg.Id;
            gcfg.Save();
            LoadCurrentServer();
        }

        void LoadCurrentServer()
        {
            loadingServer = true;
            try
            {
                txtLog.Text = cur.HistoryText();
                cur.DropPending();
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
                chkAutoRestart.Checked = cfg.AutoRestart;
                chkBackup.Checked = cfg.BackupEnabled;
                dtBackup.Value = TimeOf(cfg.BackupTime);
                numKeep.Value = Math.Max(numKeep.Minimum, Math.Min(numKeep.Maximum, cfg.BackupKeep));
                chkRestart.Checked = cfg.DailyRestart;
                dtRestart.Value = TimeOf(cfg.RestartTime);
                chkIdle.Checked = cfg.IdleSleep;
                numIdle.Value = Math.Max(numIdle.Minimum, Math.Min(numIdle.Maximum, cfg.IdleMinutes));
                int ti = Array.IndexOf(Installer.Types, cfg.ServerType);
                cbType.SelectedIndex = ti < 0 ? 1 : ti;
                if (cfg.Installed && cfg.ServerDir != "")
                {
                    txtDir.Text = cfg.ServerDir;
                    lastDefaultDir = "";
                }
                else
                {
                    txtDir.Text = DefaultDir(SelType());
                    lastDefaultDir = txtDir.Text;
                }
                if (chkGeyser.Enabled) chkGeyser.Checked = cfg.Geyser;
                grid.Rows.Clear();
                personalizeLoaded = false;
                RefreshAll();
            }
            finally { loadingServer = false; }
            LoadVersions();
            if (tabs.SelectedIndex == 7) LoadPersonalize();
            lblWl.Text = "";
            RefreshFriends();
            UpdateState();
            UpdateStats();
            RefreshServerList();
            SyncCombo();
            Text = "MineServer Manager " + AppInfo.Version + "  -  " + cfg.DisplayName;
        }

        string Prompt(string title, string text, string def)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.Font = Font;
                f.ClientSize = new Size(420, 140);
                var l = new Label();
                l.Text = text;
                l.SetBounds(12, 12, 396, 44);
                var t = new TextBox();
                t.Text = def ?? "";
                t.SetBounds(12, 62, 396, 24);
                var ok = new Button();
                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(232, 100, 85, 28);
                var no = new Button();
                no.Text = "Annulla";
                no.DialogResult = DialogResult.Cancel;
                no.SetBounds(323, 100, 85, 28);
                f.Controls.AddRange(new Control[] { l, t, ok, no });
                if (Theme.Dark) Theme.Apply(f, true);
                f.AcceptButton = ok;
                f.CancelButton = no;
                return f.ShowDialog(this) == DialogResult.OK ? t.Text.Trim() : null;
            }
        }

        void NewServer()
        {
            if (busy) { Msg("Aspetta che finisca l'installazione in corso."); return; }
            string name = Prompt("Nuovo server", "Come vuoi chiamare il nuovo server?\n(es. \"Creativa\", \"Mondo con le mod\")", "Server " + (slots.Count + 1));
            if (name == null) return;
            if (name == "") name = "Server " + (slots.Count + 1);
            var c = gcfg.AddServer(name);
            if (slots.Any(x => x.Cfg.Installed)) c.RamGB = 4;   // c'è già un altro server: RAM più bassa
            c.Save();
            var sl = AddSlot(c);
            RebuildCombo();
            SwitchTo(sl);
            tabs.SelectedIndex = 1;
            Msg("Creato \"" + name + "\".\n\nOra scegli tipo e versione e premi \"Installa server\".\n" +
                "Cartella e porta vengono scelte da sole, diverse da quelle degli altri server.");
        }

        void AddExistingServer()
        {
            if (busy) { Msg("Aspetta che finisca l'installazione in corso."); return; }
            string dir;
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "Scegli la cartella del server già esistente (quella con server.jar, mods, world...)";
                d.SelectedPath = AppConfig.BaseDir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                dir = d.SelectedPath;
            }
            if (gcfg.Servers.Any(c => c.Installed && string.Equals(ServerTools.Full(c.ServerDir), ServerTools.Full(dir), StringComparison.OrdinalIgnoreCase)))
            {
                Msg("Questo server è già nell'elenco.");
                return;
            }
            var det = ServerTools.Detect(dir);
            if (det.Error != null) { Msg(det.Error); return; }
            string ver = det.Version;
            if (string.IsNullOrEmpty(ver))
            {
                ver = Prompt("Versione di Minecraft", "Ho trovato un server " + det.Type + ", ma non capisco la versione.\nScrivila tu (es. 1.21.10):", "");
                if (string.IsNullOrEmpty(ver)) return;
            }
            string name = Prompt("Aggiungi server esistente", "Ho trovato un server " + det.Type + " " + ver + ".\nCome lo chiamo?", Path.GetFileName(dir));
            if (name == null) return;
            if (name == "") name = det.Type + " " + ver;

            tabs.SelectedIndex = 1;
            SetBusy(true, "Preparo Java...");
            AppendInstallLog("=== Aggiungo il server esistente " + det.Type + " " + ver + " (" + dir + ") ===");
            var inst = new Installer();
            inst.Log = s => UI(() => AppendInstallLog(s));
            inst.Progress = pc => UI(() =>
            {
                if (pc < 0) progress.Style = ProgressBarStyle.Marquee;
                else { progress.Style = ProgressBarStyle.Blocks; progress.Value = Math.Max(0, Math.Min(100, pc)); }
            });
            Task.Run(() =>
            {
                string err = null, java = null;
                try { java = inst.EnsureJava(Installer.JavaMajorFor(ver)); }
                catch (Exception ex) { err = ex.Message; }
                UI(() =>
                {
                    SetBusy(false, err == null ? "Fatto!" : "Errore");
                    if (err != null) { Msg("Non riesco a preparare Java per questo server:\n" + err); return; }
                    var c = gcfg.AddServer(name);
                    c.ServerType = det.Type;
                    c.McVersion = ver;
                    c.ServerDir = dir;
                    c.JavaPath = java;
                    c.LaunchMode = det.Mode;
                    c.LaunchTarget = det.Target;
                    c.Geyser = det.Geyser;
                    c.Installed = true;
                    if (slots.Any(x => x.Cfg.Installed)) c.RamGB = 4;
                    c.Save();
                    var sl = AddSlot(c);
                    string portNote = EnsureUniquePort(sl);
                    RebuildCombo();
                    SwitchTo(sl);
                    AppendInstallLog("=== Server aggiunto ===");
                    Msg("Aggiunto \"" + name + "\" (" + det.Type + " " + ver + ").\n\nIl mondo, le mod e le impostazioni sono quelli che c'erano." +
                        (portNote != null ? "\n\n" + portNote : ""));
                    tabs.SelectedIndex = 0;
                });
            });
        }

        /// <summary>Se la porta del server è già usata da un altro server dell'elenco, ne sceglie una libera. Ritorna una nota o null.</summary>
        string EnsureUniquePort(ServerSlot sl)
        {
            var c = sl.Cfg;
            if (!c.Installed) return null;
            int mine = ServerTools.JavaPort(c);
            bool clash = gcfg.Servers.Any(o => o != c && o.Installed && ServerTools.JavaPort(o) == mine);
            if (!clash) return null;
            int p = ServerTools.NextFreePort(gcfg.Servers, c);
            try
            {
                McText.SetProp(c.ServerDir, "server-port", p.ToString());
                string note = "Porta impostata a " + p + " (la " + mine + " è già usata da un altro server).";
                sl.Add("[Manager] " + note);
                return note;
            }
            catch (Exception ex)
            {
                sl.Add("[Manager] Non riesco a cambiare la porta: " + ex.Message);
                return "Attenzione: questo server usa la stessa porta (" + mine + ") di un altro. Cambiala nella scheda Impostazioni.";
            }
        }

        void RenameServer()
        {
            string name = Prompt("Rinomina server", "Nuovo nome per \"" + cfg.DisplayName + "\":", cfg.DisplayName);
            if (string.IsNullOrEmpty(name)) return;
            cfg.Name = name;
            cfg.Save();
            RefreshServerList();
            SyncCombo();
            Text = "MineServer Manager " + AppInfo.Version + "  -  " + cfg.DisplayName;
        }

        void DeleteServer()
        {
            if (busy) { Msg("Aspetta che finisca l'installazione in corso."); return; }
            var sl = cur;
            if (slots.Count == 1) { Msg("È l'unico server dell'elenco: non si può eliminare."); return; }
            if (sl.IsActive) { Msg("Prima ferma \"" + sl.Cfg.DisplayName + "\" (scheda 2. Console)."); return; }
            string dir = sl.Cfg.ServerDir;
            bool hasFiles = sl.Cfg.Installed && Directory.Exists(dir);
            DialogResult r;
            if (hasFiles)
            {
                r = MessageBox.Show(this,
                    "Eliminare \"" + sl.Cfg.DisplayName + "\"?\n\n" +
                    "Sì = toglilo dall'elenco E CANCELLA la sua cartella (mondo, mod, tutto):\n      " + dir + "\n\n" +
                    "No = toglilo solo dall'elenco: i file restano sul disco e puoi riaggiungerlo con \"Aggiungi server esistente\".\n\n" +
                    "Annulla = non fare niente",
                    "Elimina server", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (r == DialogResult.Cancel) return;
            }
            else
            {
                if (!Ask("Togliere \"" + sl.Cfg.DisplayName + "\" dall'elenco?")) return;
                r = DialogResult.No;
            }
            if (r == DialogResult.Yes)
            {
                string full = ServerTools.Full(dir);
                bool looksLikeServer = File.Exists(Path.Combine(full, "server.properties")) || Directory.Exists(Path.Combine(full, "world"));
                bool safe = full.Length > 3 && full.Split('\\', '/').Length >= 3 &&
                            !string.Equals(full, ServerTools.Full(AppConfig.BaseDir), StringComparison.OrdinalIgnoreCase) &&
                            looksLikeServer;
                if (!safe) { Msg("Per sicurezza non cancello questa cartella:\n" + dir + "\n\nTolgo solo il server dall'elenco."); r = DialogResult.No; }
                else if (Ask("Faccio prima un backup del mondo? (Consigliato: finisce nella cartella \"" + Path.GetFileName(sl.Cfg.BackupDir) + "\".)"))
                {
                    try
                    {
                        string zip = Backup.Run(sl.Cfg, sl.Proc, s => { });
                        Msg("Backup fatto: " + Path.GetFileName(zip));
                    }
                    catch (Exception ex)
                    {
                        if (!Ask("Backup non riuscito (" + ex.Message + ").\nCancello lo stesso?")) return;
                    }
                }
            }
            if (r == DialogResult.Yes)
            {
                try { Directory.Delete(dir, true); }
                catch (Exception ex) { Msg("Non sono riuscito a cancellare tutto (" + ex.Message + ").\nPuoi finire a mano: " + dir); }
            }
            slots.Remove(sl);
            gcfg.RemoveServer(sl.Cfg);
            cur = slots.FirstOrDefault(x => x.Cfg.Id == gcfg.CurrentId) ?? slots[0];
            gcfg.CurrentId = cur.Cfg.Id;
            gcfg.Save();
            RebuildCombo();
            LoadCurrentServer();
            tabs.SelectedIndex = 0;
        }

        // ------------------------------------------------------------ accensione scaglionata

        void QueueStart(List<ServerSlot> list)
        {
            foreach (var s in list)
                if (s != null && s.Cfg.Installed && !startQueue.Contains(s)) startQueue.Add(s);
            nextQueuedStart = DateTime.MinValue;
            ProcessStartQueue();
        }

        void ProcessStartQueue()
        {
            if (startQueue.Count == 0 || DateTime.Now < nextQueuedStart) return;
            var s = startQueue[0];
            startQueue.RemoveAt(0);
            if (!s.Proc.IsRunning) StartSlot(s, true);
            if (startQueue.Count > 0)
            {
                int wait = gcfg.StaggerStart ? 60 : 5;
                nextQueuedStart = DateTime.Now.AddSeconds(wait);
                s.Add("[Manager] Il prossimo server (" + startQueue[0].Cfg.DisplayName + ") parte tra " + wait + " secondi.");
            }
        }

        // ================================================================== MODPACK, MOD SOLO CLIENT, SPARK, OTTIMIZZA

        /// <summary>Finestra con un elenco da spuntare. Restituisce gli indici scelti, null se annullato.</summary>
        List<int> ChooseItems(string title, string text, IList<string> items, bool allChecked, string okText)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.Font = Font;
                f.ClientSize = new Size(760, 480);
                f.MinimumSize = new Size(520, 320);
                var l = new Label();
                l.Text = text;
                l.SetBounds(12, 10, 736, 56);
                l.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                var list = new CheckedListBox();
                list.CheckOnClick = true;
                list.IntegralHeight = false;
                list.HorizontalScrollbar = true;
                list.SetBounds(12, 70, 736, 360);
                list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                foreach (var it in items) list.Items.Add(it, allChecked);
                var all = new Button();
                all.Text = "Tutte / nessuna";
                all.SetBounds(12, 440, 130, 28);
                all.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
                all.Click += (s, e) =>
                {
                    bool any = list.CheckedItems.Count < list.Items.Count;
                    for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, any);
                };
                var ok = new Button();
                ok.Text = okText;
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(468, 440, 180, 28);
                ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                var no = new Button();
                no.Text = "Annulla";
                no.DialogResult = DialogResult.Cancel;
                no.SetBounds(654, 440, 94, 28);
                no.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                f.Controls.AddRange(new Control[] { l, list, all, ok, no });
                f.AcceptButton = ok;
                f.CancelButton = no;
                if (Theme.Dark) Theme.Apply(f, true);
                if (f.ShowDialog(this) != DialogResult.OK) return null;
                var res = new List<int>();
                foreach (int i in list.CheckedIndices) res.Add(i);
                return res;
            }
        }

        /// <summary>Tre (o più) scelte con un pulsante ciascuna. -1 = annullato.</summary>
        int ChooseOne(string title, string text, string[] options)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.Font = Font;
                f.ClientSize = new Size(520, 70 + options.Length * 44 + 40);
                var l = new Label();
                l.Text = text;
                l.SetBounds(12, 12, 496, 50);
                f.Controls.Add(l);
                int chosen = -1;
                for (int i = 0; i < options.Length; i++)
                {
                    int k = i;
                    var b = new Button();
                    b.Text = options[i];
                    b.TextAlign = ContentAlignment.MiddleLeft;
                    b.SetBounds(12, 66 + i * 44, 496, 38);
                    b.Click += (s, e) => { chosen = k; f.DialogResult = DialogResult.OK; };
                    f.Controls.Add(b);
                }
                var no = new Button();
                no.Text = "Annulla";
                no.DialogResult = DialogResult.Cancel;
                no.SetBounds(414, 66 + options.Length * 44 + 4, 94, 28);
                f.Controls.Add(no);
                f.CancelButton = no;
                if (Theme.Dark) Theme.Apply(f, true);
                return f.ShowDialog(this) == DialogResult.OK ? chosen : -1;
            }
        }

        static string Slug(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in (s ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) && ch < 128) sb.Append(ch);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }
            string r = sb.ToString().Trim('-');
            if (r.Length > 30) r = r.Substring(0, 30).Trim('-');
            return r.Length > 0 ? r : "modpack";
        }

        // ------------------------------------------------------------ importa modpack

        void ImportModpack()
        {
            if (busy) { Msg("Aspetta che finisca l'operazione in corso."); return; }
            int src = ChooseOne("Importa modpack", "Da dove arriva il modpack?\nIl programma installa il loader giusto, le mod per il server e le configurazioni.",
                new[]
                {
                    "Cerca su Modrinth  (tutto automatico)",
                    "File .mrpack o .zip  (Modrinth, CurseForge, \"Server Pack\" di CurseForge)",
                    "Cartella del modpack nell'app CurseForge  (usa le mod già scaricate)"
                });
            if (src < 0) return;
            Func<ModpackImporter, string> fetch;
            if (src == 0)
            {
                var v = PickModrinthModpack();
                if (v == null) return;
                fetch = imp => imp.DownloadMrpack(v);
            }
            else if (src == 1)
            {
                string file;
                using (var d = new OpenFileDialog())
                {
                    d.Title = "Scegli il modpack";
                    d.Filter = "Modpack (*.mrpack;*.zip)|*.mrpack;*.zip|Tutti i file|*.*";
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    file = d.FileName;
                }
                fetch = imp => file;
            }
            else
            {
                string folder;
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "Scegli la cartella del modpack nell'app CurseForge\n(di solito: Documenti\\curseforge\\minecraft\\Instances\\<nome del modpack>)";
                    string guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "curseforge", "minecraft", "Instances");
                    if (Directory.Exists(guess)) d.SelectedPath = guess;
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    folder = d.SelectedPath;
                }
                fetch = imp => folder;
            }

            tabs.SelectedIndex = 1;
            SetBusy(true, "Preparo il modpack...");
            var importer = NewImporter();
            Task.Run(() =>
            {
                string path = null, err = null;
                ModpackPlan plan = null;
                try
                {
                    path = fetch(importer);
                    plan = ModpackImporter.Inspect(path);
                }
                catch (Exception ex) { err = ex.Message; }
                UI(() =>
                {
                    SetBusy(false, "");
                    if (err != null) { Msg("Non riesco a leggere il modpack:\n" + err); return; }
                    if (plan.Error != null) { Msg(plan.Error); return; }
                    if (string.IsNullOrEmpty(plan.Mc))
                    {
                        plan.Mc = Prompt("Versione di Minecraft", "Non trovo la versione di Minecraft del modpack (" + plan.Type + ").\nScrivila tu (es. 1.20.1):", "");
                        if (string.IsNullOrEmpty(plan.Mc)) return;
                    }
                    string what = plan.Name + "\n" + plan.KindLabel + "  —  " + plan.Type + " " + (plan.LoaderVersion ?? "(versione consigliata)") +
                                  "  per Minecraft " + plan.Mc;
                    bool newServer = cfg.Installed;
                    if (!Ask("Ho trovato:\n\n" + what + "\n\n" + (newServer
                            ? "Lo installo come NUOVO server (quello attuale non viene toccato)?"
                            : "Lo installo in \"" + cfg.DisplayName + "\"?"))) return;
                    StartModpackInstall(importer, path, plan, newServer);
                });
            });
        }

        ModpackImporter NewImporter()
        {
            var imp = new ModpackImporter();
            imp.Log = s => UI(() => AppendInstallLog(s));
            imp.Progress = pc => UI(() =>
            {
                if (pc < 0) progress.Style = ProgressBarStyle.Marquee;
                else { progress.Style = ProgressBarStyle.Blocks; progress.Value = Math.Max(0, Math.Min(100, pc)); }
            });
            return imp;
        }

        void StartModpackInstall(ModpackImporter importer, string path, ModpackPlan plan, bool newServer)
        {
            ServerSlot target = cur;
            if (newServer)
            {
                var c = gcfg.AddServer(plan.Name);
                c.Save();
                target = AddSlot(c);
                RebuildCombo();
                SwitchTo(target);
            }
            else if (string.IsNullOrEmpty(cfg.Name)) { cfg.Name = plan.Name; cfg.Save(); }
            string dir = ServerTools.DefaultDir(Slug(plan.Name), gcfg.Servers, target.Cfg);
            tabs.SelectedIndex = 1;
            SetBusy(true, "Installo il modpack...");
            AppendInstallLog("=== Importo il modpack " + plan.Name + " (" + plan.Type + " " + plan.Mc + ") in " + dir + " ===");
            var inst = new Installer();
            inst.Log = importer.Log;
            inst.Progress = importer.Progress;
            Task.Run(() =>
            {
                string err = null;
                List<ModInfo> client = new List<ModInfo>();
                try
                {
                    importer.Install(path, plan, target.Cfg, dir, inst);
                    if (plan.Kind != "mrpack")   // Modrinth separa già client e server; per CurseForge controllo io
                    {
                        UI(() => AppendInstallLog("Controllo quali mod servono solo al gioco (client)..."));
                        client = ModCheck.Scan(Path.Combine(dir, "mods"), true, s => UI(() => AppendInstallLog("  " + s))).Where(m => m.ClientOnly).ToList();
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                UI(() =>
                {
                    SetBusy(false, err == null ? "Fatto!" : "Errore");
                    if (err != null)
                    {
                        AppendInstallLog("ERRORE: " + err);
                        Msg("Importazione non completata:\n\n" + err);
                        RefreshAll();
                        RefreshServerList();
                        return;
                    }
                    var c = target.Cfg;
                    int moved = 0;
                    if (client.Count > 0)
                    {
                        var pick = ChooseItems("Mod solo per il gioco",
                            "Queste " + client.Count + " mod servono solo a chi gioca (grafica, menu, suoni...): sul server sono inutili o lo fanno crashare.\n" +
                            "Le sposto in \"" + ModCheck.DisabledFolder + "\" (non vengono cancellate, si possono rimettere dalla scheda Mod/Plugin).",
                            client.Select(m => m.Label).ToList(), true, "Sposta le selezionate");
                        if (pick != null) moved = ModCheck.Disable(c.ServerDir, pick.Select(i => client[i].File));
                    }
                    string portNote = EnsureUniquePort(target);
                    int mods = Optimizer.CountMods(c);
                    string ramNote = null;
                    if (mods >= 50 && c.RamGB < 6)
                    {
                        c.RamGB = mods >= 150 ? 8 : 6;
                        c.Save();
                        ramNote = "RAM del server impostata a " + c.RamGB + " GB (con " + mods + " mod sotto i 6 GB non parte). Puoi cambiarla nella scheda 3.";
                    }
                    var sb = new StringBuilder();
                    sb.AppendLine("Modpack importato: " + plan.Name + "  (" + plan.Type + " " + plan.Mc + ", " + mods + " mod).");
                    if (importer.SkippedClientFiles > 0) sb.AppendLine("Saltati " + importer.SkippedClientFiles + " file che servono solo al gioco.");
                    if (moved > 0) sb.AppendLine("Spostate " + moved + " mod solo client in \"" + ModCheck.DisabledFolder + "\".");
                    if (portNote != null) sb.AppendLine(portNote);
                    if (ramNote != null) sb.AppendLine(ramNote);
                    if (importer.ManualDownloads.Count > 0)
                    {
                        string listFile = Path.Combine(c.ServerDir, "mod-da-scaricare-a-mano.txt");
                        try { File.WriteAllLines(listFile, importer.ManualDownloads); } catch { }
                        sb.AppendLine();
                        sb.AppendLine(importer.ManualDownloads.Count + " mod NON si possono scaricare in automatico da CurseForge (lo vieta l'autore).");
                        sb.AppendLine("Scaricale dal sito e mettile nella cartella mods: l'elenco è in " + listFile + ".");
                        sb.AppendLine("In alternativa usa il \"Server Pack\" del modpack o la cartella dell'app CurseForge.");
                    }
                    sb.AppendLine();
                    sb.AppendLine("Consiglio: scheda 3 → \"Ottimizza automaticamente\" (profilo Modpack pesante), poi accendi il server.");
                    AppendInstallLog("=== Modpack pronto ===");
                    RefreshAll();
                    RefreshServerList();
                    SyncCombo();
                    Msg(sb.ToString());
                });
            });
        }

        ModpackVersion PickModrinthModpack()
        {
            using (var f = new Form())
            {
                f.Text = "Cerca modpack su Modrinth";
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.Font = Font;
                f.ClientSize = new Size(820, 520);
                f.MinimumSize = new Size(600, 400);
                var q = new TextBox();
                q.SetBounds(12, 12, 560, 24);
                q.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                var bSearch = new Button();
                bSearch.Text = "Cerca";
                bSearch.SetBounds(580, 10, 100, 28);
                bSearch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                var lv = new ListView();
                lv.View = View.Details;
                lv.FullRowSelect = true;
                lv.MultiSelect = false;
                lv.HideSelection = false;
                lv.Columns.Add("Modpack", 220);
                lv.Columns.Add("Download", 90);
                lv.Columns.Add("Descrizione", 470);
                lv.SetBounds(12, 46, 796, 300);
                lv.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                var lVer = new Label();
                lVer.Text = "Versione del modpack:";
                lVer.AutoSize = true;
                lVer.Location = new Point(12, 360);
                lVer.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
                var cbVer = new ComboBox();
                cbVer.DropDownStyle = ComboBoxStyle.DropDownList;
                cbVer.SetBounds(150, 356, 658, 24);
                cbVer.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                var status = new Label();
                status.AutoSize = true;
                status.Location = new Point(12, 392);
                status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
                var ok = new Button();
                ok.Text = "Installa questo modpack";
                ok.SetBounds(530, 480, 180, 30);
                ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                ok.Enabled = false;
                var no = new Button();
                no.Text = "Annulla";
                no.DialogResult = DialogResult.Cancel;
                no.SetBounds(716, 480, 92, 30);
                no.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                f.Controls.AddRange(new Control[] { q, bSearch, lv, lVer, cbVer, status, ok, no });
                f.CancelButton = no;
                f.AcceptButton = bSearch;

                List<ModpackVersion> vers = null;
                ModpackVersion result = null;
                Action search = () =>
                {
                    status.Text = "Cerco...";
                    string text = q.Text.Trim();
                    Task.Run(() =>
                    {
                        List<ModpackHit> hits = null;
                        string e2 = null;
                        try { hits = ModpackImporter.SearchModrinth(text); } catch (Exception ex) { e2 = ex.Message; }
                        if (f.IsDisposed) return;
                        f.BeginInvoke((Action)(() =>
                        {
                            lv.Items.Clear();
                            if (e2 != null) { status.Text = "Errore: " + e2; return; }
                            foreach (var h in hits)
                            {
                                var it = new ListViewItem(new[] { h.Title, h.Downloads.ToString("N0"), h.Description });
                                it.Tag = h;
                                lv.Items.Add(it);
                            }
                            status.Text = hits.Count + " modpack trovati. Selezionane uno.";
                        }));
                    });
                };
                bSearch.Click += (s, e) => search();
                lv.SelectedIndexChanged += (s, e) =>
                {
                    if (lv.SelectedItems.Count == 0) return;
                    var h = (ModpackHit)lv.SelectedItems[0].Tag;
                    cbVer.Items.Clear();
                    ok.Enabled = false;
                    status.Text = "Leggo le versioni di " + h.Title + "...";
                    Task.Run(() =>
                    {
                        List<ModpackVersion> v = null;
                        string e2 = null;
                        try { v = ModpackImporter.ModrinthVersions(h.Id); } catch (Exception ex) { e2 = ex.Message; }
                        if (f.IsDisposed) return;
                        f.BeginInvoke((Action)(() =>
                        {
                            if (e2 != null) { status.Text = "Errore: " + e2; return; }
                            vers = v;
                            foreach (var x in v) cbVer.Items.Add(x.Label);
                            if (cbVer.Items.Count > 0) { cbVer.SelectedIndex = 0; ok.Enabled = true; }
                            status.Text = v.Count == 0 ? "Nessuna versione per Fabric/Forge/NeoForge." : "La prima versione è la più recente.";
                        }));
                    });
                };
                ok.Click += (s, e) =>
                {
                    if (vers == null || cbVer.SelectedIndex < 0) return;
                    result = vers[cbVer.SelectedIndex];
                    f.DialogResult = DialogResult.OK;
                };
                if (Theme.Dark) Theme.Apply(f, true);
                f.Shown += (s, e) => search();
                return f.ShowDialog(this) == DialogResult.OK ? result : null;
            }
        }

        // ------------------------------------------------------------ mod solo client

        void CheckClientMods()
        {
            if (!cfg.Installed || cfg.ServerType == "Vanilla" || cfg.ServerType == "Paper")
            {
                Msg("Il controllo serve per i server con mod (Fabric, Forge, NeoForge).");
                return;
            }
            if (server.IsRunning && !Ask("Il server è acceso: le mod spostate verranno tolte al prossimo riavvio. Continuare?")) return;
            string mods = Path.Combine(cfg.ServerDir, "mods");
            var sl = cur;
            lblAddon.Text = "Controllo le mod (metadati e Modrinth)...";
            Task.Run(() =>
            {
                List<ModInfo> all = null;
                string err = null;
                try { all = ModCheck.Scan(mods, true, s => sl.Add("[Mod] " + s)); }
                catch (Exception ex) { err = ex.Message; }
                UI(() =>
                {
                    RefreshMods();
                    if (err != null) { Msg("Controllo non riuscito: " + err); return; }
                    var client = all.Where(m => m.ClientOnly).ToList();
                    int unknown = all.Count(m => !m.ClientOnly && !m.ServerOk);
                    if (client.Count == 0)
                    {
                        Msg("Nessuna mod solo client trovata (" + all.Count + " mod controllate)." +
                            (unknown > 0 ? "\n\n" + unknown + " mod non si possono verificare (non sono su Modrinth e non lo dichiarano). " +
                                           "Se il server crasha all'avvio per colpa di una di queste, te lo dico io e ti propongo di toglierla." : ""));
                        return;
                    }
                    var pick = ChooseItems("Mod solo per il gioco",
                        "Su " + all.Count + " mod, queste " + client.Count + " servono solo a chi gioca: sul server sono inutili o lo fanno crashare.\n" +
                        "Le sposto in \"" + ModCheck.DisabledFolder + "\": non vengono cancellate e si possono rimettere con \"Mod disattivate...\".",
                        client.Select(m => m.Label).ToList(), true, "Sposta le selezionate");
                    if (pick == null || pick.Count == 0) return;
                    int n = ModCheck.Disable(cfg.ServerDir, pick.Select(i => client[i].File));
                    sl.Add("[Mod] Spostate " + n + " mod solo client in " + ModCheck.DisabledFolder + ".");
                    RefreshMods();
                    Msg("Spostate " + n + " mod." + (server.IsRunning ? "\nRiavvia il server per applicare." : ""));
                });
            });
        }

        void ShowDisabledMods()
        {
            if (!cfg.Installed) return;
            var files = ModCheck.DisabledFiles(cfg.ServerDir);
            if (files.Length == 0) { Msg("Non ci sono mod disattivate."); return; }
            var pick = ChooseItems("Mod disattivate", "Mod spostate in \"" + ModCheck.DisabledFolder + "\". Spunta quelle da rimettere nel server.",
                files, false, "Rimetti le selezionate");
            if (pick == null || pick.Count == 0) return;
            int n = ModCheck.Enable(cfg.ServerDir, pick.Select(i => files[i]));
            RefreshMods();
            Msg("Rimesse " + n + " mod." + (server.IsRunning ? "\nRiavvia il server per applicare." : ""));
        }

        /// <summary>Dopo un crash all'avvio: se la colpa è di mod solo client, propone di toglierle e riaccende.</summary>
        void CheckCrashForClientMods(ServerSlot sl)
        {
            var c = sl.Cfg;
            if (c.ServerType == "Vanilla" || c.ServerType == "Paper") return;
            var lines = sl.RecentLines(600);
            Task.Run(() =>
            {
                List<string> sus;
                try { sus = ModCheck.SuspectsFromCrash(lines, c.ServerDir); } catch { return; }
                if (sus.Count == 0) return;
                sl.Proc.StopRequested = true;   // niente riavvio automatico: prima chiedo
                UI(() =>
                {
                    sl.Add("[Mod] Il server si è chiuso per colpa di mod solo client: " + string.Join(", ", sus));
                    if (!Ask("\"" + c.DisplayName + "\" si è chiuso all'avvio: il log dice che queste mod funzionano solo nel gioco (client):\n\n• " +
                             string.Join("\n• ", sus) + "\n\nLe sposto in \"" + ModCheck.DisabledFolder + "\" e riaccendo il server?")) return;
                    ModCheck.Disable(c.ServerDir, sus);
                    if (sl == cur) RefreshMods();
                    StartSlot(sl, false);
                });
            });
        }

        // ------------------------------------------------------------ spark

        void FindLag()
        {
            var sl = cur;
            var c = sl.Cfg;
            if (!c.Installed || c.ServerType == "Vanilla") { Msg("spark si usa con Paper, Fabric, Forge o NeoForge."); return; }
            if (!sl.Proc.IsRunning) { Msg("Accendi prima il server e fai entrare qualcuno: spark osserva il server mentre si gioca."); return; }
            bool builtIn = c.ServerType == "Paper" && new VersionComparer().Compare(c.McVersion, "1.21") >= 0;
            if (!builtIn && !HasAddon("spark"))
            {
                if (!Ask("Serve spark (gratuito, molto usato). Lo installo adesso?\nPoi il server va riavviato una volta.")) return;
                string t = c.ServerType, v = c.McVersion, dir = c.ServerDir;
                sl.Add("[spark] Installo spark...");
                Task.Run(() =>
                {
                    string err = null;
                    bool ok = false;
                    try
                    {
                        var inst = new Installer();
                        inst.Log = s => sl.Add("[spark] " + s);
                        ok = inst.InstallFromModrinth("spark", t, v, dir, null);
                    }
                    catch (Exception ex) { err = ex.Message; }
                    UI(() =>
                    {
                        RefreshMods();
                        if (!ok) { Msg("spark non installato" + (err != null ? ": " + err : " (nessuna versione per " + t + " " + v + ").")); return; }
                        if (Ask("spark installato. Riavvio il server adesso? Poi premi di nuovo \"Trova cosa fa lag\".")) RestartSlot(sl);
                    });
                });
                return;
            }
            Msg("Per 60 secondi spark osserva il server: intanto giocate normalmente (meglio se nel momento in cui c'è lag).\n\n" +
                "Alla fine ti do un link con la classifica di cosa pesa di più.");
            var rx = new Regex(@"https://spark\.lucko\.me/[A-Za-z0-9]+");
            bool done = false;
            Action<string> h = null;
            h = line =>
            {
                var m = rx.Match(line);
                if (!m.Success || done) return;
                done = true;
                sl.Proc.Output -= h;
                string url = m.Value;
                UI(() =>
                {
                    sl.Add("[spark] Risultato: " + url);
                    try { Clipboard.SetText(url); } catch { }
                    if (Ask("spark ha finito! Il risultato è qui (link già copiato):\n\n" + url +
                            "\n\nIn alto trovi le mod/plugin che pesano di più. Apro il link?"))
                        try { Process.Start(url); } catch { }
                });
            };
            sl.Proc.Output += h;
            sl.Proc.SendCommand("spark profiler start --timeout 60");
            sl.Add("> spark profiler start --timeout 60");
            Task.Run(() =>
            {
                System.Threading.Thread.Sleep(240000);
                if (done) return;
                done = true;
                sl.Proc.Output -= h;
                UI(() => sl.Add("[spark] Nessun risultato dopo 4 minuti: controlla la console (serve internet per caricare il risultato)."));
            });
        }

        // ------------------------------------------------------------ ottimizza

        void OptimizeDialog()
        {
            if (!cfg.Installed) { Msg("Prima installa il server."); return; }
            int cpus = Environment.ProcessorCount, ram = ServerTools.TotalRamGB(), mods = Optimizer.CountMods(cfg);
            string rec = Optimizer.Recommend(cfg, cpus);
            int recIdx = Array.IndexOf(Optimizer.Keys, rec);
            var labels = Optimizer.Labels.Select((l, i) => l + (i == recIdx ? "    ← consigliato per questo PC" : "")).ToArray();
            int pick = ChooseOne("Ottimizza automaticamente",
                "PC: " + cpus + " thread, " + (ram > 0 ? ram + " GB di RAM" : "RAM ?") + "   ·   server: " + cfg.ServerType + " " + cfg.McVersion +
                (mods > 0 ? ", " + mods + " mod" : "") + "\nScegli il profilo (prima salvo una copia delle impostazioni attuali):", labels);
            if (pick < 0) return;
            string summary;
            try { summary = Optimizer.Apply(cfg, Optimizer.Keys[pick]); }
            catch (Exception ex) { Msg("Non applicato: " + ex.Message); return; }
            LoadProps();
            numIdle.Value = Math.Max(numIdle.Minimum, Math.Min(numIdle.Maximum, cfg.IdleMinutes));
            ApplyWakeTexts(cur);
            personalizeLoaded = false;
            Log("[Ottimizza] " + summary.Replace("\r\n", " ").Replace("\n", " "));
            if (server.IsRunning)
            {
                if (Ask(summary + "\nPer applicarle bisogna riavviare il server. Lo riavvio adesso?")) RestartServer();
            }
            else Msg(summary + "\nValgono dalla prossima accensione.");
        }

        void RestoreOptimize()
        {
            if (!cfg.Installed) return;
            if (!Optimizer.HasBackup(cfg)) { Msg("Non c'è niente da ripristinare (\"Ottimizza\" non è ancora stato usato su questo server)."); return; }
            if (!Ask("Rimetto le impostazioni com'erano prima della prima ottimizzazione?")) return;
            int n = Optimizer.Restore(cfg);
            LoadProps();
            Msg("Ripristinati " + n + " file." + (server.IsRunning ? "\nRiavvia il server per applicare." : ""));
        }

        /// <summary>Etichetta che va a capo entro maxWidth (si aggiorna quando cambia la larghezza della finestra).</summary>
        static Label LblW(Control p, string text, int x, int y, int maxWidth)
        {
            var l = Lbl(p, text, x, y);
            l.MaximumSize = new Size(Math.Max(100, maxWidth), 0);
            return l;
        }

        // ================================================================== 5. AUTOMATICO (backup, riavvii, pausa)

        void BuildAutoTab()
        {
            var p = NewTab("5. Backup, riavvii e pausa");
            p.AutoScroll = true;

            var g1 = new GroupBox();
            g1.Text = "Riavvii";
            Put(p, g1, 12, 10, 800, 84);
            chkRestart = Chk(g1, "Riavvio programmato ogni giorno alle", 15, 24, cfg.DailyRestart);
            dtRestart = Put(g1, TimePicker(TimeOf(cfg.RestartTime)), 265, 22, 70, 0);
            var lr = Lbl(g1, "i giocatori vengono avvisati 5 minuti prima; libera la RAM", 345, 24);
            lr.ForeColor = Color.DimGray;
            chkAutoRestart = Chk(g1, "Riavvia da solo se crasha", 15, 54, cfg.AutoRestart);
            chkAutoRestart.CheckedChanged += (s, e) => { if (loadingServer) return; cfg.AutoRestart = chkAutoRestart.Checked; cfg.Save(); };

            var g2 = new GroupBox();
            g2.Text = "Pausa automatica quando il server è vuoto";
            Put(p, g2, 12, 102, 800, 86);
            chkIdle = Chk(g2, "Metti in pausa il server quando è vuoto da", 15, 24, cfg.IdleSleep);
            numIdle = new NumericUpDown();
            numIdle.Minimum = 1;
            numIdle.Maximum = 240;
            numIdle.Value = Math.Max(1, Math.Min(240, cfg.IdleMinutes));
            Put(g2, numIdle, 300, 24, 55, 0);
            Lbl(g2, "minuti", 360, 24);
            lblIdle = LblW(g2, "Chi prova a entrare lo riaccende da solo (circa un minuto). I testi si cambiano nella scheda 7. Personalizza, " +
                               "il piano energetico nella scheda 8. PC e programma.", 15, 52, 770);
            lblIdle.ForeColor = Color.DimGray;
            chkIdle.CheckedChanged += (s, e) =>
            {
                if (loadingServer) return;
                cfg.IdleSleep = chkIdle.Checked;
                cfg.Save();
                cur.EmptySince = DateTime.MinValue;
                if (!cfg.IdleSleep && cur.Sleeping) StopServer();
            };
            numIdle.ValueChanged += (s, e) => { if (loadingServer) return; cfg.IdleMinutes = (int)numIdle.Value; cfg.Save(); };

            var g3 = new GroupBox();
            g3.Text = "Backup del mondo";
            Put(p, g3, 12, 196, 800, 300);
            chkBackup = Chk(g3, "Backup automatico ogni giorno alle", 15, 24, cfg.BackupEnabled);
            dtBackup = Put(g3, TimePicker(TimeOf(cfg.BackupTime)), 265, 22, 70, 0);
            Lbl(g3, "conserva gli ultimi", 345, 24);
            numKeep = new NumericUpDown();
            numKeep.Minimum = 1;
            numKeep.Maximum = 60;
            numKeep.Value = Math.Max(1, Math.Min(60, cfg.BackupKeep));
            Put(g3, numKeep, 460, 24, 55, 0);
            Lbl(g3, "backup", 522, 24);
            Btn(g3, "Fai un backup adesso", 15, 56, 170, (s, e) => RunBackup(false));
            Btn(g3, "Ripristina selezionato", 191, 56, 170, (s, e) => RestoreBackup());
            Btn(g3, "Apri cartella backup", 367, 56, 160, (s, e) => OpenFolder(cfg.BackupDir));
            lbBackups = new ListBox();
            lbBackups.IntegralHeight = false;
            Put(g3, lbBackups, 15, 94, 770, 190);

            EventHandler save = (s, e) =>
            {
                if (loadingServer) return;
                cfg.BackupEnabled = chkBackup.Checked;
                cfg.BackupTime = dtBackup.Value.ToString("HH:mm");
                cfg.BackupKeep = (int)numKeep.Value;
                cfg.DailyRestart = chkRestart.Checked;
                cfg.RestartTime = dtRestart.Value.ToString("HH:mm");
                cfg.Save();
            };
            chkBackup.CheckedChanged += save;
            dtBackup.ValueChanged += save;
            numKeep.ValueChanged += save;
            chkRestart.CheckedChanged += save;
            dtRestart.ValueChanged += save;

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width, h = p.ClientSize.Height;
                if (w < 300 || h < 300) return;
                g1.Width = w - 24; g2.Width = w - 24; g3.Width = w - 24;
                lblIdle.MaximumSize = new Size(Math.Max(200, g2.Width - 30), 0);
                g3.Height = Math.Max(220, h - 196 - 12);
                lbBackups.Size = new Size(g3.Width - 30, g3.Height - 94 - 14);
            });
        }

        // ================================================================== 6. GIOCATORI (playit, in casa, whitelist)

        void BuildPlayersTab()
        {
            var p = NewTab("6. Giocatori");
            p.AutoScroll = true;
            playit.Log = s => Log("[playit] " + s);

            var g1 = new GroupBox();
            g1.Text = "Da fuori casa (internet)  —  playit.gg: gratis, senza toccare il router";
            Put(p, g1, 12, 10, 800, 250);
            Lbl(g1, "Premi il pulsante e il programma prepara tutto da solo.\n" +
                    "Solo la prima volta si apre il browser: accedi a playit.gg (o crea un account gratuito) e premi Approva.", 15, 22);
            btnPlayit = Btn(g1, "Attiva accesso da internet", 15, 62, 260, (s, e) => PlayitSetup());
            btnPlayit.Height = 36;
            btnPlayit.Font = new Font(Font, FontStyle.Bold);
            btnPlayitStop = Btn(g1, "Disattiva", 285, 66, 110, (s, e) => PlayitDisable());
            Btn(g1, "Scollega account playit", 405, 66, 180, (s, e) => PlayitReset());

            Lbl(g1, "Indirizzo da mandare agli amici  (Multigiocatore > Aggiungi server):", 15, 112);
            txtAddress = new TextBox();
            txtAddress.ReadOnly = true;
            txtAddress.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
            txtAddress.BackColor = Color.White;
            Put(g1, txtAddress, 15, 136, 440, 0);
            Btn(g1, "Copia indirizzo", 465, 138, 140, (s, e) =>
            {
                if (txtAddress.Text.Contains(".")) { Clipboard.SetText(txtAddress.Text); lblPlayit.Text = "Indirizzo copiato: incollalo nella chat con i tuoi amici."; }
            });
            lblBedrockAddr = Lbl(g1, "", 15, 180);
            lblPlayit = Lbl(g1, "", 15, 206);
            lblPlayit.Font = new Font(Font, FontStyle.Bold);

            var g2 = new GroupBox();
            g2.Text = "In casa (stessa rete Wi-Fi o cavo): non serve playit";
            Put(p, g2, 12, 268, 800, 96);
            lblIps = Lbl(g2, "", 15, 22);

            var g3 = new GroupBox();
            g3.Text = "Chi può entrare (whitelist)";
            Put(p, g3, 12, 372, 800, 160);
            lblWl = Lbl(g3, "", 15, 22);
            lblWl.Font = new Font(Font, FontStyle.Bold);
            Lbl(g3, "Nome Minecraft dell'amico:", 15, 52);
            txtFriend = Put(g3, new TextBox(), 180, 52, 200, 0);
            Btn(g3, "Aggiungi", 390, 50, 90, (s, e) => Whitelist("add"));
            Btn(g3, "Togli", 485, 50, 80, (s, e) => Whitelist("remove"));
            Btn(g3, "Mostra elenco", 570, 50, 120, (s, e) => QuickCmd("whitelist list"));
            Btn(g3, "Attiva whitelist", 15, 88, 150, (s, e) => { if (QuickCmdOk("whitelist on")) lblWl.Text = "Whitelist ATTIVA: entrano solo i nomi aggiunti."; });
            Btn(g3, "Disattiva whitelist", 170, 88, 150, (s, e) => { if (QuickCmdOk("whitelist off")) lblWl.Text = "Whitelist disattivata: entra chiunque abbia l'indirizzo."; });
            var hint = Lbl(g3, "Il server deve essere acceso. Con il server su internet la whitelist attiva è il modo più sicuro.", 15, 126);
            hint.ForeColor = Color.DimGray;

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width;
                if (w < 200) return;
                g1.Width = w - 24; g2.Width = w - 24; g3.Width = w - 24;
            });
        }

        // ================================================================== 8. PC E PROGRAMMA (valgono per tutti i server)

        void BuildPcTab()
        {
            var p = NewTab("8. PC e programma");
            p.AutoScroll = true;
            var wraps = new List<KeyValuePair<Label, GroupBox>>();
            Func<GroupBox, string, int, int, Label> W = (g, t, x, y) =>
            {
                var l = LblW(g, t, x, y, 400);
                wraps.Add(new KeyValuePair<Label, GroupBox>(l, g));
                return l;
            };

            // ---------------- colonna sinistra: priorità e prestazioni
            var gA = new GroupBox();
            gA.Text = "Permessi";
            Put(p, gA, 12, 10, 440, 86);
            lblAdmin = W(gA, IsAdminNow
                ? "✔ Il programma è amministratore: tutte le funzioni sono disponibili."
                : "✖ Il programma NON è amministratore: 'Tempo reale' diventa 'Alta' e i servizi di Windows non si possono abbassare.", 15, 20);
            lblAdmin.ForeColor = IsAdminNow ? Color.DarkGreen : Color.DarkOrange;
            btnElevate = Btn(gA, "Riavvia come amministratore", 15, 50, 210, (s, e) => RelaunchAsAdmin());
            btnElevate.Visible = !IsAdminNow;

            var gB = new GroupBox();
            gB.Text = "Priorità CPU dei server";
            Put(p, gB, 12, 104, 440, 92);
            cbPrio = new ComboBox();
            cbPrio.DropDownStyle = ComboBoxStyle.DropDownList;
            cbPrio.Items.AddRange(PriorityManager.ServerLabels);
            Put(gB, cbPrio, 15, 24, 200, 0);
            int pi = Array.IndexOf(PriorityManager.ServerKeys, gcfg.ServerPriority);
            cbPrio.SelectedIndex = pi < 0 ? 1 : pi;
            cbPrio.SelectedIndexChanged += (s, e) => OnPrioChanged();
            lblPrioNow = Lbl(gB, "", 225, 27);
            var lp = W(gB, "'Alta' è il compromesso migliore. 'Tempo reale' può bloccare mouse e tastiera quando il server è sotto sforzo.", 15, 54);
            lp.ForeColor = Color.DimGray;

            var gC = new GroupBox();
            gC.Text = "Modalità server dedicato";
            Put(p, gC, 12, 204, 440, 250);
            chkBoost = Chk(gC, "ATTIVA: la precedenza va solo ai server", 15, 24, gcfg.BoostEnabled);
            chkBoost.Font = new Font(Font, FontStyle.Bold);
            Lbl(gC, "Intensità:", 15, 56);
            cbLevel = new ComboBox();
            cbLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            cbLevel.Items.Add("Moderata  —  gli altri programmi restano usabili");
            cbLevel.Items.Add("Massima  —  gli altri lavorano solo a CPU libera");
            Put(gC, cbLevel, 85, 53, 330, 0);
            cbLevel.SelectedIndex = gcfg.BoostAggressive ? 1 : 0;
            chkTrim = Chk(gC, "Quando la attivi, libera anche la RAM degli altri programmi", 15, 86, gcfg.BoostTrim);
            lblBoost = W(gC, "", 15, 114);
            lblBoost.Font = new Font(Font, FontStyle.Bold);
            var lb = W(gC, "Abbassa CPU, disco e RAM di tutti gli altri processi e dà ai server la precedenza su disco e RAM. " +
                           "Si riapplica ogni 30 secondi ai programmi nuovi; non tocca i processi vitali di Windows né Gestione attività. " +
                           "Spegnendola tutto torna com'era.", 15, 160);
            lb.ForeColor = Color.DimGray;
            chkBoost.CheckedChanged += (s, e) => ToggleBoost();
            cbLevel.SelectedIndexChanged += (s, e) =>
            {
                gcfg.BoostAggressive = cbLevel.SelectedIndex == 1;
                gcfg.Save();
                if (gcfg.BoostEnabled) ReapplyBoost();
            };
            chkTrim.CheckedChanged += (s, e) => { gcfg.BoostTrim = chkTrim.Checked; gcfg.Save(); };

            // ---------------- colonna destra: energia, Windows, avvio, programma
            var gD = new GroupBox();
            gD.Text = "Energia";
            Put(p, gD, 464, 10, 440, 110);
            chkPowerAuto = Chk(gD, "Piano energetico automatico", 15, 22, gcfg.AutoPowerPlan);
            var le = W(gD, "'Prestazioni eccellenti' con un server acceso, 'Risparmio di energia' con tutti spenti. Se mancano li crea.", 35, 44);
            le.ForeColor = Color.DimGray;
            chkSaverPause = Chk(gD, "'Risparmio di energia' anche quando i server sono in pausa", 35, 80, gcfg.SaverWhenPaused);
            chkSaverPause.Enabled = gcfg.AutoPowerPlan;
            chkPowerAuto.CheckedChanged += (s, e) => { chkSaverPause.Enabled = chkPowerAuto.Checked; TogglePowerAuto(); };
            chkSaverPause.CheckedChanged += (s, e) =>
            {
                gcfg.SaverWhenPaused = chkSaverPause.Checked;
                gcfg.Save();
                powerNow = null;
                Task.Run(() => ApplyPowerPlan());
            };

            var gE = new GroupBox();
            gE.Text = "Windows  (una volta sola, chiede i permessi di amministratore)";
            Put(p, gE, 464, 128, 440, 104);
            chkPower = Chk(gE, "Niente sospensione né ibernazione (il PC resta raggiungibile)", 15, 22, true);
            chkFirewall = Chk(gE, "Apri nel firewall le porte di tutti i server", 15, 46, true);
            Btn(gE, "Applica", 15, 70, 120, (s, e) => ApplyTweaks());

            var gF = new GroupBox();
            gF.Text = "Avvio con Windows";
            Put(p, gF, 464, 240, 440, 132);
            chkAutostartWin = Chk(gF, "Apri il programma all'accensione del PC (nascosto)", 15, 22, SystemTweaks.IsAutostart());
            var la = W(gF, "Accende da solo i server con la spunta nella scheda 0. I miei server.", 35, 44);
            la.ForeColor = Color.DimGray;
            chkAutostartAdmin = Chk(gF, "...come amministratore (per 'Tempo reale' e la modalità dedicata)", 35, 66, gcfg.AutostartAdmin);
            chkAutostartWin.CheckedChanged += (s, e) => ApplyAutostart();
            chkAutostartAdmin.CheckedChanged += (s, e) =>
            {
                gcfg.AutostartAdmin = chkAutostartAdmin.Checked;
                gcfg.Save();
                if (chkAutostartWin.Checked) ApplyAutostart();
            };
            Btn(gF, "Accesso automatico a Windows...", 15, 94, 230, (s, e) => AutoLoginHelp());
            themeTip.SetToolTip(gF, "L'accesso automatico serve perché dopo un riavvio (es. aggiornamenti) il PC entri da solo in Windows.");

            var gG = new GroupBox();
            gG.Text = "Programma";
            Put(p, gG, 464, 380, 440, 124);
            Lbl(gG, "Versione " + AppInfo.Version, 15, 24);
            btnUpdate = Btn(gG, "Cerca aggiornamenti", 120, 20, 170, (s, e) => OnUpdateClick());
            chkAutoUpdate = Chk(gG, "Controlla all'avvio", 300, 24, gcfg.CheckUpdates);
            chkAutoUpdate.CheckedChanged += (s, e) => { gcfg.CheckUpdates = chkAutoUpdate.Checked; gcfg.Save(); };
            lblUpdate = Lbl(gG, "", 15, 52);
            Lbl(gG, "Modalità notte:", 15, 88);
            cbTheme = new ComboBox();
            cbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
            cbTheme.Items.AddRange(new object[] { "Automatica (come Windows)", "Chiara", "Scura (notte)" });
            Put(gG, cbTheme, 120, 85, 200, 0);
            cbTheme.SelectedIndex = gcfg.ThemeMode == "light" ? 1 : gcfg.ThemeMode == "dark" ? 2 : 0;
            cbTheme.SelectedIndexChanged += (s, e) =>
            {
                if (themeLoading) return;
                SetTheme(cbTheme.SelectedIndex == 1 ? "light" : cbTheme.SelectedIndex == 2 ? "dark" : "auto");
            };
            var lnk = new LinkLabel();
            lnk.Text = "novità su GitHub";
            lnk.AutoSize = true;
            lnk.LinkClicked += (s, e) => { try { Process.Start(AppInfo.RepoUrl + "/releases"); } catch { } };
            Put(gG, lnk, 330, 88, 0, 0);

            var left = new[] { gA, gB, gC };
            var right = new[] { gD, gE, gF, gG };
            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width;
                if (w < 400) return;
                int cw = (w - 36) / 2;
                foreach (var g in left) { g.Left = 12; g.Width = cw; }
                foreach (var g in right) { g.Left = 24 + cw; g.Width = cw; }
                foreach (var kv in wraps) kv.Key.MaximumSize = new Size(Math.Max(150, kv.Value.Width - kv.Key.Left - 12), 0);
                cbLevel.Width = Math.Max(200, cw - 100);
            });
        }

        // ================================================================== 7. PERSONALIZZA

        void BuildCustomizeTab()
        {
            var p = NewTab("7. Personalizza");
            p.AutoScroll = true;
            var mono = new Font("Consolas", 10F);
            var tip = new ToolTip();

            // ---------------- barra dei colori: vale per qualsiasi casella di testo qui sotto
            Lbl(p, "Colori:", 12, 14);
            for (int i = 0; i < 16; i++)
            {
                var b = new Button();
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Color.DimGray;
                b.BackColor = McText.Colors[i];
                b.Tag = "keep";
                b.TabStop = false;
                string code = "&" + McText.ColorCodes[i];
                b.Click += (s, e) => InsertCode(code);
                tip.SetToolTip(b, McText.ColorLabels[i] + "  (" + code + ")");
                Put(p, b, 62 + i * 22, 12, 20, 20);
            }
            string[] fmtText = { "G", "C", "S", "B", "⟲" };
            string[] fmtCode = { "&l", "&o", "&n", "&m", "&r" };
            string[] fmtTip = { "Grassetto", "Corsivo", "Sottolineato", "Barrato", "Torna normale (toglie colore e stili)" };
            FontStyle[] fmtStyle = { FontStyle.Bold, FontStyle.Bold | FontStyle.Italic, FontStyle.Bold | FontStyle.Underline, FontStyle.Bold | FontStyle.Strikeout, FontStyle.Regular };
            for (int i = 0; i < fmtText.Length; i++)
            {
                var b = new Button();
                b.Text = fmtText[i];
                b.Font = new Font("Segoe UI", 8.5F, fmtStyle[i]);
                b.TabStop = false;
                string code = fmtCode[i];
                b.Click += (s, e) => InsertCode(code);
                tip.SetToolTip(b, fmtTip[i] + "  (" + code + ")");
                Put(p, b, 424 + i * 28, 10, 26, 24);
            }
            var lc = Lbl(p, "clic = inserisce il colore nella casella dove c'è il cursore", 570, 14);
            lc.ForeColor = Color.DimGray;

            // ---------------- lista Multigiocatore
            var g1 = new GroupBox();
            g1.Text = "Come appare nella lista Multigiocatore";
            Put(p, g1, 12, 44, 800, 232);
            picIcon = new PictureBox();
            picIcon.SizeMode = PictureBoxSizeMode.Zoom;
            picIcon.BorderStyle = BorderStyle.FixedSingle;
            picIcon.BackColor = Color.FromArgb(70, 70, 70);
            Put(g1, picIcon, 15, 24, 80, 80);
            Btn(g1, "Cambia logo...", 15, 112, 100, (s, e) => ChooseIcon());
            tip.SetToolTip(picIcon, "Qualsiasi immagine: la rimpicciolisco io a 64x64.");
            var lnkNoIcon = new LinkLabel();
            lnkNoIcon.Text = "Togli logo";
            lnkNoIcon.AutoSize = true;
            lnkNoIcon.LinkClicked += (s, e) => { DisposePending(); removeIcon = true; SetIconPreview(null); };
            Put(g1, lnkNoIcon, 15, 146, 0, 0);

            Lbl(g1, "Riga 1:", 125, 26);
            txtMotd1 = Put(g1, new TextBox(), 180, 24, 600, 0);
            Lbl(g1, "Riga 2:", 125, 56);
            txtMotd2 = Put(g1, new TextBox(), 180, 54, 600, 0);
            listPreview = Put(g1, new ServerListPreview(), 125, 90, 655, 128);

            // ---------------- messaggi (chat e pausa) con un'unica anteprima
            var g2 = new GroupBox();
            g2.Text = "Messaggi  (vuoto = nessun messaggio)";
            Put(p, g2, 12, 284, 800, 208);
            string[] labels = { "Benvenuto:", "Quando esce:", "Server acceso:", "In pausa:", "Si sta accendendo:" };
            string[] tips =
            {
                "In chat a chi entra.",
                "In chat a tutti quando qualcuno esce.",
                "In chat al primo che entra dopo un'accensione.",
                "Nella lista Multigiocatore mentre il server è in pausa (pausa automatica, scheda 5).",
                "A chi prova a entrare mentre il server è in pausa: intanto si riaccende."
            };
            var boxes = new TextBox[5];
            for (int i = 0; i < 5; i++)
            {
                var l = Lbl(g2, labels[i], 15, 24 + i * 30);
                boxes[i] = Put(g2, new TextBox(), 135, 22 + i * 30, 330, 0);
                tip.SetToolTip(l, tips[i]);
                tip.SetToolTip(boxes[i], tips[i]);
            }
            txtWelcome = boxes[0]; txtLeave = boxes[1]; txtStartMsg = boxes[2]; txtPauseMotd = boxes[3]; txtWakeMsg = boxes[4];
            var lv = Lbl(g2, "{giocatore} = nome   ·   {online} = quanti sono dentro   ·   {max} = posti", 15, 178);
            lv.ForeColor = Color.DimGray;
            chatPreview = Put(g2, new ChatPreview(), 475, 22, 310, 170);

            var bSave = Btn(p, "Salva e applica", 12, 500, 150, (s, e) => SavePersonalize());
            bSave.Font = new Font(Font, FontStyle.Bold);
            var bDef = Btn(p, "Ripristina predefiniti", 170, 500, 170, (s, e) => DefaultPersonalize());
            lblPersInfo = Lbl(p, "Logo e descrizione si vedono dopo un riavvio del server: te lo chiedo io quando salvi.", 350, 506);
            lblPersInfo.ForeColor = Color.DimGray;

            foreach (var t in new[] { txtMotd1, txtMotd2, txtWelcome, txtLeave, txtStartMsg, txtPauseMotd, txtWakeMsg })
            {
                var tb = t;
                tb.Font = mono;
                tb.Enter += (s, e) => lastTextFocus = tb;
                tb.TextChanged += (s, e) => UpdatePreviews();
            }
            lastTextFocus = txtMotd1;

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width;
                if (w < 300) return;
                int gw = w - 24;
                g1.Width = gw; g2.Width = gw;
                txtMotd1.Width = gw - 180 - 15;
                txtMotd2.Width = gw - 180 - 15;
                listPreview.Width = gw - 125 - 15;
                int half = (gw - 135 - 30) / 2;
                foreach (var t in boxes) t.Width = half;
                chatPreview.Left = 135 + half + 12;
                chatPreview.Width = gw - chatPreview.Left - 15;
            });
        }

        void InsertCode(string code)
        {
            var t = lastTextFocus ?? txtMotd1;
            int pos = t.SelectionStart;
            t.SelectedText = code;
            t.Focus();
            t.SelectionStart = pos + code.Length;
            t.SelectionLength = 0;
        }

        void UpdatePreviews()
        {
            if (listPreview == null) return;
            listPreview.Line1 = txtMotd1.Text;
            listPreview.Line2 = txtMotd2.Text;
            listPreview.Players = "3/" + maxPlayers;
            listPreview.Invalidate();
            string max = maxPlayers.ToString();
            chatPreview.Lines = new[]
            {
                McText.Fill(txtWelcome.Text, "Marco_07", "3", max),
                McText.Fill(txtLeave.Text, "Giulia", "2", max),
                McText.Fill(txtStartMsg.Text, "Marco_07", "3", max),
                txtPauseMotd.Text,
                txtWakeMsg.Text
            };
            chatPreview.Invalidate();
        }

        void DisposePending()
        {
            if (pendingIcon != null) { pendingIcon.Dispose(); pendingIcon = null; }
        }

        void SetIconPreview(Image img)
        {
            var old = picIcon.Image;
            picIcon.Image = img != null ? new Bitmap(img) : null;
            listPreview.Icon = picIcon.Image;
            listPreview.Invalidate();
            if (old != null) old.Dispose();
        }

        void LoadPersonalize()
        {
            personalizeLoaded = true;
            removeIcon = false;
            DisposePending();
            string l1 = "", l2 = "";
            int max = 20;
            Bitmap icon = null;
            if (cfg.Installed)
            {
                try
                {
                    McText.GetMotd(cfg.ServerDir, out l1, out l2);
                    int m;
                    if (int.TryParse(Props.Get(cfg.ServerDir, "max-players"), out m)) max = m;
                    string ip = McText.IconPath(cfg.ServerDir);
                    if (File.Exists(ip)) icon = McText.LoadImage(ip);
                }
                catch (Exception ex) { Log("[Personalizza] " + ex.Message); }
            }
            txtMotd1.Text = l1;
            txtMotd2.Text = l2;
            maxPlayers = max;
            txtWelcome.Text = cfg.WelcomeMsg;
            txtLeave.Text = cfg.LeaveMsg;
            txtStartMsg.Text = cfg.StartMsg;
            txtPauseMotd.Text = cfg.PauseMotd;
            txtWakeMsg.Text = cfg.WakeMsg;
            SetIconPreview(icon);
            if (icon != null) icon.Dispose();
            UpdatePreviews();
        }

        void DefaultPersonalize()
        {
            if (!Ask("Rimetto i testi di esempio? (Il logo resta com'è. Niente viene salvato finché non premi \"Salva e applica\".)")) return;
            txtMotd1.Text = "&6&lServer di casa &r&7| &aSurvival";
            txtMotd2.Text = "&bEntra anche tu!";
            txtWelcome.Text = AppConfig.DefWelcome;
            txtLeave.Text = "";
            txtStartMsg.Text = "";
            txtPauseMotd.Text = AppConfig.DefPause;
            txtWakeMsg.Text = AppConfig.DefWake;
        }

        void ChooseIcon()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "Scegli il logo del server";
                d.Filter = "Immagini|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Tutti i file|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var src = McText.LoadImage(d.FileName))
                    {
                        DisposePending();
                        pendingIcon = McText.MakeIcon(src);
                    }
                    removeIcon = false;
                    SetIconPreview(pendingIcon);
                }
                catch (Exception ex) { Msg("Non riesco ad aprire questa immagine:\n" + ex.Message); }
            }
        }

        void SavePersonalize()
        {
            cfg.WelcomeMsg = txtWelcome.Text.Trim();
            cfg.LeaveMsg = txtLeave.Text.Trim();
            cfg.StartMsg = txtStartMsg.Text.Trim();
            cfg.PauseMotd = txtPauseMotd.Text.Trim().Length > 0 ? txtPauseMotd.Text.Trim() : AppConfig.DefPause;
            cfg.WakeMsg = txtWakeMsg.Text.Trim().Length > 0 ? txtWakeMsg.Text.Trim() : AppConfig.DefWake;
            cfg.Save();
            ApplyWakeTexts();

            if (!cfg.Installed)
            {
                Msg("Messaggi salvati. Logo e descrizione si potranno salvare dopo aver installato il server (scheda 1).");
                return;
            }
            try
            {
                McText.SetMotd(cfg.ServerDir, txtMotd1.Text, txtMotd2.Text);
                string ip = McText.IconPath(cfg.ServerDir);
                if (pendingIcon != null)
                {
                    pendingIcon.Save(ip, System.Drawing.Imaging.ImageFormat.Png);
                    DisposePending();
                }
                else if (removeIcon && File.Exists(ip)) File.Delete(ip);
                removeIcon = false;
            }
            catch (Exception ex)
            {
                Msg("Non salvato:\n" + ex.Message);
                return;
            }
            LoadProps();   // aggiorna anche la scheda Impostazioni
            if (server.IsRunning)
            {
                if (Ask("Salvato! I messaggi in chat valgono già da subito.\n\nPer far vedere logo e descrizione nuovi bisogna riavviare il server " +
                        "(circa un minuto, il mondo viene salvato). Lo riavvio adesso?"))
                    RestartServer();
            }
            else Msg(cur.Sleeping ? "Salvato! Il messaggio di pausa è già attivo; logo e descrizione si vedranno alla prossima accensione."
                              : "Salvato! Logo e descrizione si vedranno alla prossima accensione del server.");
        }

        void ApplyWakeTexts()
        {
            ApplyWakeTexts(cur);
        }

        static void ApplyWakeTexts(ServerSlot s)
        {
            s.Wake.PauseText = McText.ToSection(string.IsNullOrEmpty(s.Cfg.PauseMotd) ? AppConfig.DefPause : s.Cfg.PauseMotd);
            s.Wake.WakeText = McText.ToSection(string.IsNullOrEmpty(s.Cfg.WakeMsg) ? AppConfig.DefWake : s.Cfg.WakeMsg);
        }

        /// <summary>Un giocatore è entrato o uscito: manda i messaggi in chat scelti nella scheda Personalizza.</summary>
        void OnPlayerEvent(ServerSlot sl, string name, bool joined)
        {
            var c = sl.Cfg;
            string msg = joined ? c.WelcomeMsg : c.LeaveMsg;
            string startMsg = "";
            if (joined && sl.GreetStart)
            {
                sl.GreetStart = false;
                startMsg = c.StartMsg;
            }
            if (string.IsNullOrEmpty(msg) && string.IsNullOrEmpty(startMsg)) return;
            Task.Run(() =>
            {
                try
                {
                    System.Threading.Thread.Sleep(joined ? 1500 : 300);   // il giocatore deve essere già dentro
                    string max = Props.Get(c.ServerDir, "max-players") ?? "?";
                    string online = "?";
                    if ((msg + startMsg).Contains("{online}"))
                    {
                        int n = McProto.QueryPlayers(ServerTools.JavaPort(c), 3000);
                        if (n >= 0) online = n.ToString();
                    }
                    if (!string.IsNullOrEmpty(msg))
                        sl.Proc.SendCommand("tellraw " + (joined ? name : "@a") + " " + McText.ToComponent(McText.Fill(msg, name, online, max)));
                    if (!string.IsNullOrEmpty(startMsg))
                        sl.Proc.SendCommand("tellraw " + name + " " + McText.ToComponent(McText.Fill(startMsg, name, online, max)));
                }
                catch (Exception ex) { sl.Add("[Personalizza] " + ex.Message); }
            });
        }

        /// <summary>Chiamato a ogni avvio del server (anche dopo crash o riavvio programmato).</summary>
        void OnServerStarted(ServerSlot sl, int pid)
        {
            sl.GreetStart = true;
            ApplyPriority(sl, pid);
        }

        void ApplyPriority(ServerSlot sl, int pid)
        {
            prio.Forget(pid);
            string warn = PriorityManager.ApplyToServer(pid, gcfg.ServerPriority, gcfg.BoostEnabled);
            if (warn != null) sl.Add("[Prestazioni] " + warn);
            else sl.Add("[Prestazioni] Priorità del server: " + PriorityManager.CurrentClassName(pid) +
                        (gcfg.BoostEnabled ? "  +  disco e RAM prioritari" : ""));
        }

        /// <summary>Riapplica la priorità a tutti i server accesi.</summary>
        void ReapplyPriorityAll()
        {
            foreach (var sl in slots.ToList())
            {
                int pid = sl.Proc.Pid;
                if (pid > 0) ApplyPriority(sl, pid);
            }
        }

        bool IsServerPid(int pid)
        {
            foreach (var sl in slots) if (sl.Proc.Pid == pid) return true;
            return false;
        }

        void ToggleBoost()
        {
            gcfg.BoostEnabled = chkBoost.Checked;
            gcfg.Save();
            if (chkBoost.Checked)
            {
                prio.Enabled = true;
                if (!IsAdminNow)
                    Msg("Senza permessi di amministratore verranno abbassati solo i programmi del tuo utente, non i servizi di Windows.\n" +
                        "Per l'effetto completo premi 'Riavvia come amministratore'.");
                RunBoostPass(gcfg.BoostTrim);
                Log("[Prestazioni] Modalità server dedicato ATTIVA.");
            }
            else
            {
                prio.Enabled = false;
                lblBoost.Text = "Ripristino delle priorità originali...";
                Task.Run(() =>
                {
                    prio.RestoreAll();
                    UI(UpdateBoostLabel);
                });
                Log("[Prestazioni] Modalità server dedicato disattivata: priorità ripristinate.");
            }
            Task.Run(() => ReapplyPriorityAll());
        }

        void ReapplyBoost()
        {
            Task.Run(() =>
            {
                prio.RestoreAll();
                prio.Aggressive = gcfg.BoostAggressive;
                prio.ApplyAll(IsServerPid, false);
                UI(UpdateBoostLabel);
            });
        }

        void RunBoostPass(bool trim)
        {
            if (boostRunning || !prio.Enabled) return;
            boostRunning = true;
            prio.Aggressive = gcfg.BoostAggressive;
            Task.Run(() =>
            {
                try { prio.ApplyAll(IsServerPid, trim); }
                catch (Exception ex) { Log("[Prestazioni] Errore: " + ex.Message); }
                finally
                {
                    boostRunning = false;
                    UI(UpdateBoostLabel);
                }
            });
        }

        void UpdateBoostLabel()
        {
            if (lblBoost == null) return;
            if (gcfg.BoostEnabled)
            {
                lblBoost.Text = "Stato: ATTIVA   —   processi abbassati: " + prio.LoweredCount +
                                "   ·   non modificabili: " + prio.SkippedCount + (IsAdminNow ? "" : "  (senza permessi admin)");
                lblBoost.ForeColor = Color.DarkGreen;
            }
            else
            {
                lblBoost.Text = "Stato: disattivata";
                lblBoost.ForeColor = SystemColors.ControlText;
            }
        }

        void RelaunchAsAdmin()
        {
            var active = slots.Where(x => x.IsActive).ToList();
            bool wasRunning = active.Count > 0;
            if (wasRunning && !Ask("I server accesi verranno fermati (salvando il mondo) e riaccesi subito dopo con i permessi di amministratore. Continuare?")) return;
            Enabled = false;
            Text = "MineServer Manager - riavvio come amministratore...";
            Task.Run(() =>
            {
                foreach (var a in active) { a.Sleeping = false; a.Wake.Stop(); a.Proc.Stop(); }
                Updater.SetStartServerFlag(string.Join(",", active.Select(x => x.Cfg.Id.ToString())));
                UI(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo(Application.ExecutablePath, "--wait");
                        psi.Verb = "runas";
                        psi.UseShellExecute = true;
                        Process.Start(psi);
                    }
                    catch (Exception)
                    {
                        Enabled = true;
                        Text = "MineServer Manager";
                        Msg("Permessi di amministratore negati.");
                        Updater.SetStartServerFlag(null);
                        foreach (var a in active) StartSlot(a, true);
                        return;
                    }
                    reallyExit = true;
                    Close();
                });
            });
        }

        // ================================================================== tray, timer, chiusura

        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = Icon;
            tray.Text = "MineServer Manager";
            tray.Visible = true;
            var menu = new ContextMenuStrip();
            // il menu si ricostruisce ogni volta: una voce per server, con Avvia / Ferma
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                menu.Items.Add("Apri", null, (a, b) => ShowFromTray());
                menu.Items.Add(new ToolStripSeparator());
                foreach (var sl in slots.ToList())
                {
                    var x = sl;
                    var item = new ToolStripMenuItem(x.Cfg.DisplayName + "  (" + x.StateText().ToLowerInvariant() + ")");
                    item.DropDownItems.Add("Avvia", null, (a, b) => StartSlot(x, false)).Enabled = x.Cfg.Installed && !x.Proc.IsRunning;
                    item.DropDownItems.Add("Ferma", null, (a, b) => StopSlot(x)).Enabled = x.IsActive;
                    menu.Items.Add(item);
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Esci", null, (a, b) => { ShowFromTray(); Close(); });
                e.Cancel = false;
            };
            menu.Items.Add("Apri", null, (s, e) => ShowFromTray());
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowFromTray();
        }

        void ShowFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (tray != null && WindowState == FormWindowState.Minimized)
            {
                Hide();
                if (!trayTipShown && !autostartMode)
                {
                    tray.ShowBalloonTip(3000, "MineServer Manager", "Sono qui vicino all'orologio: il server continua a funzionare.", ToolTipIcon.Info);
                    trayTipShown = true;
                }
            }
        }

        void RefreshAll()
        {
            UpdateInstalledLabel();
            LoadProps();
            RefreshMods();
            RefreshBackups();
            RefreshIps();
            UpdateState();
            numRam.Value = Math.Max(2, Math.Min(14, cfg.RamGB));
        }

        void OnTabChanged()
        {
            var p = tabs.SelectedTab;
            Action a;
            if (p != null && layouts.TryGetValue(p, out a)) a();
            switch (tabs.SelectedIndex)
            {
                case 0: RefreshServerList(); UpdateAutostartHint(); break;
                case 3: if (grid.Rows.Count == 0) LoadProps(); break;
                case 4: RefreshMods(); break;
                case 5: RefreshBackups(); break;
                case 6: RefreshFriends(); RefreshIps(); break;
                case 8: UpdateBoostLabel(); break;
                case 7:
                    if (!personalizeLoaded) LoadPersonalize();
                    tabs.SelectedTab.Focus();                                   // niente cursore in una casella in basso
                    tabs.SelectedTab.AutoScrollPosition = new Point(0, 0);      // si parte sempre dall'alto
                    break;
            }
        }

        // ================================================================== modalità notte

        /// <summary>Cambia tema: "auto" (come Windows), "light", "dark". Si applica subito, senza riavviare.</summary>
        public void SetTheme(string mode)
        {
            gcfg.ThemeMode = mode;
            gcfg.Save();
            ApplyTheme();
        }

        void ApplyTheme()
        {
            bool dark = Theme.Resolve(gcfg.ThemeMode);
            if (dark || Theme.Dark) Theme.Apply(this, dark);   // all'avvio in chiaro non serve toccare niente
            if (btnTheme != null) btnTheme.Text = dark ? "Giorno" : "Notte";
            if (cbTheme != null)
            {
                themeLoading = true;
                cbTheme.SelectedIndex = gcfg.ThemeMode == "light" ? 1 : gcfg.ThemeMode == "dark" ? 2 : 0;
                themeLoading = false;
            }
            RefreshServerList();
        }

        void OnWindowsThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (gcfg.ThemeMode == "auto" && Theme.Resolve("auto") != Theme.Dark) UI(ApplyTheme);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyTheme();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnWindowsThemeChanged;
            RefreshAll();
            LayoutAll();
            LoadVersions();
            tabs.SelectedIndex = cfg.Installed ? 2 : (gcfg.Servers.Count > 1 ? 0 : 1);
            if (gcfg.BoostEnabled) { prio.Enabled = true; RunBoostPass(false); }
            if (gcfg.AutoPowerPlan) Task.Run(() => { RememberOriginalPlan(); ApplyPowerPlan(); });
            if (gcfg.PlayitEnabled && PlayitAgent.IsSetUp)
            {
                try { playit.Start(); } catch (Exception ex) { Log("[playit] " + ex.Message); }
            }
            RefreshFriends();
            Task.Run(() => Updater.CleanupOld());
            if (autostartMode)
            {
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutAll();
            if (autostartMode) Hide();
            string resume = Updater.ConsumeStartServerFlag();
            var toStart = new List<ServerSlot>();
            if (resume == "*") toStart.Add(slots.FirstOrDefault(x => x.Cfg.Installed) ?? cur);   // da una versione vecchia
            else if (resume != null)
            {
                foreach (var part in resume.Split(','))
                {
                    int id;
                    var sl = int.TryParse(part, out id) ? slots.FirstOrDefault(x => x.Cfg.Id == id) : null;
                    if (sl != null) toStart.Add(sl);
                }
            }
            else if (autostartMode) toStart.AddRange(slots.Where(x => x.Cfg.AutoStart && x.Cfg.Installed));
            else if (startServerFlag) toStart.Add(cur);
            QueueStart(toStart);
            if (Updater.JustUpdated)
            {
                lblUpdate.Text = "Aggiornato alla versione " + AppInfo.Version + ".";
                tray.ShowBalloonTip(4000, "MineServer Manager", "Aggiornato alla versione " + AppInfo.Version, ToolTipIcon.Info);
            }
            else if (gcfg.CheckUpdates)
            {
                var t = new Timer();
                t.Interval = 15000;
                t.Tick += (s, ev) => { t.Stop(); t.Dispose(); CheckUpdates(true); };
                t.Start();
            }
        }

        void OnTick()
        {
            FlushLog();
            tickCount++;
            if (tickCount % 4 != 0) return;
            UpdateStats();
            if (gcfg.BoostEnabled && tickCount % 120 == 0) RunBoostPass(false);
            if (tickCount % 240 == 0) foreach (var sl in slots.ToList()) CheckIdle(sl);
            if (tickCount % 480 == 0) CheckPowerPlan();
            ProcessStartQueue();
            string hm = DateTime.Now.ToString("HH:mm");
            if (hm == lastMinute) return;
            lastMinute = hm;
            foreach (var sl in slots.ToList()) Schedule(sl, hm);
        }

        static string MinusMinutes(string hhmm, int min)
        {
            var t = TimeOf(hhmm).AddMinutes(-min);
            return t.ToString("HH:mm");
        }

        void Schedule(ServerSlot sl, string hm)
        {
            var c = sl.Cfg;
            if (!c.Installed) return;
            if (c.BackupEnabled && hm == c.BackupTime) RunBackup(sl, true);
            if (c.DailyRestart && sl.Proc.IsRunning)
            {
                if (hm == MinusMinutes(c.RestartTime, 5)) sl.Proc.SendCommand("say Il server si riavvierà tra 5 minuti.");
                if (hm == MinusMinutes(c.RestartTime, 1)) sl.Proc.SendCommand("say Il server si riavvierà tra 1 minuto!");
                if (hm == c.RestartTime && !sl.BackupRunning)
                {
                    sl.Add("[Manager] Riavvio programmato.");
                    RestartSlot(sl);
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            var running = slots.Where(x => x.Proc.IsRunning).ToList();
            if (!reallyExit && running.Count > 0)
            {
                if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
                {
                    // Spegnimento di Windows: salva i mondi il più in fretta possibile (tutti insieme)
                    foreach (var r in running) { r.Proc.StopRequested = true; r.Proc.SendCommand("stop"); }
                    var sw = Stopwatch.StartNew();
                    foreach (var r in running) r.Proc.WaitForExit(Math.Max(1000, 25000 - (int)sw.ElapsedMilliseconds));
                }
                else
                {
                    string what = running.Count == 1 ? "Il server '" + running[0].Cfg.DisplayName + "' è acceso." : running.Count + " server sono accesi.";
                    var r = MessageBox.Show(this,
                        what + "\n\nSì = ferma i server (salvando i mondi) ed esci\n" +
                        "No = nascondi la finestra, i server restano accesi\nAnnulla = torna indietro",
                        "MineServer Manager", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    e.Cancel = true;
                    if (r == DialogResult.No) { WindowState = FormWindowState.Minimized; return; }
                    if (r == DialogResult.Cancel) return;
                    Enabled = false;
                    Text = "MineServer Manager - arresto dei server in corso...";
                    Task.Run(() =>
                    {
                        foreach (var x in running) x.Proc.Stop();
                        UI(() => { reallyExit = true; Close(); });
                    });
                    return;
                }
            }
            timer.Stop();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnWindowsThemeChanged;
            foreach (var sl in slots)
            {
                sl.Sleeping = false;
                try { sl.Wake.Stop(); } catch { }
            }
            try { playit.Stop(); } catch { }
            if (gcfg.AutoPowerPlan) { try { RestoreOriginalPlan(); } catch { } }
            if (e.CloseReason != CloseReason.WindowsShutDown && prio.LoweredCount > 0)
            {
                prio.Enabled = false;
                try { prio.RestoreAll(); } catch { }
            }
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }
    }
}
