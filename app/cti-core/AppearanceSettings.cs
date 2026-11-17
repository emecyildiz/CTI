namespace CtiCore;

internal sealed record AppearanceSettings(bool Visible, string Label)
{
    internal const int MaxLabelLength = 48;
    internal static AppearanceSettings Default { get; } = new(true, "Emecworks");

    internal static bool TryNormalize(string raw, out string label)
    {
        label = raw.Trim();
        return raw.Length <= MaxLabelLength && label.Length > 0 && !raw.Any(char.IsControl);
    }
}
