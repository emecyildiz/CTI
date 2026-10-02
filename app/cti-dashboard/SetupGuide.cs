internal static class SetupGuide
{
    internal static readonly string[] Steps = ["connection", "ai", "sources", "telegram", "review"];
    internal static readonly string[] Titles = ["Connection", "AI", "News sources", "Telegram (optional)", "Review & start"];

    // Navigation only. A selected step is never evidence of completed setup.
    internal static string SelectStep(string? requested, string? result)
    {
        if (requested is not null && Steps.Contains(requested)) return requested;
        return result switch
        {
            "saved" or "credential_created" or "credential_updated" or "workflows_mapped" or "workflows_already_mapped" => "ai",
            "sources_saved" => "sources",
            "telegram_credential_created" or "telegram_credential_updated" or "telegram_workflows_mapped" or "telegram_workflows_already_mapped" => "telegram",
            _ => "connection"
        };
    }

    internal static string StepForTarget(string target) => target switch
    {
        "#ai-setup" => "ai",
        "#source-setup" => "sources",
        "#telegram-setup" => "telegram",
        "#readiness-setup" => "review",
        _ => "connection"
    };

    internal sealed record NextStep(string Target, string Title, string Detail);

    internal static bool HasInstallerDatabaseRecord(string? authMode, string? managed, string? recorded) =>
        authMode == "local" && managed == "true" && recorded == "true";

    // UI capability only, not proof of ownership, valid keys or completed setup.
    internal static bool HasWindowsGuidedConnections(string? authMode, string? managed, string? recorded, string? capability) =>
        HasInstallerDatabaseRecord(authMode, managed, recorded) && capability == "v1";

    internal static string InstallerAction(string action) => action switch
    {
        "ai" => "<aside class=\"installer-action\" data-installer-action=\"ai\"><strong>Connect your key in the CTI setup app</strong><p>Keep this browser tab open. Return to the CTI setup window (or reopen the same CTI-Setup EXE), select the installation folder used for this dashboard, and choose <b>Connect AI</b>. Enter only your Gemini API key. No n8n API key is needed.</p><p>Then return here to choose your news sources. This prepares the included Gemini tasks but does not test the provider, start collection, or verify a different model selected in the profile.</p></aside>",
        "telegram" => "<aside class=\"installer-action\" data-installer-action=\"telegram\"><strong>Connect Telegram in the CTI setup app — or skip it</strong><p>Return to the CTI setup window (or reopen the same CTI-Setup EXE), select this installation folder, and choose <b>Connect Telegram (optional)</b>. Enter your bot token and numeric private user ID. No n8n API key is needed.</p><p>No message is sent and no task is started. A public HTTPS route is still needed for incoming bot questions; outbound notifications do not need one.</p></aside>",
        "review" => "<aside class=\"installer-action\" data-installer-action=\"review\"><strong>Check preparation in the CTI setup app</strong><p>Return to the CTI setup window (or reopen the same CTI-Setup EXE), select this installation folder, and choose <b>Check setup</b>. Include AI if you use it, and Telegram only if you configured it. No n8n API key is needed.</p><p>This checks saved bindings and reviewed disabled tasks, not live passwords/provider access or public HTTPS reachability. A prepared result does not start anything. Do not edit n8n while the check runs.</p></aside>",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    internal static string ConnectionPanel(string action, string manualHtml, bool windowsGuided) => windowsGuided
        ? InstallerAction(action) + "<details class=\"setup-advanced\" data-manual-fallback=\"" + action + "\"><summary>Advanced: use the manual n8n API connection instead</summary><p>Use this only for a manual or customized installation, or if the setup app cannot prepare the reviewed tasks. Create a private key in n8n Settings → n8n API with the permissions listed below. Do not delete data to bypass a refused preparation.</p>" + manualHtml + "</details>"
        : manualHtml;

    // A navigation hint, never a persisted completion/activation decision.
    internal static NextStep Next(bool n8nReady, bool profileDefined, bool bundledAdapter, long enabledSources, bool installerDatabasePrepared = false, bool windowsGuided = false)
    {
        if (!n8nReady) return new("#n8n-check", "Connect n8n first",
            "Open n8n from the installer and create its owner account if needed. Then return here and refresh this page. No API key is sent by the connection check.");
        if (windowsGuided)
        {
            if (enabledSources <= 0) return new("#source-setup", "Select at least one news source",
                "Choose news sources here. AI and Telegram are optional; prepare them in the CTI setup app only if needed. No saved AI profile is required to continue.");
            return new("#readiness-setup", "Review your setup before starting",
                "Use Check setup in the CTI setup app for a keyless preparation snapshot. Include only the optional connections you configured, then enable tasks deliberately in n8n. Incoming Telegram queries still need a reachable public HTTPS route.");
        }
        if (!profileDefined) return new("#ai-setup", "Choose your AI provider and model",
            "Save the provider profile first, then enter the provider API key in the credential form below it. Saving a profile alone does not test the API key.");
        if (!bundledAdapter) return new("#ai-setup", "Your AI provider needs a compatible workflow adapter",
            "The bundled automatic credential setup supports Gemini. Other providers require manual workflow changes; a saved profile does not make them ready to run.");
        if (enabledSources <= 0) return new("#source-setup", "Select at least one news source",
            "Choose from the reviewed catalog. Collection only starts after the required workflows are configured and explicitly activated.");
        if (installerDatabasePrepared) return new("#readiness-setup", "Review your setup before starting",
            "The installer recorded automatic database setup. Confirm your AI connection and run the final check; it verifies the current task configuration and does not start collection.");
        return new("#database-setup", "Review credentials, then run the readiness check",
            "A provider profile and selected sources are present. AI and database credential mapping still need verification. Telegram is optional. The final check does not activate workflows.");
    }
}
