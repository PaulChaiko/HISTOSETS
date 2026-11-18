namespace HistOSets.Core;

/// <summary>A suggestion filter only. Approximate matches never establish answer identity.</summary>
public sealed class TrainingNameQuery
{
    private readonly string normalized;
    private readonly string[] words;

    public TrainingNameQuery(string text)
    {
        normalized = TrainingPlanner.Normalize(text);
        words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public bool Matches(string name)
    {
        if (words.Length == 0) return true;
        var candidate = TrainingPlanner.Normalize(name);
        if (candidate.Contains(normalized, StringComparison.Ordinal)) return true;

        // Each query word must find a distinct word in the label, in order. The
        // prefix allows incremental typing; the small edit allowance finds common
        // changes in word endings and typos without treating short words as fuzzy.
        var candidateWords = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > candidateWords.Length) return false;
        var index = 0;
        foreach (var word in words)
        {
            while (index < candidateWords.Length && !MatchesWord(word, candidateWords[index])) index++;
            if (index == candidateWords.Length) return false;
            index++;
        }
        return true;
    }

    private static bool MatchesWord(string query, string word)
    {
        if (word.StartsWith(query, StringComparison.Ordinal)) return true;
        var maxEdits = Math.Min(2, Math.Min(query.Length, word.Length) / 4);
        if (maxEdits == 0 || query.Length > 64 || word.Length > 64 || Math.Abs(query.Length - word.Length) > maxEdits)
            return false;

        Span<int> previous = stackalloc int[word.Length + 1];
        Span<int> current = stackalloc int[word.Length + 1];
        for (var j = 0; j <= word.Length; j++) previous[j] = j;
        for (var i = 1; i <= query.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= word.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j], current[j - 1]) + 1,
                    previous[j - 1] + (query[i - 1] == word[j - 1] ? 0 : 1));
            var row = previous;
            previous = current;
            current = row;
        }
        return previous[word.Length] <= maxEdits;
    }
}
