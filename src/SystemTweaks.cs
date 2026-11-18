using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MineServer
{
    public static class SystemTweaks
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "MineServerManager";

        // ------------------------------------------------------------ avvio automatico

        static bool RunKeyExists()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        public static bool TaskExists()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + AppName + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        public static bool IsAutostart()
        {
            return RunKeyExists() || TaskExists();
        }

        /// <summary>
        /// Senza admin: chiave Run dell'utente. Con admin: attività pianificata "con i privilegi più elevati"
        /// (Windows non avvia programmi elevati dalla chiave Run). Ritorna un errore o null.
        /// </summary>
        public static string SetAutostart(bool on, bool admin)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on && !admin) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\" --autostart");
                    else k.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex) { return ex.Message; }

            if (on && admin) return CreateAdminTask();
            if (TaskExists())
            {
                string err = RunElevatedBat("schtasks /Delete /TN \"" + AppName + "\" /F >nul 2>&1\r\n");
                if (err != null) return err;
            }
            return null;
        }

        static string CreateAdminTask()
        {
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string exe = SecurityElement.Escape(Application.ExecutablePath);
            string dir = SecurityElement.Escape(Path.GetDirectoryName(Application.ExecutablePath));
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Avvia MineServer Manager con i permessi di amministratore</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>false</AllowHardTerminate>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>4</Priority>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + exe + "</Command><Arguments>--autostart</Arguments><WorkingDirectory>" + dir + "</WorkingDirectory></Exec></Actions>\r\n" +
                "</Task>\r\n";
            string xmlPath = Path.Combine(Path.GetTempPath(), "mineserver_task.xml");
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            string err = RunElevatedBat("schtasks /Create /TN \"" + AppName + "\" /XML \"" + xmlPath + "\" /F\r\n");
            if (err != null) return err;
            return TaskExists() ? null : "Attività pianificata non creata.";
        }

        // ------------------------------------------------------------ ottimizzazioni

        /// <summary>Esegue un .bat come amministratore (un solo prompt UAC, nessuno se siamo già admin).</summary>
        public static string RunElevatedBat(string body)
        {
            string bat = Path.Combine(Path.GetTempPath(), "mineserver_admin.bat");
            File.WriteAllText(bat, "@echo off\r\n" + body, Encoding.Default);
            try
            {
                var psi = new ProcessStartInfo(bat);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (var p = Process.Start(psi)) p.WaitForExit();
                return null;
            }
            catch (Win32Exception) { return "Permessi di amministratore negati."; }
            catch (Exception ex) { return ex.Message; }
        }

        public static string ApplyElevated(bool power, bool firewall, int port)
        {
            var sb = new StringBuilder();
            if (power)
            {
                sb.AppendLine("powercfg -setactive SCHEME_MIN");
                sb.AppendLine("powercfg -change -standby-timeout-ac 0");
                sb.AppendLine("powercfg -change -hibernate-timeout-ac 0");
                sb.AppendLine("powercfg -change -disk-timeout-ac 0");
                sb.AppendLine("powercfg -change -monitor-timeout-ac 10");
                sb.AppendLine("powercfg -h off");
            }
            if (firewall)
            {
                foreach (var n in new[] { "Minecraft Server TCP", "Minecraft Server UDP", "Minecraft Bedrock (Geyser)" })
                    sb.AppendLine("netsh advfirewall firewall delete rule name=\"" + n + "\" >nul 2>&1");
                sb.AppendLine("netsh advfirewall firewall add rule name=\"Minecraft Server TCP\" dir=in action=allow protocol=TCP localport=" + port);
                sb.AppendLine("netsh advfirewall firewall add rule name=\"Minecraft Server UDP\" dir=in action=allow protocol=UDP localport=" + port);
                sb.AppendLine("netsh advfirewall firewall add rule name=\"Minecraft Bedrock (Geyser)\" dir=in action=allow protocol=UDP localport=19132");
            }
            return RunElevatedBat(sb.ToString());
        }

        // ------------------------------------------------------------ rete

        public static List<string> LocalIPs()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    // schede virtuali (Hyper-V, VirtualBox, VPN...): non servono agli amici per collegarsi
                    string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    if (desc.Contains("vethernet") || desc.Contains("virtual") || desc.Contains("hyper-v") ||
                        desc.Contains("vmware") || desc.Contains("tap-") || desc.Contains("wsl")) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            list.Add(ua.Address + "   (" + ni.Name + ")");
                }
            }
            catch { }
            return list;
        }
    }
}
