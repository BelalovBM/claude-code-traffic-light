using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Semaphore
{
    // Whether a newer version is out: one anonymous request to GitHub for the latest release of this repository. Off
    // until switched on (or asked for from the About page); nothing about the user or the computer is sent.
    static class Updates
    {
        const string LatestApi = "https://api.github.com/repos/BelalovBM/claude-code-traffic-light/releases/latest";

        public sealed class Result
        {
            public string Error;      // null when the check worked
            public string Latest;     // "0.9.6"
            public string Url;        // the release page
            public bool Newer;        // newer than this program
        }

        // On a pool thread; done is called there with the result.
        public static void Check(Action<Result> done)
        {
            if (SelfCheckSetup.Active) { done(new Result { Error = "self-check" }); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var r = new Result();
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
                    catch { }
                    var req = (HttpWebRequest)WebRequest.Create(LatestApi);
                    req.UserAgent = "ClaudeCodeTrafficLight";
                    req.Accept = "application/vnd.github+json";
                    req.Timeout = 15000;
                    req.ReadWriteTimeout = 15000;
                    string text;
                    using (var resp = req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        text = reader.ReadToEnd();
                    var d = Json.Parse(text) as IDictionary<string, object>;
                    string tag = d != null && d.ContainsKey("tag_name") ? d["tag_name"] as string : null;
                    r.Url = d != null && d.ContainsKey("html_url") ? d["html_url"] as string : null;
                    r.Latest = tag == null ? null : tag.TrimStart('v', 'V');
                    if (r.Latest == null) r.Error = "no version in the answer";
                    else r.Newer = IsNewer(r.Latest, AppInfo.Version);
                }
                catch (Exception ex) { r.Error = ex.Message; }
                Log.Write(r.Error == null ? "Update check: latest " + r.Latest + (r.Newer ? ", newer than " + AppInfo.Version : ", this is current") : "Update check failed: " + r.Error);
                done(r);
            });
        }

        // "0.10.0" is newer than "0.9.5": the numbers compare, not the text. A "-beta" ending is ignored.
        internal static bool IsNewer(string latest, string current)
        {
            Version a, b;
            return Version.TryParse(Numbers(latest), out a) && Version.TryParse(Numbers(current), out b) && a > b;
        }

        static string Numbers(string v)
        {
            int dash = v.IndexOf('-');
            if (dash >= 0) v = v.Substring(0, dash);
            return v.Split('.').Length == 1 ? v + ".0" : v;
        }
    }
}
