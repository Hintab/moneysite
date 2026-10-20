using System.Globalization;
using System.Text.RegularExpressions;

namespace MoneySite.Services;

/// Turns a raw bank description into a company name, for transactions where Plaid
/// didn't supply merchant_name. E.g. "Amazon Mark* 272zw2103" → "Amazon Mark",
/// "ATT PAYMENT 062983001EPAYO 042126" → "Att Payment", "SQ *BLUE BOTTLE" → "Blue Bottle".
public static class MerchantNames
{
    private static readonly HashSet<string> States = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD",
        "MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC",
        "SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","DC",
    };

    private static readonly Regex LeadingNoise = new(
        @"^(?:web initiated payment at|early pay|pos|debit card purchase|debit purchase|purchase authorized on \S+|checkcard|check card|recurring|preauthorized|ach|dbt|pur)\b[\s:\-]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Clean(string description)
    {
        var s = description.Trim();
        for (var i = 0; i < 3; i++)
        {
            var next = LeadingNoise.Replace(s, "");
            if (next == s) break;
            s = next;
        }

        // Payment-processor prefixes: "SQ *", "TST* ", "PP*", "GLF*", "Pnm*".
        s = Regex.Replace(s, @"^[A-Za-z]{2,4}\s?\*\s?", "");
        // "Name* reference" → "Name"
        var star = s.IndexOf('*');
        if (star > 0) s = s[..star];

        var words = new List<string>();
        foreach (var raw in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim(',', '.', '-', '#', ':');
            if (w.Length == 0) continue;
            var digits = w.Count(char.IsDigit);
            if (char.IsDigit(w[0]) || digits >= 3 || w.StartsWith("XXX", StringComparison.OrdinalIgnoreCase)) continue;
            w = Regex.Replace(w, @"[-#]\d+$", "");          // "CENTER-165" → "CENTER"
            if (w.Length > 0) words.Add(w);
        }
        // Drop a trailing state code ("… WESTFIELD IN").
        if (words.Count > 1 && words[^1].Length == 2 && States.Contains(words[^1])) words.RemoveAt(words.Count - 1);

        var name = string.Join(" ", words.Take(4)).Trim();
        if (name.Length == 0) name = description.Trim();

        // ALL CAPS or all lowercase → Title Case; mixed case is left as the bank wrote it.
        if (name == name.ToUpperInvariant() || name == name.ToLowerInvariant())
            name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        else if (char.IsLower(name[0]))
            name = char.ToUpperInvariant(name[0]) + name[1..];
        return name.Length > 60 ? name[..60] : name;
    }
}
