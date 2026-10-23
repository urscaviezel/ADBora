// LangExtract – collects every English UI text of ADBora (the "en" half of each DE/EN pair in the source)
// and writes lang/template.json for translators. Usage: dotnet run -- <src folder> <output json> <version>
// Pairs are recognised by parameter names: xEn next to xDe (de/en, De/En, dDe/dEn, catDe/catEn, HintDe/HintEn …),
// for method/constructor calls, target-typed new(...) and tuples typed (string De, string En, …).
// Interpolated strings become composite formats ("Checking {0}") exactly like ADBora's LocText handler.
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string src = args.Length > 0 ? args[0] : "src/AdbTool";
string output = args.Length > 1 ? args[1] : "lang/template.json";
string version = args.Length > 2 ? args[2] : "";

var trees = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
    .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f, options: new CSharpParseOptions(LanguageVersion.Latest)))
    .ToList();

// 1) Parameter lists of all methods, constructors, records and local functions, by name
var signatures = new Dictionary<string, List<string[]>>();
void AddSig(string name, ParameterListSyntax? list)
{
    if (list is null) return;
    if (!signatures.TryGetValue(name, out var l)) signatures[name] = l = new();
    l.Add(list.Parameters.Select(p => p.Identifier.Text).ToArray());
}
foreach (var root in trees.Select(t => t.GetRoot()))
{
    foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>()) AddSig(m.Identifier.Text, m.ParameterList);
    foreach (var c in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()) AddSig(c.Identifier.Text, c.ParameterList);
    foreach (var r in root.DescendantNodes().OfType<TypeDeclarationSyntax>()) AddSig(r.Identifier.Text, r.ParameterList);
    foreach (var f in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>()) AddSig(f.Identifier.Text, f.ParameterList);
}

// Pairs (index of De, index of En) in a parameter list
static List<(int De, int En)> Pairs(string[] names)
{
    var result = new List<(int, int)>();
    for (int i = 0; i < names.Length; i++)
    {
        string n = names[i];
        if (!(n.EndsWith("En") || n.EndsWith("en")) || n.Length < 2) continue;
        string prefix = n[..^2];
        int de = Array.FindIndex(names, x => x == prefix + "De" || x == prefix + "de");
        if (de >= 0) result.Add((de, i));
    }
    return result;
}

var texts = new SortedDictionary<string, string>(StringComparer.Ordinal);   // en -> de (context)
int skipped = 0;

// Interpolated / concatenated forms: returns (isInterpolated, keyText)
(bool Ok, bool Interp, string Key) Key(ExpressionSyntax e)
{
    switch (e)
    {
        case LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression):
            return (true, false, l.Token.ValueText);
        case ParenthesizedExpressionSyntax p:
            return Key(p.Expression);
        case InterpolatedStringExpressionSyntax s:
        {
            var sb = new StringBuilder();
            int hole = 0;
            foreach (var part in s.Contents)
            {
                if (part is InterpolatedStringTextSyntax t)
                    sb.Append(t.TextToken.ValueText.Replace("{", "{{").Replace("}", "}}"));
                else if (part is InterpolationSyntax i)
                {
                    sb.Append('{').Append(hole++);
                    if (i.AlignmentClause is { } a) sb.Append(',').Append(a.Value.ToString().Trim());
                    if (i.FormatClause is { } f) sb.Append(':').Append(f.FormatStringToken.ValueText);
                    sb.Append('}');
                }
            }
            return (true, true, sb.ToString());
        }
        case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression):
        {
            var left = Key(b.Left);
            var right = Key(b.Right);
            if (!left.Ok || !right.Ok) return (false, false, "");
            if (left.Interp != right.Interp) return (false, false, "");   // mixed: compiler formats to a plain string
            if (!left.Interp) return (true, false, left.Key + right.Key);
            // interpolated + interpolated: renumber the holes of the right part
            int offset = CountHoles(b.Left);
            return (true, true, left.Key + Renumber(right.Key, offset));
        }
        default:
            return (false, false, "");
    }
}
int CountHoles(ExpressionSyntax e) => e.DescendantNodesAndSelf().OfType<InterpolationSyntax>().Count(i => i.Ancestors().OfType<InterpolatedStringExpressionSyntax>().First().Ancestors().OfType<InterpolationSyntax>().Count() == 0);
static string Renumber(string format, int offset)
{
    var sb = new StringBuilder();
    for (int i = 0; i < format.Length; i++)
    {
        char c = format[i];
        if (c == '{' && i + 1 < format.Length && format[i + 1] == '{') { sb.Append("{{"); i++; continue; }
        if (c == '}' && i + 1 < format.Length && format[i + 1] == '}') { sb.Append("}}"); i++; continue; }
        if (c == '{')
        {
            int j = i + 1;
            while (j < format.Length && char.IsDigit(format[j])) j++;
            sb.Append('{').Append(int.Parse(format[(i + 1)..j]) + offset);
            i = j - 1;
            continue;
        }
        sb.Append(c);
    }
    return sb.ToString();
}

