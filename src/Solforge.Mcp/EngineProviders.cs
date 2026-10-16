namespace Solforge.Mcp;

/**
 * Per-engine model providers for the AI workspace. Each preset only picks launch flags and environment for the
 * engine's own, unmodified CLI; logins always happen in that CLI's own sign-in flow (claude auth login, cline auth),
 * which Solforge only starts in the terminal: the browser sign-in and the saved credentials belong to that CLI.
 * and Solforge never reads, copies or stores tokens or API keys. Secrets stay in the user's environment.
 *
 * Supported combinations only:
 * - Claude Code: the user's Claude subscription login, their own ANTHROPIC_API_KEY, an Anthropic-format gateway, or
 *   ClinePass through Solforge's localhost translator (ClinePassProxy) with the user's own CLINE_API_KEY, which Cline
 *   documents for use from other clients. That run gets every Anthropic/Claude login variable removed, so no Claude
 *   subscription token ever reaches the translator.
 * - Cline: its saved provider, Cline's built-in "claude-code" provider (Cline runs the user's own claude binary,
 *   which signs in with their Claude subscription), or ClinePass through their Cline account.
 */
public static class EngineProviders
{
    /** @param Login The engine's own sign-in command, run in the terminal; empty when none is needed. */
    public sealed record Preset(string Id, string Engine, string Name, string Note, string Login = "",
        string DefaultModel = "", bool UsesModel = false, bool UsesBaseUrl = false);

    /** The non-secret choice saved in Studio settings for one engine. */
    public sealed class Choice
    {
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public string BaseUrl { get; set; } = "";
    }

    public const string ClaudeSubscription = "claude-subscription", ClaudeApiKey = "claude-api-key", ClaudeGateway = "claude-gateway",
        ClineSaved = "cline-saved", ClineClaudeCode = "cline-claude-code", ClinePass = "cline-pass", ClaudeClinePass = "claude-cline-pass";

    /** Credentials that would make claude bill an API key instead of the subscription login. */
    private static readonly string[] AnthropicCredentials = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"];

    public static readonly IReadOnlyList<Preset> Presets =
    [
        new(ClaudeSubscription, "claude", "Claude subscription (claude auth login)",
            "Uses your Pro/Max login. Solforge hides ANTHROPIC_API_KEY/AUTH_TOKEN/BASE_URL from this run so the subscription is used.",
            "claude auth login"),
        new(ClaudeApiKey, "claude", "Anthropic API key (your environment)",
            "Uses ANTHROPIC_API_KEY from your own environment, billed per token to your Console account. Solforge stores no key."),
        new(ClaudeGateway, "claude", "Anthropic-format LLM gateway",
            "Sets ANTHROPIC_BASE_URL; put the gateway credential in ANTHROPIC_AUTH_TOKEN in your own environment. Claude models only.",
            UsesBaseUrl: true),
        new(ClaudeClinePass, "claude", "Claude Code on ClinePass (CLINE_API_KEY)",
            "Runs Claude Code on open-weight ClinePass models through Solforge's localhost translator. Needs CLINE_API_KEY (app.cline.bot > Settings > API Keys) " +
            "in your own environment; your Claude login is removed from this run. Model: a cline-pass/ id; /model in Claude Code takes any cline-pass/ id.",
            "", ClinePassProxy.DefaultModel, UsesModel: true),
        new(ClineSaved, "cline", "Cline's saved provider (cline auth)",
            "Whatever cline auth last configured.", "cline auth"),
        new(ClineClaudeCode, "cline", "Cline using Claude Code subscription",
            "Cline's built-in claude-code provider: Cline runs your own claude CLI, which uses your Claude Pro/Max login. Sign in once with claude auth login.",
            "claude auth login", "sonnet", UsesModel: true),
        new(ClinePass, "cline", "ClinePass (Cline account)",
            "Open-weight models from your ClinePass subscription. Sign in with your Cline account (cline auth) or set CLINE_API_KEY in your environment.",
            "cline auth", "", UsesModel: true)
    ];

    public static IEnumerable<Preset> For(string engine) => Presets.Where(preset => preset.Engine == engine);

    /** The saved preset for the engine, else its first; null for engines without presets (agy, custom). */
    public static Preset? Resolve(string engine, string? id) =>
        For(engine).FirstOrDefault(preset => preset.Id == id) ?? For(engine).FirstOrDefault();

    /** Launch flags for the preset, joined like EngineLaunch arguments. */
    public static string Arguments(Preset? preset, Choice? choice)
    {
        if (preset is null) return "";
        string model = Model(preset, choice);
        return preset.Id switch
        {
            ClineClaudeCode => EngineLaunch.JoinArguments("-P claude-code", model.Length > 0 ? "-m " + EngineLaunch.Quote(model) : ""),
            ClinePass => EngineLaunch.JoinArguments("-P cline-pass", model.Length > 0 ? "-m " + EngineLaunch.Quote(model) : ""),
            _ => ""
        };
    }

    /** Environment overrides for the run; an empty value removes the variable from the engine's environment. */
    public static IReadOnlyDictionary<string, string> Environment(Preset? preset, Choice? choice)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (preset?.Id)
        {
            // Cline's claude-code provider starts claude with Cline's environment, so the same rule keeps it on the subscription.
            case ClaudeSubscription or ClineClaudeCode:
                foreach (string name in AnthropicCredentials) env[name] = "";
                break;
            // The editor adds the translator's base URL and local token (ClinePassProxy.ClaudeEnvironment); until then no login is passed.
            case ClaudeClinePass:
                foreach (string name in AnthropicCredentials) env[name] = "";
                env["CLAUDE_CODE_OAUTH_TOKEN"] = "";
                break;
            case ClaudeGateway when (choice?.BaseUrl.Trim() ?? "") is { Length: > 0 } url:
                env["ANTHROPIC_BASE_URL"] = url;
                break;
        }
        return env;
    }

    public static string Model(Preset preset, Choice? choice) =>
        !preset.UsesModel ? "" : choice?.Model.Trim() is { Length: > 0 } model ? model : preset.DefaultModel;
}
