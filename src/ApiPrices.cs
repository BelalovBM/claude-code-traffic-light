using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Semaphore
{
    // What the use in the transcripts would cost at Anthropic's API prices: for a subscription, to see what the same
    // work would cost by the token; for a sign-in by API key, roughly what it is billed. The prices are built in (from
    // Anthropic's price list when the version was made); prices typed in the settings or loaded from LiteLLM's open
    // price file are kept in api-prices.json next to the program, each with where it came from.
    static class ApiPrices
    {
        // Dollars per million tokens. Writing to the cache costs 1.25 of the input for 5 minutes and 2 for an hour
        // (Claude Code on a subscription writes for an hour); a model without its own price for reading the cache
        // reads at a tenth of the input. Some models cost more from a long prompt on.
        internal sealed class Price
        {
            public double Input { get; set; }
            public double Output { get; set; }
            public double? CacheRead { get; set; }
            public long LongFrom { get; set; }
            public double? LongInput { get; set; }
            public double? LongOutput { get; set; }
            // Where a price of the file came from: "you" (typed in the settings) or "litellm" (loaded, on Date, local
            // "yyyy-MM-dd HH:mm"); null for a built-in one.
            public string Source { get; set; }
            public string Date { get; set; }

            // Reading the cache when the price names none: a tenth of the input.
            internal double Reading { get { return CacheRead ?? Input / 10; } }

            internal bool SameAs(Price o)
            {
                Func<double?, double?, bool> eq = (a, b) => a.HasValue == b.HasValue && (!a.HasValue || Math.Abs(a.Value - b.Value) < 1e-9);
                return o != null && eq(Input, o.Input) && eq(Output, o.Output) && eq(Reading, o.Reading)
                    && LongFrom == o.LongFrom && eq(LongInput, o.LongInput) && eq(LongOutput, o.LongOutput);
            }
        }

        internal const string FromYou = "you", FromLiteLlm = "litellm";

        // Each web search Claude makes is $10 per thousand on top of the tokens.
        internal const double WebSearch = 0.01;

        static readonly Dictionary<string, Price> BuiltIn = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase)
        {
            { "claude-fable-5-1", new Price { Input = 10, Output = 50, CacheRead = 0.25 } },
            { "claude-mythos-5-1", new Price { Input = 10, Output = 50, CacheRead = 0.25 } },
            { "claude-fable-5", new Price { Input = 10, Output = 50 } },
            { "claude-opus-5-5", new Price { Input = 4, Output = 20, CacheRead = 0.20 } },
            { "claude-opus-5", new Price { Input = 5, Output = 25 } },
            { "claude-opus-4-8", new Price { Input = 5, Output = 25 } },
            { "claude-opus-4-7", new Price { Input = 5, Output = 25 } },
            { "claude-opus-4-6", new Price { Input = 5, Output = 25 } },
            { "claude-opus-4-5", new Price { Input = 5, Output = 25 } },
            { "claude-sonnet-5-5", new Price { Input = 2, Output = 10, CacheRead = 0.10 } },
            { "claude-sonnet-5", new Price { Input = 2, Output = 10, CacheRead = 0.20 } },
            { "claude-sonnet-4-6", new Price { Input = 3, Output = 15 } },
            { "claude-sonnet-4-5", new Price { Input = 3, Output = 15 } },
            { "claude-haiku-5-5", new Price { Input = 0.10, Output = 0.50, LongFrom = 100000, LongInput = 0.50, LongOutput = 2.50 } },
            { "claude-haiku-4-5", new Price { Input = 1, Output = 5 } },
        };

        static Dictionary<string, Price> table;
        static DateTime fileWritten, fileChecked;

        internal static string FilePath { get { return Path.Combine(AppPaths.DataDir, "api-prices.json"); } }

        // The built-in prices with those of the file over them; the file is read again when it changes.
        static Dictionary<string, Price> Table()
        {
            // Asked for every step counted: the file is looked at again at most every few seconds.
            if (table != null && DateTime.UtcNow - fileChecked < TimeSpan.FromSeconds(5)) return table;
            fileChecked = DateTime.UtcNow;
            string path = FilePath;
            DateTime written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (table != null && written == fileWritten) return table;
            var t = new Dictionary<string, Price>(BuiltIn, StringComparer.OrdinalIgnoreCase);
            if (written != DateTime.MinValue)
            {
                try
                {
                    var own = new JavaScriptSerializer().Deserialize<Dictionary<string, Price>>(File.ReadAllText(path, Encoding.UTF8));
                    if (own != null) foreach (var kv in own) if (kv.Value != null && kv.Value.Input > 0) t[kv.Key] = kv.Value;
                }
                catch (Exception ex) { Log.Write("api-prices.json could not be read: " + ex.Message); }
            }
            fileWritten = written;
            lock (found) found.Clear();
            table = t;
            return t;
        }

        // The price of a model: its own, or that of the longest name it starts with ("claude-haiku-4-5-20251001" is
        // "claude-haiku-4-5"). A newer model of a known family has no price until it is added: it is not guessed.
        internal static Price For(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            Dictionary<string, Price> t = Table();
            Price p;
            lock (found)
            {
                if (found.TryGetValue(model, out p)) return p;
                if (!t.TryGetValue(model, out p))
                {
                    string key = t.Keys.Where(k => model.StartsWith(k + "-", StringComparison.OrdinalIgnoreCase)).OrderByDescending(k => k.Length).FirstOrDefault();
                    p = key != null ? t[key] : null;
                }
                found[model] = p;
                return p;
            }
        }

        // The price found for each model name, until the prices change.
        static readonly Dictionary<string, Price> found = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase);

        // Read the prices again now (after they were changed here), not when the file is next looked at.
        internal static void Reload()
        {
            table = null;
        }

        // ---- changing the prices ----

        // The prices of the file: those typed in and those loaded.
        internal static Dictionary<string, Price> Kept()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var d = new JavaScriptSerializer().Deserialize<Dictionary<string, Price>>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (d != null)
                        return new Dictionary<string, Price>(d.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) { Log.Write("api-prices.json could not be read: " + ex.Message); }
            return new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase);
        }

        static void Keep(Dictionary<string, Price> kept)
        {
            if (kept.Count == 0) { if (File.Exists(FilePath)) File.Delete(FilePath); }
            else
            {
                var s = new JavaScriptSerializer();
                File.WriteAllText(FilePath, Json.Pretty(s.DeserializeObject(s.Serialize(kept))), new UTF8Encoding(false));
            }
            Reload();
        }

        // A price typed in the settings for one model.
        internal static void SetOwn(string model, double input, double output, double? cacheRead)
        {
            var kept = Kept();
            Price old = For(model);
            kept[model] = new Price
            {
                Input = input, Output = output, CacheRead = cacheRead, Source = FromYou,
                // A higher price for long prompts stays as it was: it is not typed in the table.
                LongFrom = old != null ? old.LongFrom : 0,
                LongInput = old != null ? old.LongInput : null,
                LongOutput = old != null ? old.LongOutput : null,
            };
            Keep(kept);
        }

        // Back to the built-in prices: what was typed in and what was loaded is dropped.
        internal static void Clear()
        {
            Keep(new Dictionary<string, Price>());
        }

        internal static bool AnyKept { get { return File.Exists(FilePath); } }

        // The prices loaded from LiteLLM replace those kept so far, typed ones too: LiteLLM goes first, so that the
        // prices stay current without anyone looking after them. A typed price stays only for a model LiteLLM lacks.
        internal static void Apply(Dictionary<string, Price> loaded, DateTime when)
        {
            var kept = Kept();
            string date = when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            foreach (var kv in loaded)
            {
                kv.Value.Source = FromLiteLlm;
                kv.Value.Date = date;
                kept[kv.Key] = kv.Value;
            }
            Keep(kept);
        }

        // One difference between the prices now and those loaded: a new model (Old null) or a changed price.
        internal sealed class Change
        {
            public string Model;
            public Price Old, New;
        }

        internal static List<Change> Compare(Dictionary<string, Price> loaded)
        {
            var list = new List<Change>();
            foreach (var kv in loaded.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                // The price the model has now, its own or its base model's: a dated name priced as its base model
                // already is ("claude-haiku-4-5-20251001") is no news.
                Price now = For(kv.Key);
                if (now != null && now.SameAs(kv.Value)) continue;
                list.Add(new Change { Model = kv.Key, Old = now, New = kv.Value });
            }
            return list;
        }

        // ---- LiteLLM's open price file ----

        internal const string LiteLlmFile = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
        internal const string LiteLlmPage = "https://github.com/BerriAI/litellm/blob/main/model_prices_and_context_window.json";
        internal const string AnthropicPage = "https://platform.claude.com/docs/en/about-claude/pricing";
        static readonly System.Text.RegularExpressions.Regex AboveRx = new System.Text.RegularExpressions.Regex("^input_cost_per_token_above_([0-9]+)k_tokens$");

        // The Claude models of Anthropic's own API in the file, in dollars per million tokens; empty when there are none.
        internal static Dictionary<string, Price> ParseLiteLlm(string json)
        {
            var result = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase);
            var all = Json.Parse(json) as IDictionary<string, object>;
            if (all == null) return result;
            foreach (var kv in all)
            {
                var m = kv.Value as IDictionary<string, object>;
                if (m == null || !kv.Key.StartsWith("claude-", StringComparison.Ordinal) || kv.Key.IndexOfAny(new[] { '/', '@', ':' }) >= 0) continue;
                object provider;
                if (!m.TryGetValue("litellm_provider", out provider) || !"anthropic".Equals(provider)) continue;
                double? input = PerMillion(m, "input_cost_per_token"), output = PerMillion(m, "output_cost_per_token");
                if (!input.HasValue || !output.HasValue || input <= 0) continue;
                var p = new Price { Input = input.Value, Output = output.Value, CacheRead = PerMillion(m, "cache_read_input_token_cost") };
                // A higher price from a long prompt on: "input_cost_per_token_above_100k_tokens".
                foreach (string key in m.Keys)
                {
                    var above = AboveRx.Match(key);
                    if (!above.Success) continue;
                    p.LongFrom = long.Parse(above.Groups[1].Value, CultureInfo.InvariantCulture) * 1000;
                    p.LongInput = PerMillion(m, key);
                    p.LongOutput = PerMillion(m, "output_cost_per_token_above_" + above.Groups[1].Value + "k_tokens");
                }
                result[kv.Key] = p;
            }
            return result;
        }

        static double? PerMillion(IDictionary<string, object> m, string key)
        {
            object v;
            if (!m.TryGetValue(key, out v) || v == null) return null;
            try { return Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture) * 1e6, 6); }
            catch (Exception) { return null; }
        }

        // Loads the file (on a pool thread; done is called there): the prices, or the error in words for the user.
        internal static void LoadLiteLlm(Action<Dictionary<string, Price>, string> done)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                Dictionary<string, Price> prices = null;
                string error = null;
                try
                {
                    try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; }
                    catch { }
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(LiteLlmFile);
                    req.UserAgent = "ClaudeCodeTrafficLight";
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 30000;
                    string text;
                    using (var resp = req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        text = reader.ReadToEnd();
                    prices = ParseLiteLlm(text);
                    if (prices.Count == 0) { error = Loc.T("prices.load.empty"); prices = null; }
                }
                catch (Exception ex) { error = ex.Message; }
                Log.Write(error == null ? "Prices from LiteLLM: " + prices.Count + " Claude models" : "Prices from LiteLLM could not be loaded: " + error);
                done(prices, error);
            });
        }

        // Dollars for one reply, or null when the model has no price. Fast mode is priced at twice the standard.
        internal static double? Usd(string model, long input, long write5m, long write1h, long read, long output, long searches, bool fast)
        {
            Price p = For(model);
            if (p == null) return null;
            bool longPrompt = p.LongFrom > 0 && input + write5m + write1h + read > p.LongFrom;
            double inPrice = longPrompt && p.LongInput.HasValue ? p.LongInput.Value : p.Input;
            double outPrice = longPrompt && p.LongOutput.HasValue ? p.LongOutput.Value : p.Output;
            double readPrice = p.CacheRead.HasValue ? p.CacheRead.Value * inPrice / p.Input : inPrice / 10;
            double usd = (input * inPrice + write5m * 1.25 * inPrice + write1h * 2 * inPrice + read * readPrice + output * outPrice) / 1e6;
            return (fast ? 2 : 1) * usd + searches * WebSearch;
        }

        // "740", "35k", "18M", "3.2B" in the words of the program's language.
        internal static string Tokens(long n, CultureInfo culture)
        {
            if (n < 1000) return n.ToString(culture);
            if (n < 1000000) return Loc.T("cost.tokens.k", Math.Round(n / 1e3).ToString("0", culture));
            if (n < 1000000000) return Loc.T("cost.tokens.m", (n / 1e6).ToString(n < 1e7 ? "0.#" : "0", culture));
            return Loc.T("cost.tokens.b", (n / 1e9).ToString(n < 1e10 ? "0.#" : "0", culture));
        }

        // "$0.42", "$38", "$1,089".
        internal static string Dollars(double usd, CultureInfo culture)
        {
            return "$" + usd.ToString(usd < 10 ? "0.00" : "N0", culture);
        }
    }
}
