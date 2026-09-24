using System.Text.RegularExpressions;

namespace VoiceAgent.Tools;

/// <summary>
/// Okapi BM25 over the business's own articles, the retrieval half of the agent's RAG.
///
/// Lexical on purpose: a phone agent's corpus is a few dozen short articles, callers use the
/// same nouns the articles do ("oil change", "trade-in", "late fee"), and a local index adds
/// zero network latency to a turn. The seam is <see cref="Search"/>; a vector store can sit
/// behind it without touching the tool or the call loop.
/// </summary>
public sealed partial class KnowledgeIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly List<(KnowledgeArticle Article, Dictionary<string, int> Terms, int Length)> _docs;
    private readonly Dictionary<string, int> _documentFrequency = new();
    private readonly double _averageLength;

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "if", "in",
        "is", "it", "me", "my", "of", "on", "or", "the", "to", "what", "when", "with", "you", "your", "much", "we",
    ];

    public KnowledgeIndex(IEnumerable<KnowledgeArticle> articles)
    {
        _docs = articles.Select(a =>
        {
            var tokens = Tokenize(a.Title + " " + a.Text).ToList();
            return (a, tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count()), tokens.Count);
        }).ToList();

        foreach (var doc in _docs)
            foreach (var term in doc.Terms.Keys)
                _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;

        _averageLength = _docs.Count == 0 ? 0 : _docs.Average(d => d.Length);
    }

    public IReadOnlyList<(KnowledgeArticle Article, double Score)> Search(string query, int top = 3)
    {
        var terms = Tokenize(query).Distinct().ToList();
        if (terms.Count == 0 || _docs.Count == 0) return [];

        return _docs
            .Select(doc => (doc.Article, Score: terms.Sum(t => TermScore(t, doc.Terms, doc.Length))))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .Take(top)
            .ToList();
    }

    private double TermScore(string term, Dictionary<string, int> terms, int length)
    {
        if (!terms.TryGetValue(term, out int tf)) return 0;
        int n = _docs.Count, df = _documentFrequency[term];
        double idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
        return idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * length / _averageLength));
    }

    internal static IEnumerable<string> Tokenize(string text) =>
        WordPattern().Matches(text.ToLowerInvariant())
            .Select(m => Stem(m.Value))
            .Where(t => t.Length > 1 && !StopWords.Contains(t));

    /// <summary>Just enough stemming for plurals and -ing forms ("payments", "changing").</summary>
    private static string Stem(string word)
    {
        if (word.Length > 5 && word.EndsWith("ing")) return word[..^3];
        if (word.Length > 4 && word.EndsWith("ies")) return word[..^3] + "y";
        if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss")) return word[..^1];
        return word;
    }

    // Hyphenated words split into their parts: a caller says "trade", the article says "trade-ins".
    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordPattern();
}
