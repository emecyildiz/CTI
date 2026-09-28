internal static class SetupGuide
{
    internal sealed record NextStep(string Target, string Title, string Detail);

    // A navigation hint, never a persisted completion/activation decision.
    internal static NextStep Next(bool n8nReady, bool profileDefined, bool bundledAdapter, long enabledSources)
    {
        if (!n8nReady) return new("#n8n-check", "Connect n8n first",
            "Open n8n from the installer and create its owner account if needed. Then return here and refresh this page. No API key is sent by the connection check.");
        if (!profileDefined) return new("#ai-setup", "Choose your AI provider and model",
            "Save the provider profile first, then enter the provider API key in the credential form below it. Saving a profile alone does not test the API key.");
        if (!bundledAdapter) return new("#ai-setup", "Your AI provider needs a compatible workflow adapter",
            "The bundled automatic credential setup supports Gemini. Other providers require manual workflow changes; a saved profile does not make them ready to run.");
        if (enabledSources <= 0) return new("#source-setup", "Select at least one news source",
            "Choose from the reviewed catalog. Collection only starts after the required workflows are configured and explicitly activated.");
        return new("#database-setup", "Review credentials, then run the readiness check",
            "A provider profile and selected sources are present. AI and database credential mapping still need verification. Telegram is optional. The final check does not activate workflows.");
    }
}
