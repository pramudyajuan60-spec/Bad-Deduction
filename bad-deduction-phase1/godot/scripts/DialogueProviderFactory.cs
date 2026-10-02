using BadDeduction.AI;

namespace BadDeduction.Godot;

/// <summary>Which dialogue backend a run uses. Mock is the deterministic default; Ollama is opt-in.</summary>
public enum DialogueProviderKind
{
    Mock,
    Ollama,
}

/// <summary>
/// Builds the dialogue provider for a run from the UI's settings. Every provider flows
/// through the same DialogueOrchestrator → DialogueValidator pipeline, so the
/// anti-hallucination guard applies identically; the Ollama provider additionally falls
/// back to the mock internally when the local server is unreachable (see
/// <see cref="OllamaDialogueProvider"/>), flagged on the result for the UI indicator.
/// </summary>
public static class DialogueProviderFactory
{
    public static IAIProvider Create(
        DialogueProviderKind kind, ulong seed, string model, string endpoint)
    {
        if (kind == DialogueProviderKind.Ollama)
        {
            var defaults = new OllamaOptions();
            return new OllamaDialogueProvider(
                new OllamaOptions
                {
                    Model = string.IsNullOrWhiteSpace(model) ? defaults.Model : model.Trim(),
                    Endpoint = string.IsNullOrWhiteSpace(endpoint) ? defaults.Endpoint : endpoint.Trim(),
                },
                new MockAIProvider(seed));
        }
        return new MockAIProvider(seed);
    }
}
