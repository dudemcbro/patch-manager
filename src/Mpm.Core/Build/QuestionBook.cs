namespace Mpm.Core.Build;

/// <summary>
/// Collects the questions a build raises. <see cref="Choose"/> returns the user's answer when it is one of
/// the valid options, else the default; the wording is attached later (when counts are known) with
/// <see cref="Describe"/>.
/// </summary>
internal sealed class QuestionBook
{
    private readonly IReadOnlyDictionary<string, string> _answers;
    private readonly Dictionary<string, (string Default, string Chosen, IReadOnlyList<string> Valid)> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Question> _described = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public QuestionBook(IReadOnlyDictionary<string, string>? answers)
    {
        _answers = answers ?? new Dictionary<string, string>();
    }

    public string Choose(string id, IReadOnlyList<string> valid, string @default)
    {
        if (_pending.TryGetValue(id, out var existing))
        {
            return existing.Chosen;
        }
        var chosen = _answers.TryGetValue(id, out var answer) && valid.Contains(answer) ? answer : @default;
        _pending[id] = (@default, chosen, valid);
        _order.Add(id);
        return chosen;
    }

    public bool IsAnswered(string id) => _answers.TryGetValue(id, out var a) && _pending.TryGetValue(id, out var p) && p.Valid.Contains(a);

    public void Describe(string id, string kind, string prompt, string? detail, IReadOnlyList<QuestionOption> options)
    {
        var p = _pending[id];
        _described[id] = new Question(id, kind, prompt, detail, options, p.Default, p.Chosen);
    }

    public IReadOnlyList<Question> All() => _order.Where(_described.ContainsKey).Select(id => _described[id]).ToList();

    /// <summary>Answers that did not match any question this build raised (stale or mistyped).</summary>
    public IEnumerable<string> UnusedAnswers() => _answers.Keys.Where(k => !_pending.ContainsKey(k));
}