void AddPair(ExpressionSyntax de, ExpressionSyntax en)
{
    var k = Key(en);
    var d = Key(de);
    if (!k.Ok) { skipped++; return; }
    string key;
    if (k.Interp && d.Ok && d.Interp) key = k.Key;                 // T(LocText, LocText): composite format
    else if (k.Interp && !k.Key.Replace("{{", "").Contains('{')) key = UnescapeIfNoHoles(k.Key);   // constant
    else if (k.Interp) { skipped++; return; }                       // formatted at runtime, cannot be a key
    else key = k.Key;
    if (key.Trim().Length == 0) return;
    texts[key] = d.Ok ? (d.Interp ? d.Key : d.Key) : "";
}
static string UnescapeIfNoHoles(string format) => format.Replace("{{", "{").Replace("}}", "}");

void HandleArgs(SeparatedSyntaxList<ArgumentSyntax> arguments, string name)
{
    if (!signatures.TryGetValue(name, out var sigs)) return;
    foreach (string[] sig in sigs.Where(s => s.Length >= arguments.Count))
    {
        var pairs = Pairs(sig);
        if (pairs.Count == 0) continue;
        // map arguments to parameter positions (named arguments supported)
        var map = new Dictionary<int, ExpressionSyntax>();
        for (int i = 0; i < arguments.Count; i++)
        {
            var a = arguments[i];
            int idx = a.NameColon is { } nc ? Array.IndexOf(sig, nc.Name.Identifier.Text) : i;
            if (idx >= 0) map[idx] = a.Expression;
        }
        foreach (var (de, en) in pairs)
            if (map.TryGetValue(de, out var d) && map.TryGetValue(en, out var e))
                AddPair(d, e);
        return;
    }
}

static string? SimpleTypeName(TypeSyntax? t) => t switch
{
    ArrayTypeSyntax a => SimpleTypeName(a.ElementType),
    GenericNameSyntax g => g.TypeArgumentList.Arguments.Count == 1 ? SimpleTypeName(g.TypeArgumentList.Arguments[0]) : g.Identifier.Text,
    QualifiedNameSyntax q => SimpleTypeName(q.Right),
    IdentifierNameSyntax i => i.Identifier.Text,
    NullableTypeSyntax n => SimpleTypeName(n.ElementType),
    _ => null
};

foreach (var root in trees.Select(t => t.GetRoot()))
{
    foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        string? name = inv.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            IdentifierNameSyntax i => i.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            _ => null
        };
        if (name is not null) HandleArgs(inv.ArgumentList.Arguments, name);
    }
    foreach (var oc in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        if (SimpleTypeName(oc.Type) is { } t && oc.ArgumentList is { } al) HandleArgs(al.Arguments, t);
    foreach (var ic in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
    {
        TypeSyntax? target = ic.Ancestors().OfType<VariableDeclarationSyntax>().FirstOrDefault()?.Type
                             ?? ic.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Type;
        if (SimpleTypeName(target) is { } t) HandleArgs(ic.ArgumentList.Arguments, t);
    }
    // Tuples typed (string De, string En, …): fields, locals and properties
    foreach (var decl in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
    {
        TypeSyntax type = decl.Type is ArrayTypeSyntax arr ? arr.ElementType : decl.Type;
        if (type is GenericNameSyntax gen && gen.TypeArgumentList.Arguments.Count == 1) type = gen.TypeArgumentList.Arguments[0];
        if (type is not TupleTypeSyntax tuple) continue;
        string[] names = tuple.Elements.Select(e => e.Identifier.Text).ToArray();
        var pairs = Pairs(names);
        if (pairs.Count == 0) continue;
        foreach (var tx in decl.DescendantNodes().OfType<TupleExpressionSyntax>().Where(x => x.Arguments.Count == names.Length))
            foreach (var (de, en) in pairs) AddPair(tx.Arguments[de].Expression, tx.Arguments[en].Expression);
    }
    // Assignments to tuple fields: _status = ("Bereit", "Ready") etc.
    foreach (var tx in root.DescendantNodes().OfType<TupleExpressionSyntax>().Where(x => x.Parent is AssignmentExpressionSyntax && x.Arguments.Count == 2))
        AddPair(tx.Arguments[0].Expression, tx.Arguments[1].Expression);
}

var json = new JsonObject
{
    ["_meta"] = new JsonObject
    {
        ["code"] = "xx",
        ["name"] = "Language name (in that language)",
        ["version"] = version,
        ["app"] = "ADBora",
        ["translator"] = ""
    },
    ["_readme"] = "Translate the VALUES (right side) only; keep {0}, {1} … and \\n. Save as lang/<code>.json (e.g. fr.json). Missing or empty texts fall back to English."
};
foreach (var (en, _) in texts) json[en] = en;
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new UTF8Encoding(false));

// German reference (en -> de) next to the template, useful for translators who start from German
var reference = new JsonObject();
foreach (var (en, de) in texts) reference[en] = de;
File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "_reference-de.json"),
    reference.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new UTF8Encoding(false));

Console.WriteLine($"{texts.Count} texts -> {output} ({skipped} skipped: non-literal arguments)");
