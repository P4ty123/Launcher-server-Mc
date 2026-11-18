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
        readonly AppConfig cfg;
        readonly ServerProcess server;
        readonly bool autostartMode, startServerFlag;
        readonly PriorityManager prio = new PriorityManager();
        volatile bool boostRunning;
        bool suppress;
        bool reallyExit, busy, backupRunning, trayTipShown;
        int tickCount, loadToken;
        string lastMinute = "";
        readonly ConcurrentQueue<string> logQueue = new ConcurrentQueue<string>();
        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);
        readonly Dictionary<TabPage, Action> layouts = new Dictionary<TabPage, Action>();
        Timer timer;
        NotifyIcon tray;
        TabControl tabs;

        // Installa
        ComboBox cbType, cbVersion;
        TextBox txtDir, txtInstallLog;
        CheckBox chkOptim, chkGeyser, chkEula;
        Button btnInstall, btnRefreshVers;
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
        CheckBox chkPower, chkFirewall, chkAutostartWin, chkStartServer;
        Label lblIps, lblPublicIp;
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

        static readonly string[] TypeLabels =
        {
            "Vanilla  —  server ufficiale Mojang, nessuna modifica",
            "Paper  —  vanilla ottimizzato + plugin  (consigliato per il mini PC)",
            "Fabric  —  mod, ottime mod di performance",
            "Forge  —  mod classiche e modpack"
        };

        static readonly Dictionary<string, string> PropHelp = new Dictionary<string, string>
        {
            { "motd", "Messaggio mostrato nella lista server" },
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
            cfg = AppConfig.Load();
            server = new ServerProcess(cfg);
            server.Output += line => logQueue.Enqueue(line);
            server.StateChanged += () => UI(UpdateState);
            server.Started += pid => OnServerStarted(pid);

            Text = "MineServer Manager " + AppInfo.Version;
            Size = new Size(960, 720);
            MinimumSize = new Size(880, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            Icon = AppIcon();

            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            Controls.Add(tabs);

            BuildInstallTab();
            BuildConsoleTab();
            BuildSettingsTab();
            BuildModsTab();
            BuildBackupTab();
            BuildSystemTab();
            BuildPerfTab();
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

        static string DefaultDir(string type)
        {
            return Path.Combine(AppConfig.BaseDir, "server-" + type.ToLower());
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
            chkGeyser.Enabled = t == "Paper" || t == "Fabric";
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
                    ? "In questa cartella c'è già un server " + t + ".\nLo aggiorno a " + ver + " mantenendo il mondo?\n\n(Consiglio: fai prima un backup dalla scheda Backup.)"
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
                    RefreshAll();
                    Msg("Server installato!\n\nVai nella scheda Console e premi Avvia.\nIl primo avvio è più lento perché genera il mondo.");
                    tabs.SelectedIndex = 1;
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

            chkAutoRestart = Chk(p, "Riavvia da solo se crasha", 12, 46, cfg.AutoRestart);
            chkAutoRestart.CheckedChanged += (s, e) => { cfg.AutoRestart = chkAutoRestart.Checked; cfg.Save(); };

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.FromArgb(24, 24, 24);
            txtLog.ForeColor = Color.Gainsboro;
            txtLog.Font = new Font("Consolas", 9F);
            Put(p, txtLog, 12, 74, 800, 300);

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
            var bStopGen = Btn(p, "Ferma pre-generazione", 594, 440, 160, (s, e) => QuickCmd("chunky cancel"));

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width, h = p.ClientSize.Height;
                if (w < 200 || h < 200) return;
                txtLog.Size = new Size(w - 24, h - 74 - 90);
                int y1 = h - 80;
                txtCmd.Location = new Point(12, y1 + 3);
                txtCmd.Width = w - 24 - 120;
                btnSend.Location = new Point(w - 12 - 110, y1);
                int y2 = h - 40;
                bList.Top = y2; bSave.Top = y2; bGen.Top = y2; bStopGen.Top = y2;
                lblR.Top = y2 + 6;
                numRadius.Top = y2 + 3;
            });
        }

        void StartServer()
        {
            if (!cfg.Installed) { Msg("Prima installa il server (scheda 1)."); return; }
            if (server.IsRunning) return;
            try { server.Start(); }
            catch (Exception ex) { Msg("Impossibile avviare il server:\n" + ex.Message); }
            UpdateState();
        }

        void StopServer()
        {
            if (!server.IsRunning) return;
            lblState.Text = "● Arresto in corso...";
            lblState.ForeColor = Color.DarkOrange;
            Task.Run(() => server.Stop());
        }

        void RestartServer()
        {
            if (!cfg.Installed) return;
            lblState.Text = "● Riavvio in corso...";
            lblState.ForeColor = Color.DarkOrange;
            Task.Run(() =>
            {
                try { server.Restart(); }
                catch (Exception ex) { logQueue.Enqueue("[Manager] Errore nel riavvio: " + ex.Message); }
            });
        }

        void SendCmd()
        {
            string c = txtCmd.Text.Trim();
            if (c == "") return;
            if (c.StartsWith("/")) c = c.Substring(1);
            if (server.SendCommand(c)) logQueue.Enqueue("> " + c);
            else logQueue.Enqueue("[Manager] Il server non è acceso.");
            txtCmd.Clear();
        }

        void QuickCmd(string c)
        {
            if (server.SendCommand(c)) logQueue.Enqueue("> " + c);
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
            logQueue.Enqueue("[Manager] Pre-generazione avviata (raggio " + r + " blocchi). Sull'N95 può richiedere qualche ora: puoi giocare intanto, ma è meglio lasciarlo lavorare da solo.");
        }

        void UpdateState()
        {
            bool r = server.IsRunning;
            if (!(lblState.Text.Contains("in corso") && r))
            {
                lblState.Text = r ? "● Acceso" : "● Fermo";
                lblState.ForeColor = r ? Color.ForestGreen : Color.Firebrick;
            }
            btnStart.Enabled = !r && cfg.Installed;
            btnStop.Enabled = r;
            btnRestart.Enabled = cfg.Installed;
            if (tray != null) tray.Text = r ? "MineServer Manager - server acceso" : "MineServer Manager - server fermo";
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
            }
            else lblStats.Text = "";
            if (lblPrioNow != null)
                lblPrioNow.Text = server.IsRunning ? "Priorità attuale: " + PriorityManager.CurrentClassName(server.Pid) : "(server spento)";
            UpdateState();
        }

        void FlushLog()
        {
            if (logQueue.IsEmpty) return;
            var sb = new StringBuilder();
            string line;
            int n = 0;
            while (n < 400 && logQueue.TryDequeue(out line))
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
            numRam.ValueChanged += (s, e) => { cfg.RamGB = (int)numRam.Value; cfg.Save(); };

            lblPropsInfo = Lbl(p, "Impostazioni del mondo (server.properties). Clicca su un valore per cambiarlo, poi Salva e riavvia il server.", 12, 46);

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
            var bPreset = Btn(p, "Preset leggero per mini PC", 224, 0, 200, (s, e) => ApplyPreset());
            var bOpen = Btn(p, "Apri cartella server", 430, 0, 160, (s, e) => OpenFolder(cfg.ServerDir));

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width, h = p.ClientSize.Height;
                if (w < 200 || h < 200) return;
                grid.Size = new Size(w - 24, h - 72 - 50);
                int y = h - 40;
                bSave.Top = y; bReload.Top = y; bPreset.Top = y; bOpen.Top = y;
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
                if (dict.ContainsKey(key)) grid.Rows.Add(key, dict[key], PropHelp[key]);
            foreach (var kv in props.OrderBy(x => x.Key))
                if (!PropHelp.ContainsKey(kv.Key)) grid.Rows.Add(kv.Key, kv.Value, "");
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
            if (server.IsRunning) logQueue.Enqueue("[Mod] Nota: le nuove mod/plugin funzioneranno dopo il riavvio del server.");
            string t = cfg.ServerType, v = cfg.McVersion, dir = cfg.ServerDir;
            lblAddon.Text = "Installo " + what + "...";
            Task.Run(() =>
            {
                var inst = new Installer();
                var lines = new List<string>();
                inst.Log = s => { lock (lines) lines.Add(s); logQueue.Enqueue("[Mod] " + s); };
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

        void BuildBackupTab()
        {
            var p = NewTab("5. Backup");
            chkBackup = Chk(p, "Backup automatico ogni giorno alle", 12, 16, cfg.BackupEnabled);
            dtBackup = Put(p, TimePicker(TimeOf(cfg.BackupTime)), 260, 14, 70, 0);
            Lbl(p, "conserva gli ultimi", 345, 14);
            numKeep = new NumericUpDown();
            numKeep.Minimum = 1;
            numKeep.Maximum = 60;
            numKeep.Value = Math.Max(1, Math.Min(60, cfg.BackupKeep));
            Put(p, numKeep, 460, 15, 55, 0);
            Lbl(p, "backup", 522, 14);

            chkRestart = Chk(p, "Riavvio automatico ogni giorno alle", 12, 52, cfg.DailyRestart);
            dtRestart = Put(p, TimePicker(TimeOf(cfg.RestartTime)), 260, 50, 70, 0);
            Lbl(p, "(i giocatori vengono avvisati 5 minuti prima; libera la RAM)", 345, 50);

            EventHandler save = (s, e) =>
            {
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

            Btn(p, "Fai un backup adesso", 12, 92, 170, (s, e) => RunBackup(false));
            Btn(p, "Ripristina selezionato", 188, 92, 170, (s, e) => RestoreBackup());
            Btn(p, "Apri cartella backup", 364, 92, 160, (s, e) => OpenFolder(cfg.BackupDir));

            Lbl(p, "Backup salvati (cartella 'backup' accanto al programma):", 12, 134);
            lbBackups = new ListBox();
            lbBackups.IntegralHeight = false;
            Put(p, lbBackups, 12, 158, 800, 300);

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width, h = p.ClientSize.Height;
                if (w < 200 || h < 200) return;
                lbBackups.Size = new Size(w - 24, h - 158 - 12);
            });
        }

        void RefreshBackups()
        {
            lbBackups.Items.Clear();
            foreach (var f in Backup.List(cfg))
            {
                long mb = new FileInfo(f).Length / 1048576;
                lbBackups.Items.Add(Path.GetFileName(f) + "   (" + mb + " MB)");
            }
        }

        void RunBackup(bool auto)
        {
            if (backupRunning) { if (!auto) Msg("C'è già un backup in corso."); return; }
            if (!cfg.Installed) { if (!auto) Msg("Prima installa il server."); return; }
            backupRunning = true;
            logQueue.Enqueue("[Backup] Avvio backup...");
            Task.Run(() =>
            {
                try
                {
                    string path = Backup.Run(cfg, server, s => logQueue.Enqueue("[Backup] " + s));
                    long mb = new FileInfo(path).Length / 1048576;
                    logQueue.Enqueue("[Backup] Completato: " + Path.GetFileName(path) + " (" + mb + " MB)");
                    if (!auto) UI(() => Msg("Backup completato: " + Path.GetFileName(path)));
                }
                catch (Exception ex)
                {
                    logQueue.Enqueue("[Backup] Errore: " + ex.Message);
                    if (!auto) UI(() => Msg("Backup non riuscito: " + ex.Message));
                }
                finally
                {
                    backupRunning = false;
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

        void BuildSystemTab()
        {
            var p = NewTab("6. Sistema");

            var g1 = new GroupBox();
            g1.Text = "Ottimizzazioni di Windows (ti chiederà i permessi di amministratore)";
            Put(p, g1, 12, 10, 800, 115);
            chkPower = Chk(g1, "Piano energetico 'Prestazioni elevate', niente sospensione né ibernazione", 15, 25, true);
            chkFirewall = Chk(g1, "Apri le porte del server nel firewall (Java 25565, Bedrock 19132)", 15, 50, true);
            Btn(g1, "Applica ottimizzazioni", 15, 76, 200, (s, e) => ApplyTweaks());

            var g2 = new GroupBox();
            g2.Text = "Avvio automatico";
            Put(p, g2, 12, 135, 800, 150);
            chkAutostartWin = Chk(g2, "Apri MineServer Manager all'accensione del PC (parte nascosto vicino all'orologio)", 15, 25, SystemTweaks.IsAutostart());
            chkStartServer = Chk(g2, "...e avvia subito il server", 35, 50, cfg.StartServerOnLaunch);
            chkAutostartAdmin = Chk(g2, "...come amministratore (serve per la scheda Prestazioni: priorità Alta/Tempo reale e modalità server dedicato)", 35, 75, cfg.AutostartAdmin);
            chkAutostartWin.CheckedChanged += (s, e) => ApplyAutostart();
            chkAutostartAdmin.CheckedChanged += (s, e) =>
            {
                cfg.AutostartAdmin = chkAutostartAdmin.Checked;
                cfg.Save();
                if (chkAutostartWin.Checked) ApplyAutostart();
            };
            chkStartServer.CheckedChanged += (s, e) => { cfg.StartServerOnLaunch = chkStartServer.Checked; cfg.Save(); };
            Btn(g2, "Accesso automatico a Windows...", 15, 108, 230, (s, e) => AutoLoginHelp());
            Lbl(g2, "Serve perché dopo un riavvio (es. aggiornamenti) il PC entri da solo in Windows.", 255, 110);

            var g3 = new GroupBox();
            g3.Text = "Come si collegano i giocatori";
            Put(p, g3, 12, 295, 800, 200);
            lblIps = Lbl(g3, "", 15, 22);
            Btn(g3, "Mostra IP pubblico", 15, 110, 160, (s, e) => ShowPublicIp());
            lblPublicIp = Lbl(g3, "", 185, 112);
            Lbl(g3, "Per giocare da fuori casa: sul router inoltra la porta 25565 (TCP) verso l'IP di casa qui sopra.\n" +
                    "Se non puoi toccare il router usa playit.gg (gratuito), che crea un indirizzo pubblico senza aprire porte.", 15, 145);

            var g4 = new GroupBox();
            g4.Text = "Aggiornamenti del programma";
            Put(p, g4, 12, 505, 800, 80);
            Lbl(g4, "Versione installata: " + AppInfo.Version, 15, 24);
            btnUpdate = Btn(g4, "Cerca aggiornamenti", 200, 20, 190, (s, e) => OnUpdateClick());
            chkAutoUpdate = Chk(g4, "Controlla da solo all'avvio", 400, 24, cfg.CheckUpdates);
            chkAutoUpdate.CheckedChanged += (s, e) => { cfg.CheckUpdates = chkAutoUpdate.Checked; cfg.Save(); };
            lblUpdate = Lbl(g4, "", 15, 52);
            var lnk = new LinkLabel();
            lnk.Text = "pagina del progetto su GitHub";
            lnk.AutoSize = true;
            lnk.LinkClicked += (s, e) => { try { Process.Start(AppInfo.RepoUrl + "/releases"); } catch { } };
            Put(g4, lnk, 600, 27, 0, 0);

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width;
                if (w < 200) return;
                g1.Width = w - 24; g2.Width = w - 24; g3.Width = w - 24; g4.Width = w - 24;
            });
        }

        int ServerPort()
        {
            int port;
            string v = cfg.Installed ? Props.Get(cfg.ServerDir, "server-port") : null;
            return int.TryParse(v, out port) ? port : 25565;
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

        void ShowPublicIp()
        {
            lblPublicIp.Text = "...";
            Task.Run(() =>
            {
                string ip;
                try { ip = Net.GetString("https://api.ipify.org").Trim(); }
                catch (Exception ex) { ip = "errore: " + ex.Message; }
                UI(() => lblPublicIp.Text = "Da fuori casa (dopo aver aperto la porta sul router):  " + ip + ":" + ServerPort());
            });
        }

        void ApplyTweaks()
        {
            if (!chkPower.Checked && !chkFirewall.Checked) return;
            bool power = chkPower.Checked, fw = chkFirewall.Checked;
            int port = ServerPort();
            Task.Run(() =>
            {
                string err = SystemTweaks.ApplyElevated(power, fw, port);
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
            string[] names = { "1-installa", "2-console", "3-impostazioni", "4-mod-plugin", "5-backup", "6-sistema", "7-prestazioni" };
            for (int i = 0; i < tabs.TabCount && i < names.Length; i++)
            {
                tabs.SelectedIndex = i;
                if (i == 3 && ModsUsable())
                {
                    txtSearch.Text = "chunky";
                    DoSearch();
                }
                Pump(3000);
                Capture(Path.Combine(outDir, names[i] + ".png"));
            }
            if (server.IsRunning) server.Stop();
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
                                                  ". Aggiorna dalla scheda Sistema.", ToolTipIcon.Info);
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
            if (server.IsRunning) q += "\n\nIl server verrà fermato (salvando il mondo) e riacceso da solo dopo l'aggiornamento.";
            if (Ask(q)) DoUpdate(u);
        }

        void DoUpdate(UpdateInfo u)
        {
            updateBusy = true;
            btnUpdate.Enabled = false;
            bool wasRunning = server.IsRunning;
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
                        logQueue.Enqueue("[Aggiornamento] Fermo il server prima di aggiornare...");
                        server.Stop();
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
                    Updater.SetStartServerFlag(wasRunning);
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
                        Updater.SetStartServerFlag(false);
                        lblUpdate.Text = "Aggiornamento non riuscito.";
                        Msg("Aggiornamento non riuscito:\n" + err);
                        if (wasRunning && !server.IsRunning) StartServer();
                        return;
                    }
                    lblUpdate.Text = "Riavvio con la nuova versione...";
                    reallyExit = true;
                    Close();
                });
            });
        }

        // ================================================================== 7. PRESTAZIONI

        static readonly bool IsAdminNow = PriorityManager.IsAdmin();

        void BuildPerfTab()
        {
            var p = NewTab("7. Prestazioni");

            var g0 = new GroupBox();
            g0.Text = "Permessi";
            Put(p, g0, 12, 10, 800, 88);
            lblAdmin = Lbl(g0, IsAdminNow
                ? "✔ Il programma è in esecuzione come amministratore: tutte le funzioni sono disponibili."
                : "✖ Il programma NON è amministratore: 'Tempo reale' diventa 'Alta' e i servizi di Windows non si possono abbassare.", 15, 22);
            lblAdmin.ForeColor = IsAdminNow ? Color.DarkGreen : Color.DarkOrange;
            btnElevate = Btn(g0, "Riavvia come amministratore", 15, 50, 210, (s, e) => RelaunchAsAdmin());
            btnElevate.Visible = !IsAdminNow;
            if (!IsAdminNow)
                Lbl(g0, "Per averlo sempre: scheda 6 > avvio automatico 'come amministratore'.", 235, 52);

            var g1 = new GroupBox();
            g1.Text = "Priorità CPU del server Minecraft";
            Put(p, g1, 12, 108, 800, 92);
            Lbl(g1, "Priorità:", 15, 26);
            cbPrio = new ComboBox();
            cbPrio.DropDownStyle = ComboBoxStyle.DropDownList;
            cbPrio.Items.AddRange(PriorityManager.ServerLabels);
            Put(g1, cbPrio, 85, 26, 220, 0);
            int pi = Array.IndexOf(PriorityManager.ServerKeys, cfg.ServerPriority);
            cbPrio.SelectedIndex = pi < 0 ? 1 : pi;
            cbPrio.SelectedIndexChanged += (s, e) => OnPrioChanged();
            lblPrioNow = Lbl(g1, "", 320, 26);
            Lbl(g1, "'Alta' è il miglior compromesso. 'Tempo reale' può bloccare mouse, tastiera e desktop remoto quando il server è sotto sforzo.", 15, 58);

            var g2 = new GroupBox();
            g2.Text = "Modalità server dedicato";
            Put(p, g2, 12, 210, 800, 240);
            chkBoost = Chk(g2, "ATTIVA: abbassa CPU, disco e RAM di tutti gli altri processi, la precedenza va solo al server", 15, 26, cfg.BoostEnabled);
            chkBoost.Font = new Font(Font, FontStyle.Bold);
            Lbl(g2, "Intensità:", 15, 60);
            cbLevel = new ComboBox();
            cbLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            cbLevel.Items.Add("Moderata  —  gli altri programmi restano usabili");
            cbLevel.Items.Add("Massima  —  gli altri processi lavorano solo quando la CPU è libera");
            Put(g2, cbLevel, 85, 60, 460, 0);
            cbLevel.SelectedIndex = cfg.BoostAggressive ? 1 : 0;
            chkTrim = Chk(g2, "Quando la attivi, libera anche la RAM occupata dagli altri programmi", 15, 94, cfg.BoostTrim);
            lblBoost = Lbl(g2, "", 15, 122);
            lblBoost.Font = new Font(Font, FontStyle.Bold);
            Lbl(g2, "Cosa fa:\n" +
                    "•  server: priorità disco alta e priorità RAM massima (le sue pagine restano in memoria per prime)\n" +
                    "•  tutti gli altri: CPU 'sotto il normale' (Massima: 'bassa'), disco basso, RAM a bassa priorità\n" +
                    "•  si riapplica da sola ogni 30 secondi ai programmi nuovi; spegnendola tutto torna com'era\n" +
                    "•  non tocca i processi vitali di Windows né Gestione attività, così puoi sempre intervenire", 15, 150);

            chkBoost.CheckedChanged += (s, e) => ToggleBoost();
            cbLevel.SelectedIndexChanged += (s, e) =>
            {
                cfg.BoostAggressive = cbLevel.SelectedIndex == 1;
                cfg.Save();
                if (cfg.BoostEnabled) ReapplyBoost();
            };
            chkTrim.CheckedChanged += (s, e) => { cfg.BoostTrim = chkTrim.Checked; cfg.Save(); };

            RegisterLayout(p, () =>
            {
                int w = p.ClientSize.Width;
                if (w < 200) return;
                g0.Width = w - 24; g1.Width = w - 24; g2.Width = w - 24;
            });
        }

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
                    int i = Array.IndexOf(PriorityManager.ServerKeys, cfg.ServerPriority);
                    cbPrio.SelectedIndex = i < 0 ? 1 : i;
                    suppress = false;
                    return;
                }
                if (!IsAdminNow) Msg("Senza permessi di amministratore Windows trasforma 'Tempo reale' in 'Alta'.\nPremi 'Riavvia come amministratore' in questa scheda.");
            }
            cfg.ServerPriority = key;
            cfg.Save();
            int pid = server.Pid;
            if (pid > 0) Task.Run(() => OnServerStarted(pid));
        }

        /// <summary>Chiamato a ogni avvio del server (anche dopo crash o riavvio programmato).</summary>
        void OnServerStarted(int pid)
        {
            prio.Forget(pid);
            string warn = PriorityManager.ApplyToServer(pid, cfg.ServerPriority, cfg.BoostEnabled);
            if (warn != null) logQueue.Enqueue("[Prestazioni] " + warn);
            else logQueue.Enqueue("[Prestazioni] Priorità del server: " + PriorityManager.CurrentClassName(pid) +
                                  (cfg.BoostEnabled ? "  +  disco e RAM prioritari" : ""));
        }

        void ToggleBoost()
        {
            cfg.BoostEnabled = chkBoost.Checked;
            cfg.Save();
            int spid = server.Pid;
            if (chkBoost.Checked)
            {
                prio.Enabled = true;
                if (!IsAdminNow)
                    Msg("Senza permessi di amministratore verranno abbassati solo i programmi del tuo utente, non i servizi di Windows.\n" +
                        "Per l'effetto completo premi 'Riavvia come amministratore'.");
                RunBoostPass(cfg.BoostTrim);
                logQueue.Enqueue("[Prestazioni] Modalità server dedicato ATTIVA.");
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
                logQueue.Enqueue("[Prestazioni] Modalità server dedicato disattivata: priorità ripristinate.");
            }
            if (spid > 0) Task.Run(() => OnServerStarted(spid));
        }

        void ReapplyBoost()
        {
            Task.Run(() =>
            {
                prio.RestoreAll();
                prio.Aggressive = cfg.BoostAggressive;
                prio.ApplyAll(() => server.Pid, false);
                UI(UpdateBoostLabel);
            });
        }

        void RunBoostPass(bool trim)
        {
            if (boostRunning || !prio.Enabled) return;
            boostRunning = true;
            prio.Aggressive = cfg.BoostAggressive;
            Task.Run(() =>
            {
                try { prio.ApplyAll(() => server.Pid, trim); }
                catch (Exception ex) { logQueue.Enqueue("[Prestazioni] Errore: " + ex.Message); }
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
            if (cfg.BoostEnabled)
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
            bool wasRunning = server.IsRunning;
            if (wasRunning && !Ask("Il server verrà fermato (salvando il mondo) e riavviato subito dopo con i permessi di amministratore. Continuare?")) return;
            Enabled = false;
            Text = "MineServer Manager - riavvio come amministratore...";
            Task.Run(() =>
            {
                server.Stop();
                UI(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo(Application.ExecutablePath, "--wait" + (wasRunning ? " --startserver" : ""));
                        psi.Verb = "runas";
                        psi.UseShellExecute = true;
                        Process.Start(psi);
                    }
                    catch (Exception)
                    {
                        Enabled = true;
                        Text = "MineServer Manager";
                        Msg("Permessi di amministratore negati.");
                        if (wasRunning) StartServer();
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
            menu.Items.Add("Apri", null, (s, e) => ShowFromTray());
            menu.Items.Add("Avvia server", null, (s, e) => StartServer());
            menu.Items.Add("Ferma server", null, (s, e) => StopServer());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Esci", null, (s, e) => { ShowFromTray(); Close(); });
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
                case 2: if (grid.Rows.Count == 0) LoadProps(); break;
                case 3: RefreshMods(); break;
                case 4: RefreshBackups(); break;
                case 5: RefreshIps(); break;
                case 6: UpdateBoostLabel(); break;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            RefreshAll();
            LayoutAll();
            LoadVersions();
            if (cfg.Installed) tabs.SelectedIndex = 1;
            if (cfg.BoostEnabled) { prio.Enabled = true; RunBoostPass(false); }
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
            if (autostartMode)
            {
                Hide();
                if (cfg.StartServerOnLaunch && cfg.Installed) StartServer();
            }
            else if (startServerFlag && cfg.Installed) StartServer();

            if (Updater.ConsumeStartServerFlag() && cfg.Installed && !server.IsRunning) StartServer();
            if (Updater.JustUpdated)
            {
                lblUpdate.Text = "Aggiornato alla versione " + AppInfo.Version + ".";
                tray.ShowBalloonTip(4000, "MineServer Manager", "Aggiornato alla versione " + AppInfo.Version, ToolTipIcon.Info);
            }
            else if (cfg.CheckUpdates)
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
            if (cfg.BoostEnabled && tickCount % 120 == 0) RunBoostPass(false);
            string hm = DateTime.Now.ToString("HH:mm");
            if (hm == lastMinute) return;
            lastMinute = hm;
            Schedule(hm);
        }

        static string MinusMinutes(string hhmm, int min)
        {
            var t = TimeOf(hhmm).AddMinutes(-min);
            return t.ToString("HH:mm");
        }

        void Schedule(string hm)
        {
            if (!cfg.Installed) return;
            if (cfg.BackupEnabled && hm == cfg.BackupTime) RunBackup(true);
            if (cfg.DailyRestart && server.IsRunning)
            {
                if (hm == MinusMinutes(cfg.RestartTime, 5)) server.SendCommand("say Il server si riavvierà tra 5 minuti.");
                if (hm == MinusMinutes(cfg.RestartTime, 1)) server.SendCommand("say Il server si riavvierà tra 1 minuto!");
                if (hm == cfg.RestartTime && !backupRunning)
                {
                    logQueue.Enqueue("[Manager] Riavvio programmato.");
                    RestartServer();
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && server.IsRunning)
            {
                if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
                {
                    // Spegnimento di Windows: salva il mondo il più in fretta possibile
                    server.StopRequested = true;
                    server.SendCommand("stop");
                    server.WaitForExit(25000);
                }
                else
                {
                    var r = MessageBox.Show(this,
                        "Il server è acceso.\n\nSì = ferma il server (salvando il mondo) ed esci\n" +
                        "No = nascondi la finestra, il server resta acceso\nAnnulla = torna indietro",
                        "MineServer Manager", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    e.Cancel = true;
                    if (r == DialogResult.No) { WindowState = FormWindowState.Minimized; return; }
                    if (r == DialogResult.Cancel) return;
                    Enabled = false;
                    Text = "MineServer Manager - arresto del server in corso...";
                    Task.Run(() =>
                    {
                        server.Stop();
                        UI(() => { reallyExit = true; Close(); });
                    });
                    return;
                }
            }
            timer.Stop();
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
