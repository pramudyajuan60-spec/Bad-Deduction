using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BadDeduction.AI;

/// <summary>Connection settings for the Ollama-backed dialogue provider. No secrets involved.</summary>
public sealed class OllamaOptions
{
    /// <summary>Base URL of the local Ollama server.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Model tag to query, e.g. "llama3.1:8b". Any tag Ollama knows works.</summary>
    public string Model { get; set; } = "llama3.1:8b";

    /// <summary>HTTP timeout per exchange, in seconds. The call is synchronous (ADR-027).</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Sampling temperature forwarded to Ollama. Config only — never game state.</summary>
    public double Temperature { get; set; } = 0.7;
}

/// <summary>
/// Drop-in <see cref="IAIProvider"/> backed by a local Ollama server.
/// <para/>
/// The provider sends the EXACT prompt the pipeline built for the mock
/// (<see cref="AIRequest.ContextPrompt"/>, verbatim, as the single user message to
/// <c>POST {endpoint}/api/chat</c> with <c>stream: false</c>). It never invents prompt
/// content, so the truth/knowledge separation (ContextEngine builds prompts from one
/// NPC's knowledge only) holds unchanged — see the prompt-parity test.
/// <para/>
/// The model's free text is parsed best-effort into <see cref="DialogueOutput"/>
/// (reply + the structured fields the prompt asks for). The result still flows through
/// the SAME <see cref="DialogueValidator"/> in the orchestrator: the validator stays
/// the anti-hallucination guard and may still reject (role words, unknown entities,
/// over-long fields), which funnels into the orchestrator's deterministic fallback.
/// <para/>
/// FAILURE POLICY: on ANY transport or parse failure (Ollama not running, timeout,
/// non-2xx, malformed JSON, empty content) the provider delegates to its fallback
/// (normally the mock) and marks the result <see cref="AIResponse.UsedFallback"/>.
/// The game never crashes or hangs because Ollama is missing; callers can surface a
/// small "offline dialogue" indicator from the flag.
/// <para/>
/// DETERMINISM BOUNDARY (ADR-067): with Ollama active, dialogue output is inherently
/// non-deterministic. Replay-hash reproducibility holds only with the mock provider,
/// which remains the default; Ollama is opt-in.
/// <para/>
/// Synchronous by design: the async HttpClient API is blocked on with
/// GetAwaiter().GetResult() — deadlock-safe here because HttpClient never marshals
/// continuations back to a synchronization context, and it works with ANY
/// HttpMessageHandler (unlike HttpClient.Send, which requires handlers to override
/// the sync Send virtual). This keeps the <see cref="IAIProvider"/> sync contract.
/// </summary>
public sealed class OllamaDialogueProvider : IAIProvider, IDisposable
{
    private static readonly Regex FieldHeader = new(
        @"^\s*(trust[\s_]*delta|suspicion[\s_]*delta|new[\s_]*facts|memory[\s_]*summary|relationship[\s_]*note)\s*[:=]\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FirstInteger = new(
        @"-?\d+", RegexOptions.Compiled);

    private readonly OllamaOptions _options;
    private readonly IAIProvider _fallback;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private bool _disposed;

