namespace Tereditor.Plugins.Providers;

/// <summary>A provider an agent documents: its id in the agent's files, a name and the variable its key is read from.</summary>
public sealed record CatalogProvider(string Id, string Name, string EnvVar);

/// <summary>
/// The providers each agent documents. Pi: https://github.com/badlogic/pi-mono (docs/providers.md, the API key table).
/// omp: https://omp.sh/docs/providers and https://omp.sh/docs/env. A provider missing here is added by hand.
/// </summary>
public static class ProviderCatalog
{
    /// <summary>Anthropic is offered by API key only (Claude Console): a Claude subscription cannot be used through a third-party agent.</summary>
    public const string AnthropicId = "anthropic";

    public const string AnthropicNote = "Use an API key from the Claude Console. A Claude subscription sign-in is not supported here.";

    public static readonly IReadOnlyList<CatalogProvider> Pi =
    [
        new("anthropic", "Anthropic", "ANTHROPIC_API_KEY"),
        new("openai", "OpenAI", "OPENAI_API_KEY"),
        new("google", "Google Gemini", "GEMINI_API_KEY"),
        new("azure-openai-responses", "Azure OpenAI Responses", "AZURE_OPENAI_API_KEY"),
        new("deepseek", "DeepSeek", "DEEPSEEK_API_KEY"),
        new("mistral", "Mistral", "MISTRAL_API_KEY"),
        new("groq", "Groq", "GROQ_API_KEY"),
        new("cerebras", "Cerebras", "CEREBRAS_API_KEY"),
        new("cloudflare-ai-gateway", "Cloudflare AI Gateway", "CLOUDFLARE_API_KEY"),
        new("cloudflare-workers-ai", "Cloudflare Workers AI", "CLOUDFLARE_API_KEY"),
        new("xai", "xAI", "XAI_API_KEY"),
        new("openrouter", "OpenRouter", "OPENROUTER_API_KEY"),
        new("vercel-ai-gateway", "Vercel AI Gateway", "AI_GATEWAY_API_KEY"),
        new("zai", "ZAI", "ZAI_API_KEY"),
        new("opencode", "OpenCode Zen", "OPENCODE_API_KEY"),
        new("opencode-go", "OpenCode Go", "OPENCODE_API_KEY"),
        new("huggingface", "Hugging Face", "HF_TOKEN"),
        new("fireworks", "Fireworks", "FIREWORKS_API_KEY"),
        new("kimi-coding", "Kimi For Coding", "KIMI_API_KEY"),
        new("minimax", "MiniMax", "MINIMAX_API_KEY"),
        new("minimax-cn", "MiniMax (China)", "MINIMAX_CN_API_KEY"),
        new("xiaomi", "Xiaomi MiMo", "XIAOMI_API_KEY"),
        new("xiaomi-token-plan-cn", "Xiaomi MiMo Token Plan (China)", "XIAOMI_TOKEN_PLAN_CN_API_KEY"),
        new("xiaomi-token-plan-ams", "Xiaomi MiMo Token Plan (Amsterdam)", "XIAOMI_TOKEN_PLAN_AMS_API_KEY"),
        new("xiaomi-token-plan-sgp", "Xiaomi MiMo Token Plan (Singapore)", "XIAOMI_TOKEN_PLAN_SGP_API_KEY")
    ];

    public static readonly IReadOnlyList<CatalogProvider> Omp =
    [
        new("anthropic", "Anthropic", "ANTHROPIC_API_KEY"),
        new("openai", "OpenAI", "OPENAI_API_KEY"),
        new("google", "Google Gemini", "GEMINI_API_KEY"),
        new("groq", "Groq", "GROQ_API_KEY"),
        new("mistral", "Mistral", "MISTRAL_API_KEY"),
        new("xai", "xAI", "XAI_API_KEY"),
        new("deepseek", "DeepSeek", "DEEPSEEK_API_KEY"),
        new("openrouter", "OpenRouter", "OPENROUTER_API_KEY"),
        new("cerebras", "Cerebras", "CEREBRAS_API_KEY"),
        new("fireworks", "Fireworks", "FIREWORKS_API_KEY"),
        new("huggingface", "Hugging Face", "HF_TOKEN"),
        new("together", "Together", "TOGETHER_API_KEY"),
        new("nvidia", "NVIDIA", "NVIDIA_API_KEY"),
        new("novita", "Novita", "NOVITA_API_KEY"),
        new("baseten", "Baseten", "BASETEN_API_KEY"),
        new("venice", "Venice", "VENICE_API_KEY"),
        new("moonshot", "Moonshot", "MOONSHOT_API_KEY"),
        new("kimi-code", "Kimi Code", "KIMI_API_KEY"),
        new("minimax", "MiniMax", "MINIMAX_API_KEY"),
        new("zai", "z.ai", "ZAI_API_KEY"),
        new("siliconflow", "SiliconFlow", "SILICONFLOW_API_KEY"),
        new("qianfan", "Qianfan", "QIANFAN_API_KEY"),
        new("litellm", "LiteLLM", "LITELLM_API_KEY"),
        new("vllm", "vLLM", "VLLM_API_KEY"),
        new("lm-studio", "LM Studio", "LM_STUDIO_API_KEY"),
        new("ollama", "Ollama", "OLLAMA_API_KEY"),
        new("ollama-cloud", "Ollama Cloud", "OLLAMA_CLOUD_API_KEY"),
        new("cloudflare-ai-gateway", "Cloudflare AI Gateway", "CLOUDFLARE_AI_GATEWAY_API_KEY"),
        new("vercel-ai-gateway", "Vercel AI Gateway", "AI_GATEWAY_API_KEY"),
        new("opencode-zen", "OpenCode Zen", "OPENCODE_API_KEY"),
        new("opencode-go", "OpenCode Go", "OPENCODE_API_KEY"),
        new("zenmux", "ZenMux", "ZENMUX_API_KEY"),
        new("kilo", "Kilo Gateway", "KILO_API_KEY")
    ];

    public static IReadOnlyList<CatalogProvider> For(ProviderAgent agent) => agent == ProviderAgent.Pi ? Pi : Omp;

    /// <summary>A variable name is upper case letters, digits and underscores, not starting with a digit.</summary>
    public static bool IsEnvVar(string name) =>
        name.Length is > 0 and <= 128 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');

    /// <summary>A provider id is lower case letters, digits, dots, dashes and underscores.</summary>
    public static bool IsProviderId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.');
}