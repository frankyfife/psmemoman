namespace MemcardManager.Core;

/// <summary>
/// Minimal UI localisation: every user visible text is written as L.T("Deutsch", "English").
/// The language is chosen once at start-up (setting or system language).
/// </summary>
public static class L
{
    /// <summary>true = English, false = German.</summary>
    public static bool En { get; set; }

    public static string T(string de, string en) => En ? en : de;

    /// <summary>"de" / "en" / "" (= system language).</summary>
    public static void Init(string? setting)
    {
        En = setting switch
        {
            "de" => false,
            "en" => true,
            _ => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "de"
        };
    }
}
