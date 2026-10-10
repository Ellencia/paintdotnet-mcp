using System.Globalization;

namespace PaintDotNetMcp.Bridge;

// The host UI follows Windows' configured UI language; English is the default for all others.
internal static class EditorStrings
{
    public static string L(string italian, string english) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("it", StringComparison.OrdinalIgnoreCase)
            ? italian
            : english;
}
