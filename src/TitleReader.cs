using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Semaphore
{
    // Claude Code writes the chat title into the session transcript as "ai-title" (generated)
    // or "custom-title" (renamed by the user) entries, repeated as the file grows. Only the
    // end of the file is read, since transcripts can reach hundreds of megabytes.
    static class TitleReader
    {
        const int TailBytes = 1024 * 1024;

        public static string Read(string path)
        {
            string tail = Tail(path);
            return tail == null ? null : Latest(tail);
        }

        // The folder of the file the session touched last, taken from the end of the transcript; it shows
        // where the work really goes when the session was started elsewhere.
        public static string ReadWorkDir(string path)
        {
            string tail = Tail(path);
            if (tail == null) return null;
            const string key = "\"file_path\":\"";
            int at = tail.LastIndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;
            int open = at + key.Length - 1;
            for (int i = open + 1; i < tail.Length; i++)
            {
                if (tail[i] == '\\') { i++; continue; }
                if (tail[i] != '"') continue;
                try
                {
                    string file = new JavaScriptSerializer().Deserialize<string>(tail.Substring(open, i - open + 1));
                    return string.IsNullOrWhiteSpace(file) ? null : Path.GetDirectoryName(file);
                }
                catch { return null; }
            }
            return null;
        }

        // The tool call at the end of the transcript that has no result yet: what a waiting session asks for
        // (a permission prompt or a question). Null when the last call was answered or nothing is found.
        public static Approvals.Request ReadPendingRequest(string path)
        {
            string tail = Tail(path);
            if (tail == null) return null;
            try
            {
                var answered = new HashSet<string>();
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string[] lines = tail.Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i];
                    if (line.IndexOf("\"tool_result\"", StringComparison.Ordinal) >= 0)
                    {
                        foreach (Match m in Regex.Matches(line, "\"tool_use_id\":\"([^\"]+)\"")) answered.Add(m.Groups[1].Value);
                        continue;
                    }
                    if (line.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal) < 0) continue;

                    var root = serializer.DeserializeObject(line) as IDictionary<string, object>;
                    var message = root != null && root.ContainsKey("message") ? root["message"] as IDictionary<string, object> : null;
                    var content = message != null && message.ContainsKey("content") ? message["content"] as System.Collections.IList : null;
                    if (content == null) return null;
                    for (int c = content.Count - 1; c >= 0; c--)
                    {
                        var item = content[c] as IDictionary<string, object>;
                        if (item == null || !"tool_use".Equals(item.ContainsKey("type") ? item["type"] : null)) continue;
                        string id = item.ContainsKey("id") ? item["id"] as string : null;
                        if (id != null && answered.Contains(id)) continue;
                        return ToRequest(item);
                    }
                    return null;
                }
            }
            catch (Exception ex) { Log.Write("Pending request read failed: " + ex.Message); }
            return null;
        }

        static Approvals.Request ToRequest(IDictionary<string, object> item)
        {
            string name = item.ContainsKey("name") ? item["name"] as string : null;
            var input = item.ContainsKey("input") ? item["input"] as IDictionary<string, object> : null;
            var request = new Approvals.Request { Tool = name ?? "?", Summary = input == null ? "" : HookEvent.Describe(input) };
            var questions = input != null && input.ContainsKey("questions") ? input["questions"] as System.Collections.IList : null;
            if (name == "AskUserQuestion" && questions != null && questions.Count > 0)
            {
                var texts = new List<string>();
                foreach (object q in questions)
                {
                    var d = q as IDictionary<string, object>;
                    if (d != null && d.ContainsKey("question") && d["question"] is string) texts.Add((string)d["question"]);
                }
                request.Question = string.Join("\n", texts);
                request.Summary = request.Question;
                var first = questions[0] as IDictionary<string, object>;
                var options = first != null && first.ContainsKey("options") ? first["options"] as System.Collections.IList : null;
                if (questions.Count == 1 && options != null)
                {
                    request.Options = new List<string>();
                    request.Descriptions = new List<string>();
                    foreach (object o in options)
                    {
                        var od = o as IDictionary<string, object>;
                        if (od == null) continue;
                        request.Options.Add(od.ContainsKey("label") ? od["label"] as string ?? "" : "");
                        request.Descriptions.Add(od.ContainsKey("description") ? od["description"] as string ?? "" : "");
                    }
                }
            }
            return request;
        }

        // Background tasks the session started and has not been told about yet: Claude stops its turn while one
        // runs, and a new turn begins when the task reports back. The start is a tool result with
        // "backgroundTaskId"; the end is a <task-notification> naming the same <task-id>. A task can run long
        // after it started, so more of the transcript is read than for the title.
        public static int PendingBackgroundTasks(string path)
        {
            string tail = Tail(path, 8 * TailBytes);
            if (tail == null) return 0;
            var started = new HashSet<string>();
            foreach (Match m in Regex.Matches(tail, "\"backgroundTaskId\":\"([^\"]+)\""))
                started.Add(m.Groups[1].Value);
            if (started.Count == 0) return 0;
            foreach (Match m in Regex.Matches(tail, @"<task-id>([^<]+)</task-id>"))
                started.Remove(m.Groups[1].Value);
            return started.Count;
        }

        static string Tail(string path, int bytes = TailBytes)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - bytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return Encoding.UTF8.GetString(buf, 0, read);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Title read failed: " + ex.Message);
                return null;
            }
        }

        // The later entry wins, so a rename after the generated title is honoured.
        static string Latest(string text)
        {
            int custom = text.LastIndexOf("\"customTitle\":\"", StringComparison.Ordinal);
            int ai = text.LastIndexOf("\"aiTitle\":\"", StringComparison.Ordinal);
            if (custom < 0 && ai < 0) return null;

            int at = Math.Max(custom, ai);
            string key = at == custom ? "\"customTitle\":\"" : "\"aiTitle\":\"";
            int open = at + key.Length - 1;
            for (int i = open + 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] != '"') continue;
                try
                {
                    string title = new JavaScriptSerializer().Deserialize<string>(text.Substring(open, i - open + 1));
                    return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
                }
                catch { return null; }
            }
            return null;
        }
    }
}
