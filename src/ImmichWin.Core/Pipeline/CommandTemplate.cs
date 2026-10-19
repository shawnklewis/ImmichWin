using System.Text;

namespace ImmichWin.Pipeline;

/// <summary>Turns a user-configured command template into an executable + argument list without involving a shell.</summary>
public static class CommandTemplate
{
    /// <summary>Tokenises the template first (double quotes group words), then substitutes {input}/{output}
    /// into the tokens, so substituted values always stay a single argument and are never re-parsed.</summary>
    public static (string Exe, List<string> Args) Build(string template, string input, string output)
    {
        var tokens = Tokenize(template);
        if (tokens.Count == 0) throw new ArgumentException("Empty command template");
        var r = tokens.Select(t => t.Replace("{input}", input).Replace("{output}", output)).ToList();
        return (r[0], r.Skip(1).ToList());
    }

    static List<string> Tokenize(string s)
    {
        var tokens = new List<string>(); var cur = new StringBuilder(); var inQ = false; var has = false;
        foreach (var ch in s)
        {
            if (ch == '"') { inQ = !inQ; has = true; }
            else if (char.IsWhiteSpace(ch) && !inQ) { if (has) { tokens.Add(cur.ToString()); cur.Clear(); has = false; } }
            else { cur.Append(ch); has = true; }
        }
        if (has) tokens.Add(cur.ToString());
        return tokens;
    }
}