    /// <summary>
    /// Creates a provider with an explicit fallback (used by tests with a fake handler).
    /// The fallback must return a fresh <see cref="AIResponse"/> per call.
    /// </summary>
    public OllamaDialogueProvider(OllamaOptions options, IAIProvider fallback, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            throw new ArgumentException("An endpoint is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.Model))
            throw new ArgumentException("A model tag is required.", nameof(options));

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)) };
            _ownsHttp = true;
        }
    }

    /// <summary>Convenience: mock fallback seeded like the rest of the run.</summary>
    public OllamaDialogueProvider(ulong seed, OllamaOptions? options = null)
        : this(options ?? new OllamaOptions(), new MockAIProvider(seed))
    {
    }

    public AIResponse Complete(AIRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        try
        {
            var content = Chat(request.ContextPrompt);
            var output = ParseOutput(content);
            return AIResponse.Accept(output);
        }
        catch (Exception)
        {
            // ANY failure (not running, timeout, bad status, bad JSON, empty/unparseable
            // content) → the fallback's output, byte-identical to calling it directly,
            // flagged so the UI can show "offline dialogue". Never throws: the game must
            // survive a missing Ollama.
            var fb = _fallback.Complete(request);
            fb.UsedFallback = true;
            return fb;
        }
    }

    // ------------------------------------------------------------------ transport

    private string Chat(string prompt)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = _options.Model,
            stream = false,
            options = new { temperature = _options.Temperature },
            messages = new[] { new { role = "user", content = prompt } },
        });

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, _options.Endpoint.TrimEnd('/') + "/api/chat")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        // Blocked synchronously (see class docs): keeps the IAIProvider contract and
        // works with any handler, including test fakes that only override SendAsync.
        using var response = _http.SendAsync(httpRequest).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}.");

        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("Ollama returned empty content.");
        return content;
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>
    /// Best-effort parse of the model's free text. Lines before the first structured
    /// field header are the in-character reply; headers introduce
    /// trust_delta / suspicion_delta / new_facts / memory_summary / relationship_note.
    /// Missing fields degrade gracefully (zero deltas, no facts) — the validator still
    /// gets the final say. Deliberately no truncation here: over-long fields are the
    /// validator's documented rejection, not the provider's silent edit.
    /// </summary>
    public static DialogueOutput ParseOutput(string content)
    {
        var text = StripCodeFence(content).Replace("\r\n", "\n");
        var lines = text.Split('\n');

        var replyLines = new List<string>();
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;

        foreach (var rawLine in lines)
        {
            var m = FieldHeader.Match(rawLine);
            if (m.Success)
            {
                current = m.Groups[1].Value.Replace(" ", "", StringComparison.Ordinal)
                    .Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
                fields[current] = new List<string>();
                var rest = m.Groups[2].Value.Trim();
                if (rest.Length > 0) fields[current].Add(rest);
            }
            else if (current is null)
            {
                replyLines.Add(rawLine);
            }
            else
            {
                fields[current].Add(rawLine);
            }
        }

        var reply = string.Join("\n", replyLines).Trim();
        if (reply.Length == 0)
            throw new InvalidOperationException("Ollama returned no speakable reply text.");

        return new DialogueOutput
        {
            ReplyText = reply,
            TrustDelta = ParseDelta(fields, "trustdelta"),
            SuspicionDelta = ParseDelta(fields, "suspiciondelta"),
            NewFacts = ParseFacts(fields),
            NewMemorySummary = ParseSingleLine(fields, "memorysummary"),
            RelationshipNote = ParseSingleLine(fields, "relationshipnote"),
        };
    }

    private static string StripCodeFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNl = t.IndexOf('\n');
        if (firstNl < 0) return text;
        t = t.Substring(firstNl + 1);
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0) t = t.Substring(0, lastFence);
        return t;
    }

    private static int ParseDelta(
        Dictionary<string, List<string>> fields, string key)
    {
        if (!fields.TryGetValue(key, out var lines)) return 0;
        var m = FirstInteger.Match(string.Join(" ", lines));
        return m.Success && int.TryParse(m.Value, out var v) ? v : 0;
    }

    private static List<string> ParseFacts(Dictionary<string, List<string>> fields)
    {
        var facts = new List<string>();
        if (!fields.TryGetValue("newfacts", out var lines)) return facts;

        var joined = string.Join("\n", lines).Trim();
        if (joined.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                var arr = JsonSerializer.Deserialize<string[]>(joined);
                if (arr is not null)
                    foreach (var f in arr)
                        AddFact(facts, f);
                return facts;
            }
            catch (JsonException)
            {
                // Fall through to line splitting.
            }
        }

        foreach (var chunk in joined.Split(new[] { '\n', ';' }, StringSplitOptions.None))
            AddFact(facts, chunk);
        return facts;
    }

    private static void AddFact(List<string> facts, string raw)
    {
        if (facts.Count >= AIRules.MaxNewFacts) return;
        var f = raw.Trim().TrimStart('-', '*', ' ', '\t');
        // Strip "1. " style bullets.
        var dot = f.IndexOf('.');
        if (dot > 0 && dot < 4 && f.Substring(0, dot).All(char.IsDigit))
            f = f.Substring(dot + 1).TrimStart();
        f = f.Trim().Trim('"', '\'').Trim();
        if (f.Length > 0) facts.Add(f);
    }

    private static string? ParseSingleLine(
        Dictionary<string, List<string>> fields, string key)
    {
        if (!fields.TryGetValue(key, out var lines)) return null;
        var v = string.Join(" ", lines).Trim();
        return v.Length == 0 ? null : v;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
    }
}
