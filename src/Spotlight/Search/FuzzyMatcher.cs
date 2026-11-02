namespace Spotlight.Search;

/// <summary>
/// Scores how well a query matches a name. Higher is better, -1 means no match.
///   1000      exact name
///   ~900      name starts with the query            ("chr"  → "Chrome")
///   ~850/720  first letters of words                ("vsc"  → "Visual Studio Code")
///   ~760      a word starts with the query          ("code" → "Visual Studio Code")
///   ~560      query appears somewhere in the name   ("rome" → "Chrome")
///   100–325   letters appear in order with gaps     ("gglchr" → "Google Chrome")
/// Shorter names win ties, so "Paint" beats "Paint 3D Projects Backup".
/// Matching ignores case and Hebrew final letter forms (typing "חשבון" finds "חשבונית").
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>Lower-case and fold Hebrew final forms (ך ם ן ף ץ) to their regular letters.</summary>
    public static char Fold(char c) => c switch
    {
        'ך' => 'כ',
        'ם' => 'מ',
        'ן' => 'נ',
        'ף' => 'פ',
        'ץ' => 'צ',
        _ => char.ToLowerInvariant(c),
    };

    /// <summary>Turns typed text into the form <see cref="Score"/> expects.</summary>
    public static string NormalizeQuery(string text)
    {
        var chars = text.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++) chars[i] = Fold(chars[i]);
        return new string(chars);
    }

    /// <param name="query">Already passed through <see cref="NormalizeQuery"/>, non-empty.</param>
    public static int Score(string query, string name)
    {
        int m = query.Length, n = name.Length;
        if (m == 0 || m > n) return -1;
        if (!IsSubsequence(query, name)) return -1; // cheap rejection for most items

        int lengthPenalty = Math.Min(60, n - m);
        int index = IndexOf(name, query, 0);
        if (index == 0) return m == n ? 1000 : 900 - lengthPenalty;

        int acronym = AcronymScore(query, name);
        if (acronym > 0) return acronym - lengthPenalty / 2;

        if (index > 0)
        {
            for (int i = index; i >= 0; i = IndexOf(name, query, i + 1))
            {
                if (IsWordStart(name, i)) return 760 - Math.Min(40, i) - lengthPenalty;
            }
            return 560 - Math.Min(40, index) - lengthPenalty;
        }

        return Math.Max(1, Subsequence(query, name) - lengthPenalty);
    }

    private static bool IsSubsequence(string query, string name)
    {
        int j = 0;
        for (int i = 0; i < name.Length && j < query.Length; i++)
        {
            if (Fold(name[i]) == query[j]) j++;
        }
        return j == query.Length;
    }

    private static int IndexOf(string name, string query, int start)
    {
        int last = name.Length - query.Length;
        for (int i = start; i <= last; i++)
        {
            int k = 0;
            while (k < query.Length && Fold(name[i + k]) == query[k]) k++;
            if (k == query.Length) return i;
        }
        return -1;
    }

    public static bool IsWordStart(string name, int i)
    {
        if (i == 0) return true;
        char c = name[i], prev = name[i - 1];
        if (!char.IsLetterOrDigit(c)) return false;
        if (!char.IsLetterOrDigit(prev)) return true;
        if (char.IsUpper(c) && char.IsLower(prev)) return true;  // camelCase
        return char.IsDigit(c) != char.IsDigit(prev);           // "Office365", "3D"
    }

    private static int AcronymScore(string query, string name)
    {
        if (query.Length < 2) return -1;
        int j = 0, words = 0, skipped = 0;
        for (int i = 0; i < name.Length; i++)
        {
            if (!IsWordStart(name, i)) continue;
            words++;
            if (j < query.Length && Fold(name[i]) == query[j]) j++;
            else if (j < query.Length) skipped++;
        }
        if (j < query.Length) return -1;
        return skipped == 0 && words == query.Length ? 850 : 720 - Math.Min(60, skipped * 15);
    }

    private static int Subsequence(string query, string name)
    {
        int score = 0, j = 0, last = -2, first = -1;
        for (int i = 0; i < name.Length && j < query.Length; i++)
        {
            if (Fold(name[i]) != query[j]) continue;
            int s = 10;
            if (IsWordStart(name, i)) s += 20;
            if (i == last + 1) s += 15;
            score += s;
            if (first < 0) first = i;
            last = i;
            j++;
        }
        if (j < query.Length) return -1;
        return 100 + score * 5 / query.Length - Math.Min(30, first);
    }
}
