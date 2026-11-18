using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MineServer
{
    public static class Net
    {
        public const string UA = "MineServerManager/1.0 (strumento per server Minecraft su mini PC)";

        static HttpWebRequest Req(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return req;
        }

        public static string GetString(string url)
        {
            using (var resp = Req(url).GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        /// <summary>POST JSON. Restituisce il corpo della risposta anche in caso di errore HTTP (4xx/5xx).</summary>
        public static string PostString(string url, string json, string authorization)
        {
            var req = Req(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            if (authorization != null) req.Headers[HttpRequestHeader.Authorization] = authorization;
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            try
            {
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                if (ex.Response == null) throw;
                using (var sr = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                {
                    string txt = sr.ReadToEnd();
                    if (txt.TrimStart().StartsWith("{")) return txt;
                }
                throw;
            }
        }

        public static object ParseJson(string json)
        {
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.DeserializeObject(json);
        }

        public static string ToJson(object o)
        {
            return new JavaScriptSerializer().Serialize(o);
        }

        public static object GetJson(string url)
        {
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.DeserializeObject(GetString(url));
        }

        public static void Download(string url, string dest, Action<long, long> progress)
        {
            string tmp = dest + ".part";
            long done = 0, total = -1, lastReport = 0;
            using (var resp = Req(url).GetResponse())
            {
                total = resp.ContentLength;
                using (var s = resp.GetResponseStream())
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        done += n;
                        if (progress != null && done - lastReport >= 262144)
                        {
                            lastReport = done;
                            progress(done, total);
                        }
                    }
                }
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            if (progress != null) progress(done, total > 0 ? total : done);
        }
    }

    /// <summary>Piccoli aiuti per leggere il JSON di JavaScriptSerializer.</summary>
    public static class J
    {
        public static Dictionary<string, object> O(object o) { return o as Dictionary<string, object>; }
        public static object[] A(object o) { return o as object[]; }

        public static string S(object o)
        {
            return o == null ? null : Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        public static object P(object o, params string[] path)
        {
            foreach (var k in path)
            {
                var d = O(o);
                if (d == null || !d.ContainsKey(k)) return null;
                o = d[k];
            }
            return o;
        }
    }

    public class VersionComparer : IComparer<string>
    {
        public int Compare(string a, string b)
        {
            var pa = Parts(a);
            var pb = Parts(b);
            int n = Math.Max(pa.Count, pb.Count);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Count ? pa[i] : 0;
                int y = i < pb.Count ? pb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        static List<int> Parts(string s)
        {
            var l = new List<int>();
            foreach (var p in Regex.Split(s ?? "", @"\D+"))
            {
                int v;
                if (p.Length > 0 && int.TryParse(p, out v)) l.Add(v);
            }
            return l;
        }
    }
}
