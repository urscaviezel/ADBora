
namespace AdbTool.Core;

/// <summary>
/// Tiny two-language localisation. Static UI texts are bound once and are
/// re-applied automatically whenever the language changes. Runtime messages
/// (log lines, status texts) are translated at the moment they are created.
/// </summary>
internal static class Loc
{
    private static readonly List<(WeakReference<object> Target, Action<string> Apply, string De, string En)> Bindings = new();

    public static string Language { get; private set; } = "de";
    public static bool IsEnglish => Language == "en";

    public static event Action? LanguageChanged;

    public static string T(string de, string en) => IsEnglish ? en : de;

    public static void SetLanguage(string language)
    {
        string normalized = language is "en" or "English" ? "en" : "de";
        if (normalized == Language)
            return;

        Language = normalized;
        ApplyAll();
        LanguageChanged?.Invoke();
    }

    /// <summary>Binds Control.Text.</summary>
    public static TControl Bind<TControl>(TControl control, string de, string en) where TControl : Control
    {
        Bind(control, value => control.Text = value, de, en);
        return control;
    }

    /// <summary>Binds any setter (placeholder texts, column headers, tooltips …).</summary>
    public static void Bind(object owner, Action<string> apply, string de, string en)
    {
        Bindings.Add((new WeakReference<object>(owner), apply, de, en));
        apply(T(de, en));
    }

    private static void ApplyAll()
    {
        Bindings.RemoveAll(b => !b.Target.TryGetTarget(out _));
        foreach (var binding in Bindings)
        {
            try { binding.Apply(T(binding.De, binding.En)); }
            catch { /* control may be disposed */ }
        }
    }
}
