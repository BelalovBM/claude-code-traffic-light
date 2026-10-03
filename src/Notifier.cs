using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Semaphore
{
    // Delivers a message to the remote channel. Sending runs on the thread pool,
    // so a slow or unreachable server never blocks the tray.
    static class Notifier
    {
        static Notifier()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }
        }

        // auth, forbidden, ratelimit, server, timeout, network or other.
        public static string ErrorKind(string error)
        {
            if (string.IsNullOrEmpty(error)) return "other";
            if (error.StartsWith("401")) return "auth";
            if (error.StartsWith("403")) return "forbidden";
            if (error.StartsWith("429")) return "ratelimit";
            if (error.Length >= 3 && error[0] == '5' && char.IsDigit(error[1]) && char.IsDigit(error[2])) return "server";
            if (error.StartsWith("timeout")) return "timeout";
            if (error.StartsWith("network")) return "network";
            return "other";
        }

        public static void Dispatch(Config cfg, string title, string body, bool urgent)
        {
            if (cfg.NtfyEnabled && !cfg.NtfyPaused && !string.IsNullOrEmpty(cfg.NtfyTopic))
                SendNtfy(cfg.NtfyServer, Authorization(cfg), cfg.NtfyTopic, title, body, urgent, null, null);
        }

        // Value of the Authorization header for a server with access control, or null for none.
        // A token ("tk_...") needs no user name; otherwise user name and password are sent.
        public static string Authorization(Config cfg)
        {
            // The sign-in of the own server never goes to the public one.
            return cfg.NtfyOwn ? Authorization(cfg.NtfyUser, cfg.NtfySecret) : null;
        }

        public static string Authorization(string user, string secret)
        {
            if (string.IsNullOrEmpty(secret)) return null;
            if (string.IsNullOrEmpty(user))
                return secret.StartsWith("tk_", StringComparison.Ordinal) ? "Bearer " + secret : null;
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + secret));
        }

        // actions are ntfy action buttons (maps with action/label/url/method/body).
        // done is called on a pool thread with null on success or an error text.
        // tag is the name of an emoji ntfy puts before the title; by default the lamp of the state: red for
        // something that waits for the user, green for the rest (the colours of the tray icon).
        public static void SendNtfy(string server, string authorization, string topic, string title, string body,
            bool urgent, object[] actions, Action<string> done, string tag = null)
        {
            // The self-check runs on someone else's computer with made-up sessions: nothing leaves it.
            if (SelfCheckSetup.Active)
            {
                Log.Write("self-check: push not sent (" + title + ")");
                if (done != null) done("self-check");
                return;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                try
                {
                    Uri uri;
                    if (!Uri.TryCreate((server ?? "").Trim(), UriKind.Absolute, out uri) ||
                        (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                        throw new InvalidOperationException("Invalid server address");

                    // JSON publishing keeps non-ASCII titles intact (HTTP headers would not).
                    var payload = new Dictionary<string, object>();
                    payload["topic"] = topic;
                    payload["title"] = title;
                    payload["message"] = body;
                    payload["priority"] = urgent ? 4 : 3;
                    payload["tags"] = new[] { tag ?? (urgent ? "red_circle" : "green_circle") };
                    if (actions != null) payload["actions"] = actions;
                    byte[] data = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(payload));

                    var req = (HttpWebRequest)WebRequest.Create(uri);
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.Timeout = 10000;
                    req.ReadWriteTimeout = 10000;
                    req.UserAgent = "ClaudeCodeTrafficLight";
                    if (!string.IsNullOrEmpty(authorization)) req.Headers["Authorization"] = authorization;
                    using (var s = req.GetRequestStream())
                        s.Write(data, 0, data.Length);
                    using (req.GetResponse())
                    { }
                }
                catch (WebException ex) { error = Describe(ex); }
                catch (Exception ex) { error = ex.Message; }
                Log.Write(error == null ? "ntfy sent: " + (urgent ? "waiting" : "message") : "ntfy failed: " + error);
                if (done != null) done(error);
            });
        }

        // The first word tells what went wrong ("401 ...", "timeout: ...", "network: ..."), so the
        // interface can say it in plain words; the rest stays for the log.
        static string Describe(WebException ex)
        {
            var resp = ex.Response as HttpWebResponse;
            if (resp == null)
            {
                switch (ex.Status)
                {
                    case WebExceptionStatus.Timeout:
                        return "timeout: " + ex.Message;
                    case WebExceptionStatus.ConnectFailure:
                    case WebExceptionStatus.NameResolutionFailure:
                    case WebExceptionStatus.ProxyNameResolutionFailure:
                    case WebExceptionStatus.ConnectionClosed:
                    case WebExceptionStatus.ReceiveFailure:
                    case WebExceptionStatus.SendFailure:
                        return "network: " + ex.Message;
                    default:
                        return ex.Message;
                }
            }
            try
            {
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return (int)resp.StatusCode + " " + resp.StatusDescription + " " + r.ReadToEnd();
            }
            catch { return ex.Message; }
        }
    }
}
