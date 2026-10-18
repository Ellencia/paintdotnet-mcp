using System.Globalization;

namespace PaintDotNetMcp.Bridge;

// User-facing editor text follows the Windows display language: Korean, Italian, otherwise English.
// Italian strings from PR #2 (1SiDPlayer).
internal static class EditorStrings
{
    public static string L(string korean, string english, string italian) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "ko" => korean,
            "it" => italian,
            _ => english,
        };

    public static string EditorTitle => L("MCP 텍스트 편집", "MCP Text Editor", "Editor di testo MCP");
}
