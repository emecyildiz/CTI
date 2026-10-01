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

    // A navigation hint, never a persisted completion/activation decision.
    internal static NextStep Next(bool n8nReady, bool profileDefined, bool bundledAdapter, long enabledSources, bool installerDatabasePrepared = false)
    {
        if (!n8nReady) return new("#n8n-check", "Connect n8n first",
            "Open n8n from the installer and create its owner account if needed. Then return here and refresh this page. No API key is sent by the connection check.");
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
