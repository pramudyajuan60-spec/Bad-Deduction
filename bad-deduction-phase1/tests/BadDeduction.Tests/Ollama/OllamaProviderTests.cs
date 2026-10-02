using System.Net;
using System.Text.Json;
using BadDeduction.AI;
using BadDeduction.Core;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests.Ollama;

/// <summary>
/// Ollama provider tests. Never touch a real server: every test injects a fake
/// <see cref="HttpMessageHandler"/> (deterministic, offline-safe).
/// </summary>
public sealed class OllamaProviderTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => throw new InvalidOperationException("test setup: no responder configured");
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(Responder(request));
        }
    }

    private static AIRequest SampleRequest() => new()
    {
        SpeakerId = "s1",
        SpeakerName = "Aldric",
        ListenerId = "l1",
        ListenerName = "Bryn",
        ConversationId = "c1",
        Utterance = "What did you see?",
        ContextPrompt = "You are roleplaying Aldric, a 40-year-old merchant.\nBryn says to you: \"What did you see?\"\nReply in character.",
    };

    private static OllamaDialogueProvider WithResponder(FakeHandler handler, ulong seed = 7) =>
        new(new OllamaOptions(), new MockAIProvider(seed), new HttpClient(handler));

    private static string ChatJson(string content) =>
        JsonSerializer.Serialize(new { message = new { role = "assistant", content } });

    private static HttpResponseMessage Ok(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(ChatJson(content)) };

    private const string StructuredContent =
        "I saw a lantern near the chapel, I swear it.\n" +
        "\n" +
        "trust_delta: 5\n" +
        "suspicion_delta: -2\n" +
        "new_facts:\n" +
        "- heard shouting near the chapel\n" +
        "- saw a lantern after midnight\n" +
        "memory_summary: talked with Bryn about the fire\n" +
        "relationship_note: seems honest";

    // ------------------------------------------------------------ success path

    [Fact]
    public void Success_parses_structured_output()
    {
        var handler = new FakeHandler { Responder = _ => Ok(StructuredContent) };
        var result = WithResponder(handler).Complete(SampleRequest());

        Assert.False(result.Refused);
        Assert.False(result.UsedFallback, "a healthy Ollama must not flag fallback");
        Assert.Equal("I saw a lantern near the chapel, I swear it.", result.Output!.ReplyText);
        Assert.Equal(5, result.Output.TrustDelta);
        Assert.Equal(-2, result.Output.SuspicionDelta);
        Assert.SequenceEqual(
            new[] { "heard shouting near the chapel", "saw a lantern after midnight" },
            result.Output.NewFacts);
        Assert.Equal("talked with Bryn about the fire", result.Output.NewMemorySummary);
        Assert.Equal("seems honest", result.Output.RelationshipNote);
    }

    [Fact]
    public void Plain_prose_without_fields_uses_whole_text_as_reply()
    {
        var handler = new FakeHandler { Responder = _ => Ok("Just some words, friend.") };
        var result = WithResponder(handler).Complete(SampleRequest());

        Assert.False(result.Refused);
        Assert.False(result.UsedFallback);
        Assert.Equal("Just some words, friend.", result.Output!.ReplyText);
        Assert.Equal(0, result.Output.TrustDelta);
        Assert.Equal(0, result.Output.SuspicionDelta);
        Assert.Equal(0, result.Output.NewFacts.Count);
    }

    [Fact]
    public void Null_request_throws_like_the_mock()
    {
        var handler = new FakeHandler();
        Assert.Throws<ArgumentNullException>(() => WithResponder(handler).Complete(null!));
    }

    // ------------------------------------------------------------ fallback path

    private static void AssertMockFallback(AIResponse result, AIRequest request, ulong seed = 7)
    {
        var expected = new MockAIProvider(seed).Complete(request);
        Assert.False(result.Refused, "fallback must produce a usable response, not a refusal");
        Assert.True(result.UsedFallback, "fallback must set UsedFallback");
        Assert.Equal(expected.Output!.ReplyText, result.Output!.ReplyText);
        Assert.Equal(expected.Output.TrustDelta, result.Output.TrustDelta);
        Assert.Equal(expected.Output.SuspicionDelta, result.Output.SuspicionDelta);
        Assert.SequenceEqual(expected.Output.NewFacts, result.Output.NewFacts);
        Assert.Equal(expected.Output.NewMemorySummary, result.Output.NewMemorySummary);
    }

    [Fact]
    public void Connection_refused_falls_back_byte_identical_to_mock()
    {
        var handler = new FakeHandler
        {
            Responder = _ => throw new HttpRequestException("Connection refused"),
        };
        var request = SampleRequest();
        AssertMockFallback(WithResponder(handler).Complete(request), request);
    }

    [Fact]
    public void Timeout_falls_back()
    {
        var handler = new FakeHandler
        {
            Responder = _ => throw new TaskCanceledException("The request timed out"),
        };
        var request = SampleRequest();
        AssertMockFallback(WithResponder(handler).Complete(request), request);
    }

    [Fact]
    public void Http_500_falls_back()
    {
        var handler = new FakeHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        var request = SampleRequest();
        AssertMockFallback(WithResponder(handler).Complete(request), request);
    }

    [Fact]
    public void Malformed_json_falls_back()
    {
        var handler = new FakeHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("this is not json{{{"),
            },
        };
        var request = SampleRequest();
        AssertMockFallback(WithResponder(handler).Complete(request), request);
    }

    [Fact]
    public void Empty_content_falls_back()
    {
        var handler = new FakeHandler { Responder = _ => Ok("   \n  ") };
        var request = SampleRequest();
        AssertMockFallback(WithResponder(handler).Complete(request), request);
    }

    // ------------------------------------------------------------ config & parity

    [Fact]
    public void Options_have_documented_defaults()
    {
        var o = new OllamaOptions();
        Assert.Equal("http://localhost:11434", o.Endpoint);
        Assert.Equal("llama3.1:8b", o.Model);
        Assert.Equal(60, o.TimeoutSeconds);
        Assert.Equal(0.7, o.Temperature);
    }

    [Fact]
    public void Outgoing_prompt_matches_request_verbatim_and_leaks_nothing()
    {
        var handler = new FakeHandler { Responder = _ => Ok("Fine.") };
        var request = SampleRequest();
        WithResponder(handler).Complete(request);

        Assert.True(handler.LastRequest!.RequestUri!.ToString().EndsWith("/api/chat"),
            "must POST to /api/chat, got: " + handler.LastRequest.RequestUri);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        Assert.Equal("llama3.1:8b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean(), "streaming must be off");
        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        var sent = messages[0].GetProperty("content").GetString();
        Assert.Equal(request.ContextPrompt, sent,
            "the provider must send EXACTLY the prompt the pipeline built — no additions");
        Assert.False(sent!.Contains("malvr", StringComparison.OrdinalIgnoreCase),
            "outgoing prompt leaks a role word");
        Assert.False(sent.Contains("lumiel", StringComparison.OrdinalIgnoreCase),
            "outgoing prompt leaks a role word");
    }

    // ------------------------------------------------------------ pipeline integration

    [Fact]
    public void Orchestrator_validates_ollama_output_like_any_provider()
    {
        var s = TestSupport.NewPopulatedSession(11);
        var ids = s.State.World.Characters.Keys.Take(2).ToArray();
        var handler = new FakeHandler
        {
            Responder = _ => Ok(
                "I heard shouting that night, truly.\n" +
                "trust_delta: 4\n" +
                "suspicion_delta: 0\n" +
                "new_facts: heard shouting at night\n" +
                "memory_summary: talked for a while"),
        };
        var provider = WithResponder(handler);
        var engine = new ContextEngine(s.View, s.Cognition, s.Social, s.Relationships,
            s.Content, s.State.World.Profiles,
            id => s.State.World.Characters[id].CurrentLocationId);
        var orchestrator = new DialogueOrchestrator(engine, provider, new DialogueValidator(),
            s.Cognition, s.Social, s.Events,
            id => s.State.World.Characters[id].CurrentLocationId,
            () => new GameTime(s.State.TotalMinutes),
            new List<string>(), Difficulty.Medium, 99);

        var result = orchestrator.Exchange(ids[0], ids[1], "What did you hear that night?");

        Assert.True(result.Accepted, "well-formed Ollama output must pass the validator");
        Assert.False(result.UsedFallback);
        Assert.Equal("I heard shouting that night, truly.", result.ReplyText);
        Assert.Equal(4, result.TrustDelta);
    }
}
