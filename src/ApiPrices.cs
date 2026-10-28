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
    // work would cost by the token; for a sign-in by API key, roughly what it is billed. The prices are built in and
    // can be changed or added to in api-prices.json next to the program, so that a new model needs no new version.
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
        }

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
            { "claude-sonnet-5-5", new Price { Input = 2, Output = 10, CacheRead = 0.20 } },
            { "claude-sonnet-5", new Price { Input = 2, Output = 10, CacheRead = 0.20 } },
            { "claude-sonnet-4-6", new Price { Input = 3, Output = 15 } },
            { "claude-sonnet-4-5", new Price { Input = 3, Output = 15 } },
            { "claude-haiku-5-5", new Price { Input = 0.10, Output = 0.50, LongFrom = 100000, LongInput = 0.50, LongOutput = 2.50 } },
            { "claude-haiku-4-5", new Price { Input = 1, Output = 5 } },
        };

        static Dictionary<string, Price> table;
        static DateTime fileWritten;

        internal static string FilePath { get { return Path.Combine(AppPaths.DataDir, "api-prices.json"); } }

        // The built-in prices with those of the file over them; the file is read again when it changes.
        static Dictionary<string, Price> Table()
        {
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
            if (t.TryGetValue(model, out p)) return p;
            string key = t.Keys.Where(k => model.StartsWith(k + "-", StringComparison.OrdinalIgnoreCase)).OrderByDescending(k => k.Length).FirstOrDefault();
            return key != null ? t[key] : null;
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
