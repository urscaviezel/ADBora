using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace AdbTool.Core;

/// <summary>
/// Localisation. German and English are built in (every text is written as a DE/EN pair in the code).
/// Further languages are JSON files (lang\&lt;code&gt;.json) that translate the English text:
/// { "_meta": { "code": "fr", "name": "Français", "version": "1.5.3" }, "Ready": "Prêt", "Checking {0}": "Vérification de {0}" }.
/// Interpolated texts use {0}, {1} … for their values. A missing text falls back to English.
/// Static UI texts are bound once and re-applied automatically whenever the language changes.
/// </summary>
internal static class Loc
{
    private static readonly List<(WeakReference<object> Target, Action<string> Apply, string De, string En)> Bindings = new();
    private static Dictionary<string, string> _table = new(StringComparer.Ordinal);

    /// <summary>Active language code ("de", "en", or the code of a language file).</summary>
    public static string Language { get; private set; } = "de";
    public static bool IsEnglish => Language == "en";
    public static bool IsGerman => Language == "de";

    public static event Action? LanguageChanged;

    public static string T(string de, string en) => Language switch
    {
        "de" => de,
        "en" => en,
        _ => _table.TryGetValue(en, out string? text) && text.Length > 0 ? text : en
    };

    /// <summary>Interpolated texts: other languages translate the English format ("Checking {0}") and insert the values.</summary>
    public static string T(LocText de, LocText en)
    {
        if (Language == "de") return de.ToString();
        if (Language == "en") return en.ToString();
        if (_table.TryGetValue(en.Format, out string? format) && format.Length > 0)
        {
            try { return string.Format(CultureInfo.CurrentCulture, format, en.Args); }
            catch (FormatException) { }
        }
        return en.ToString();
    }

    public static void SetLanguage(string language)
    {
        string code = (language ?? "").Trim().ToLowerInvariant();
        if (code is "english") code = "en";
        if (code is "" or "deutsch") code = "de";
        Dictionary<string, string> table = new(StringComparer.Ordinal);
        if (code is not ("de" or "en"))
        {
            LanguageFile? file = Languages.Load(code);
            if (file is null) code = "en";
            else table = file.Texts;
        }
        if (code == Language && code is "de" or "en")
            return;

        Language = code;
        _table = table;
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

/// <summary>
/// Captures an interpolated string as composite format ("Checking {0}") plus values, so that language
/// files can translate texts that contain values. Chosen by the compiler instead of string for $"…".
/// </summary>
[InterpolatedStringHandler]
internal struct LocText
{
    private readonly StringBuilder _format;
    private readonly List<object?> _args;

    public LocText(int literalLength, int formattedCount)
    {
        _format = new StringBuilder(literalLength + formattedCount * 4);
        _args = new List<object?>(formattedCount);
    }

    public void AppendLiteral(string value) => _format.Append(value.Replace("{", "{{").Replace("}", "}}"));

    public void AppendFormatted<T>(T value) => Hole(value, null, null);
    public void AppendFormatted<T>(T value, string? format) => Hole(value, null, format);
    public void AppendFormatted<T>(T value, int alignment) => Hole(value, alignment, null);
    public void AppendFormatted<T>(T value, int alignment, string? format) => Hole(value, alignment, format);

    private void Hole(object? value, int? alignment, string? format)
    {
        _format.Append('{').Append(_args.Count);
        if (alignment is int a) _format.Append(',').Append(a);
        if (!string.IsNullOrEmpty(format)) _format.Append(':').Append(format);
        _format.Append('}');
        _args.Add(value);
    }

    /// <summary>Composite format with {0}, {1} … (the key in language files).</summary>
    public string Format => _format?.ToString() ?? "";
    public object?[] Args => _args?.ToArray() ?? Array.Empty<object?>();

    public override string ToString()
    {
        try { return string.Format(CultureInfo.CurrentCulture, Format, Args); }
        catch (FormatException) { return Format; }
    }
}
