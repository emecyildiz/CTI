using System.Text.RegularExpressions;

namespace CtiInstaller;

internal static class TelegramConnection
{
    internal static bool ValidToken(string value) => value.Length <= 149 && Regex.IsMatch(value, "\\A[0-9]{5,20}:[A-Za-z0-9_-]{20,128}\\z", RegexOptions.CultureInvariant);
    internal static bool ValidPrivateChat(string value) => value.Length is >= 5 and <= 20 && value.All(c => c is >= '0' and <= '9');
    internal static bool Prepared(int exitCode, string output) => exitCode == 0 && output.Trim() == "CTI_MANAGED_TELEGRAM_RESULT:v1:prepared";
}
