using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MineServer
{
    /// <summary>Il minimo del protocollo di Minecraft Java che serve per sapere chi è online e per rispondere a server spento.</summary>
    public static class McProto
    {
        public static int ReadVarInt(Stream s)
        {
            int value = 0, shift = 0;
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
                shift += 7;
                if (shift > 28) throw new IOException("VarInt troppo lungo");
            }
        }

        public static void WriteVarInt(Stream s, int v)
        {
            uint u = (uint)v;
            while (true)
            {
                if ((u & ~0x7Fu) == 0) { s.WriteByte((byte)u); return; }
                s.WriteByte((byte)((u & 0x7F) | 0x80));
                u >>= 7;
            }
        }

        static byte[] ReadExact(Stream s, int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) throw new EndOfStreamException();
                off += r;
            }
            return buf;
        }

        public static string ReadString(Stream s, int max)
        {
            int len = ReadVarInt(s);
            if (len < 0 || len > max) throw new IOException("Stringa non valida");
            return Encoding.UTF8.GetString(ReadExact(s, len));
        }

        public static void WriteString(Stream s, string v)
        {
            var b = Encoding.UTF8.GetBytes(v ?? "");
            WriteVarInt(s, b.Length);
            s.Write(b, 0, b.Length);
        }

        /// <summary>Legge un pacchetto intero e restituisce id e contenuto.</summary>
        public static MemoryStream ReadPacket(Stream s, int maxLen, out int id)
        {
            int len = ReadVarInt(s);
            if (len <= 0 || len > maxLen) throw new IOException("Pacchetto non valido");
            var ms = new MemoryStream(ReadExact(s, len));
            id = ReadVarInt(ms);
            return ms;
        }

        public static void SendPacket(Stream s, int id, byte[] body)
        {
            var inner = new MemoryStream();
            WriteVarInt(inner, id);
            if (body != null) inner.Write(body, 0, body.Length);
            var outer = new MemoryStream();
            WriteVarInt(outer, (int)inner.Length);
            inner.WriteTo(outer);
            outer.WriteTo(s);
            s.Flush();
        }

        public static byte[] StringBody(string v)
        {
            var ms = new MemoryStream();
            WriteString(ms, v);
            return ms.ToArray();
        }

        /// <summary>
        /// Chiede al server (come fa la lista server del gioco) quanti giocatori sono online.
        /// -1 = non risponde (sta ancora partendo, o la risposta di stato è disattivata).
        /// </summary>
        public static int QueryPlayers(int port, int timeoutMs)
        {
            string json = QueryStatus(port, timeoutMs);
            if (json == null) return -1;
            try
            {
                var o = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
                int n;
                return int.TryParse(J.S(J.P(o, "players", "online")), out n) ? n : -1;
            }
            catch { return -1; }
        }

        /// <summary>Risposta di stato completa (JSON) come la vede la lista server; null se non risponde.</summary>
        public static string QueryStatus(int port, int timeoutMs)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return null;
                    c.EndConnect(ar);
                    c.ReceiveTimeout = timeoutMs;
                    c.SendTimeout = timeoutMs;
                    var s = c.GetStream();

                    var hs = new MemoryStream();
                    WriteVarInt(hs, -1);                         // versione: "qualsiasi"
                    WriteString(hs, "127.0.0.1");
                    hs.WriteByte((byte)(port >> 8));
                    hs.WriteByte((byte)(port & 0xFF));
                    WriteVarInt(hs, 1);                          // stato
                    SendPacket(s, 0x00, hs.ToArray());
                    SendPacket(s, 0x00, null);                   // richiesta di stato

                    int id;
                    var body = ReadPacket(s, 4 * 1024 * 1024, out id);
                    if (id != 0x00) return null;
                    return ReadString(body, 4 * 1024 * 1024);
                }
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Mentre il server dorme, il programma resta in ascolto sulle sue porte:
    /// nella lista server appare "in pausa", e chi prova a entrare lo riaccende.
    /// </summary>
    public class WakeListener
    {
        public Action<string> Log = delegate { };
        /// <summary>Chiamato (una sola volta) quando qualcuno prova a entrare. Il testo dice chi.</summary>
        public Action<string> WakeRequested = delegate { };
        /// <summary>Se restituisce false il giocatore non può svegliare il server (es. non è in whitelist).</summary>
        public Func<string, bool> CanWake = delegate { return true; };

        static readonly byte[] RakMagic = { 0x00, 0xFF, 0xFF, 0x00, 0xFE, 0xFE, 0xFE, 0xFE, 0xFD, 0xFD, 0xFD, 0xFD, 0x12, 0x34, 0x56, 0x78 };
        /// <summary>Testi con i codici colore "§" (si cambiano dalla scheda Personalizza).</summary>
        public string PauseText = "§6Server in pausa: §eentra per accenderlo (circa 1 minuto)";
        public string WakeText = "§6Il server si sta accendendo...\n§eRiprova tra circa un minuto.";
        public string DeniedText = "§cIl server è in pausa e tu non sei nella whitelist.";
        /// <summary>
        /// Scritta al posto dei giocatori nella lista (es. "§9● In pausa"). Per farla vedere la "versione" del server
        /// risulta diversa da quella del gioco: Minecraft allora mostra questo testo e la X rossa, come fa exaroton.
        /// Vuota = si vedono i giocatori (0/0) come prima.
        /// </summary>
        public string PauseVersion = "§9● In pausa";

        TcpListener tcp;
        UdpClient udp;
        volatile bool running, woken;
        int javaPort, bedrockPort;
        readonly long serverGuid = DateTime.Now.Ticks;

        public bool IsRunning { get { return running; } }

        public void Start(int port, int bedrock)
        {
            Stop();
            javaPort = port;
            bedrockPort = bedrock;
            woken = false;
            running = true;
            try
            {
                tcp = new TcpListener(IPAddress.Any, port);
                tcp.Start();
                var t = new Thread(AcceptLoop);
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception ex)
            {
                running = false;
                tcp = null;
                throw new Exception("Non riesco ad ascoltare sulla porta " + port + ": " + ex.Message);
            }
            if (bedrock > 0)
            {
                try
                {
                    udp = new UdpClient(new IPEndPoint(IPAddress.Any, bedrock));
                    var t = new Thread(UdpLoop);
                    t.IsBackground = true;
                    t.Start();
                }
                catch (Exception ex)
                {
                    udp = null;
                    Log("Bedrock: non riesco ad ascoltare sulla porta " + bedrock + " (" + ex.Message + "). Gli amici da telefono/console dovranno aspettare che qualcuno da PC accenda il server.");
                }
            }
        }

        public void Stop()
        {
            running = false;
            try { if (tcp != null) tcp.Stop(); } catch { }
            try { if (udp != null) udp.Close(); } catch { }
            tcp = null;
            udp = null;
        }

        void Wake(string who)
        {
            if (woken) return;
            woken = true;
            try { WakeRequested(who); } catch { }
        }

        // ------------------------------------------------------------ Java

        void AcceptLoop()
        {
            var l = tcp;
            while (running && l != null)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(_ => Handle(c));
            }
        }

        void Handle(TcpClient c)
        {
            try
            {
                using (c)
                {
                    c.ReceiveTimeout = 5000;
                    c.SendTimeout = 5000;
                    var s = c.GetStream();
                    int first = s.ReadByte();
                    if (first < 0 || first == 0xFE) return;      // ping dei client vecchissimi: ignoro

                    // il primo byte era già l'inizio della lunghezza del pacchetto
                    int len = first & 0x7F, shift = 7, b = first;
                    while ((b & 0x80) != 0)
                    {
                        b = s.ReadByte();
                        if (b < 0 || shift > 28) return;
                        len |= (b & 0x7F) << shift;
                        shift += 7;
                    }
                    if (len <= 0 || len > 4096) return;
                    var buf = new byte[len];
                    int off = 0;
                    while (off < len)
                    {
                        int r = s.Read(buf, off, len - off);
                        if (r <= 0) return;
                        off += r;
                    }
                    var hs = new MemoryStream(buf);
                    if (McProto.ReadVarInt(hs) != 0x00) return;
                    int protocol = McProto.ReadVarInt(hs);
                    McProto.ReadString(hs, 1024);
                    hs.ReadByte(); hs.ReadByte();
                    int next = McProto.ReadVarInt(hs);

                    var ser = new JavaScriptSerializer();
                    if (next == 1)
                    {
                        int id;
                        McProto.ReadPacket(s, 64, out id);           // richiesta di stato
                        var status = new Dictionary<string, object>();
                        bool tag = !string.IsNullOrEmpty(PauseVersion);
                        status["version"] = new Dictionary<string, object> { { "name", tag ? PauseVersion : "In pausa" }, { "protocol", tag ? -1 : protocol } };
                        status["players"] = new Dictionary<string, object> { { "max", 0 }, { "online", 0 } };
                        status["description"] = new Dictionary<string, object> { { "text", PauseText } };
                        McProto.SendPacket(s, 0x00, McProto.StringBody(ser.Serialize(status)));
                        try
                        {
                            var ping = McProto.ReadPacket(s, 64, out id);   // ping: rimando gli stessi 8 byte
                            if (id == 0x01) McProto.SendPacket(s, 0x01, Tail(ping));
                        }
                        catch { }
                        return;
                    }
                    if (next == 2 || next == 3)
                    {
                        string name = "";
                        try
                        {
                            int id;
                            var ls = McProto.ReadPacket(s, 4096, out id);
                            if (id == 0x00) name = McProto.ReadString(ls, 64);
                        }
                        catch { }
                        bool ok = CanWake(name);
                        var text = new Dictionary<string, object> { { "text", ok ? WakeText : DeniedText } };
                        McProto.SendPacket(s, 0x00, McProto.StringBody(ser.Serialize(text)));   // Disconnect (fase login)
                        if (ok) Wake(name.Length > 0 ? name : "un giocatore");
                        else Log("Tentativo di accesso di '" + name + "' (non in whitelist): il server resta in pausa.");
                    }
                }
            }
            catch { }
        }

        static byte[] Tail(MemoryStream ms)
        {
            var all = ms.ToArray();
            var r = new byte[all.Length - (int)ms.Position];
            Array.Copy(all, (int)ms.Position, r, 0, r.Length);
            return r;
        }

        // ------------------------------------------------------------ Bedrock (Geyser)

        void UdpLoop()
        {
            var u = udp;
            while (running && u != null)
            {
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] d;
                try { d = u.Receive(ref from); }
                catch (SocketException) { if (!running) break; continue; }
                catch { break; }
                if (d.Length == 0) continue;
                try
                {
                    if ((d[0] == 0x01 || d[0] == 0x02) && d.Length >= 9)
                    {
                        // Unconnected Ping -> Pong con il messaggio "in pausa"
                        string motd = "MCPE;" + PauseText.Replace(";", ",").Replace("\n", " ") + ";0;In pausa;0;0;" + serverGuid + ";MineServer;Survival;1;" +
                                      bedrockPort + ";" + bedrockPort + ";";
                        var ms = new MemoryStream();
                        ms.WriteByte(0x1C);
                        ms.Write(d, 1, 8);                                       // tempo del client
                        var g = BitConverter.GetBytes(serverGuid);
                        if (BitConverter.IsLittleEndian) Array.Reverse(g);
                        ms.Write(g, 0, 8);
                        ms.Write(RakMagic, 0, RakMagic.Length);
                        var sb = Encoding.UTF8.GetBytes(motd);
                        ms.WriteByte((byte)(sb.Length >> 8));
                        ms.WriteByte((byte)(sb.Length & 0xFF));
                        ms.Write(sb, 0, sb.Length);
                        var reply = ms.ToArray();
                        u.Send(reply, reply.Length, from);
                    }
                    else if (d[0] == 0x05)
                    {
                        // Open Connection Request 1: qualcuno sta entrando
                        Wake("un giocatore Bedrock");
                    }
                }
                catch { }
            }
        }
    }
}
