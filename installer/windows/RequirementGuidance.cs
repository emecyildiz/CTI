namespace CtiInstaller;

internal enum RequirementStage { Command, Compose, Engine }

internal static class RequirementGuidance
{
    // Failed access is not proof that Docker is absent or that the engine is stopped.
    internal static string Failure(RequirementStage stage) => stage switch
    {
        RequirementStage.Command => "Docker could not be found or started. If you just installed Docker Desktop, reopen this installer. Otherwise install Docker Desktop first.",
        RequirementStage.Compose => "Docker was found, but its Compose component is unavailable. Complete or repair your Docker Desktop installation, then check again.",
        RequirementStage.Engine => "Docker was found, but CTI cannot reach its engine. Open Docker Desktop and wait until it is running, then check again. If it still fails, review Docker's error message (including virtualization or access problems).",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    internal static string NextStep(bool managed) => managed
        ? "Services installed. Next: create your local n8n account if this is a new installation. Keep this window open: use Connect AI and optional Connect Telegram here, choose news sources with Open setup, then use Check setup here. These easy actions require the recorded managed database bootstrap; older/manual installations keep the browser forms. Installation does not activate collection; existing tasks keep their current state."
        : "CTI installed. Next: open setup and connect your existing n8n service. External n8n was not changed. Installation does not activate collection; existing tasks keep their current state.";
}
