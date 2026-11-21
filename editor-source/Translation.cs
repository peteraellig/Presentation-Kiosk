using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kiosk3;

/// <summary>
/// Einstellungen des Übersetzers. Liegt lokal neben dem Editor (Uebersetzer.json), nicht im Projekt,
/// damit der DeepL-Schlüssel nie mit dem Projekt oder dem Export weitergegeben wird.
/// </summary>
public class TranslatorSettings
{
    public string Engine { get; set; } = "DeepL";
    public string DeepLKey { get; set; } = "";
    static string FileName => Path.Combine(Project.Data, "Uebersetzer.json");
    public bool UseDeepL => Engine == "DeepL" && DeepLKey.Trim().Length > 0;

    public static TranslatorSettings Load()
    {
        try { if (File.Exists(FileName)) return JsonSerializer.Deserialize<TranslatorSettings>(File.ReadAllText(FileName)) ?? new(); }
        catch { }
        return new();
    }
    public void Save() => File.WriteAllText(FileName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

public static class Translation
{
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };
    static readonly Dictionary<(string, string, string, string), string> Cache = new();
    public static TranslatorSettings Settings { get; set; } = TranslatorSettings.Load();
    /// <summary>Gesetzt, wenn DeepL gewählt war, aber MyMemory einspringen musste (Grund für die Meldung).</summary>
    public static string? FallbackReason { get; set; }
    /// <summary>Welcher Dienst die letzte Übersetzung geliefert hat.</summary>
    public static string LastEngine { get; private set; } = "";

    public static async Task<string> Get(string source, string sourceCode, string code)
    {
        if (source.Trim().Length == 0) return "";
        if (Settings.UseDeepL)
        {
            try
            {
                var r = await Cached("DeepL", source, sourceCode, code, () => DeepL(source, sourceCode, code));
                LastEngine = "DeepL";
                return r;
            }
            catch (Exception ex) { FallbackReason ??= ex.Message; }
        }
        else if (Settings.Engine == "DeepL") FallbackReason ??= "No DeepL key entered.";
        var m = await Cached("MyMemory", source, sourceCode, code, () => MyMemory(source, sourceCode, code));
        LastEngine = "MyMemory";
        return m;
    }

    static async Task<string> Cached(string engine, string source, string from, string to, Func<Task<string>> make)
    {
        if (Cache.TryGetValue((engine, source, from, to), out var hit)) return hit;
        return Cache[(engine, source, from, to)] = await make();
    }

    // ----- DeepL ------------------------------------------------------------------------

    static string Endpoint(string key, string path) =>
        (key.Trim().EndsWith(":fx", StringComparison.OrdinalIgnoreCase) ? "https://api-free.deepl.com/v2/" : "https://api.deepl.com/v2/") + path;

    static HttpRequestMessage DeepLRequest(HttpMethod method, string path, string key)
    {
        var req = new HttpRequestMessage(method, Endpoint(key, path));
        req.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key.Trim());
        return req;
    }

    /// <summary>DeepL will bei Englisch und Portugiesisch als Ziel die Variante.</summary>
    static string Target(string code)
    {
        var two = Language.Catalog[code].TwoLetterISOLanguageName.ToUpperInvariant();
        return two switch { "EN" => "EN-GB", "PT" => "PT-PT", "ZH" => "ZH-HANS", _ => two };
    }

    static async Task<string> DeepL(string source, string sourceCode, string code)
    {
        // Jede Zeile einzeln schicken, damit die eigenen Zeilenumbrüche erhalten bleiben.
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var texts = new JsonArray();
        foreach (var l in lines.Where(l => l.Trim().Length > 0)) texts.Add(l.Trim());
        var body = new JsonObject
        {
            ["text"] = texts,
            ["source_lang"] = Language.Catalog[sourceCode].TwoLetterISOLanguageName.ToUpperInvariant(),
            ["target_lang"] = Target(code),
            ["formality"] = "prefer_more",   // Sie-Form, wo die Sprache das kennt
            ["preserve_formatting"] = true,
        };
        using var req = DeepLRequest(HttpMethod.Post, "translate", Settings.DeepLKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Send(req);
        var answer = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception(DeepLError(res.StatusCode, answer));
        var result = JsonNode.Parse(answer)!["translations"]!.AsArray().Select(t => t!["text"]!.GetValue<string>()).ToList();
        if (result.Count != texts.Count) throw new Exception("DeepL returned an unexpected answer.");
        int i = 0;
        return string.Join("\n", lines.Select(l => l.Trim().Length > 0 ? result[i++] : ""));
    }

    static string DeepLError(HttpStatusCode status, string answer) => (int)status switch
    {
        403 => "DeepL: key invalid. Check it in tab «Project».",
        456 => "DeepL: monthly quota used up.",
        429 => "DeepL: too many requests, wait a moment.",
        400 => "DeepL: language not supported or bad request.",
        _ => $"DeepL: error {(int)status}.",
    };

    static async Task<HttpResponseMessage> Send(HttpRequestMessage req)
    {
        try { return await Client.SendAsync(req); }
        catch (HttpRequestException ex) { throw new Exception("DeepL cannot be reached (internet?).", ex); }
        catch (TaskCanceledException ex) { throw new Exception("DeepL does not answer in time.", ex); }
    }

    /// <summary>Schlüssel prüfen und Verbrauch abfragen.</summary>
    public static async Task<string> DeepLUsage(string key)
    {
        if (key.Trim().Length == 0) return "No key entered.";
        using var req = DeepLRequest(HttpMethod.Get, "usage", key);
        using var res = await Send(req);
        var answer = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return DeepLError(res.StatusCode, answer);
        var json = JsonNode.Parse(answer)!;
        long used = json["character_count"]?.GetValue<long>() ?? 0, limit = json["character_limit"]?.GetValue<long>() ?? 0;
        return $"✓ Key valid · {used:N0} of {limit:N0} characters used this month.";
    }

    // ----- MyMemory (ohne Konto, Ausweichlösung) ------------------------------------------

    static async Task<string> MyMemory(string source, string sourceCode, string code)
    {
        var origin = Language.Catalog[sourceCode].TwoLetterISOLanguageName;
        var target = Language.Catalog[code].TwoLetterISOLanguageName;
        var lines = new List<string>();
        foreach (var paragraph in source.ReplaceLineEndings("\n").Split('\n'))
        {
            var pieces = new List<string>();
            foreach (var chunk in Chunks(paragraph))
            {
                var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(chunk) + "&langpair=" + Uri.EscapeDataString(origin) + "%7C" + Uri.EscapeDataString(target);
                using var response = await Request(url);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = json.RootElement;
                if (root.GetProperty("responseStatus").ToString() != "200" || (root.TryGetProperty("quotaFinished", out var quota) && quota.ValueKind == JsonValueKind.True))
                    throw new Exception("MyMemory: " + (root.TryGetProperty("responseDetails", out var d) ? d.ToString() : "daily limit reached."));
                var value = root.GetProperty("responseData").GetProperty("translatedText").GetString();
                if (string.IsNullOrWhiteSpace(value)) throw new Exception("MyMemory returned an empty translation.");
                pieces.Add(WebUtility.HtmlDecode(value));
            }
            lines.Add(string.Join(" ", pieces));
        }
        return string.Join("\n", lines);
    }

    static async Task<HttpResponseMessage> Request(string url)
    {
        try { return await Client.GetAsync(url); }
        catch (HttpRequestException ex) { throw new Exception("MyMemory cannot be reached. Check the internet connection and access to api.mymemory.translated.net. Existing versions can be edited offline.", ex); }
        catch (TaskCanceledException ex) { throw new Exception("MyMemory does not answer in time. Try again.", ex); }
    }

    static IEnumerable<string> Chunks(string text)
    {
        var remaining = text.Trim();
        while (Encoding.UTF8.GetByteCount(remaining) > 500)
        {
            int end = 0, bytes = 0;
            foreach (var rune in remaining.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 500) break;
                bytes += rune.Utf8SequenceLength;
                end += rune.Utf16SequenceLength;
            }
            var prefix = remaining[..end];
            var breaks = Regex.Matches(prefix, @"(?<=[.!?])\s+");
            if (breaks.Count == 0) breaks = Regex.Matches(prefix, @"\s+");
            if (breaks.Count > 0) end = breaks[^1].Index;
            yield return remaining[..end].TrimEnd();
            remaining = remaining[end..].TrimStart();
        }
        if (remaining.Length > 0) yield return remaining;
    }
}
