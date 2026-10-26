using System.Text.Json;

namespace SharpRail.Plugins.ClaudeCode.UI.Chat;

/// <summary>
/// The model picker's data and rules, after ThinkRail's pickerGroups, modelVisibility and useModelPreferences: models grouped by
/// family (Claude has one provider, so the family is the group), favourites and recents first, hidden models kept out of the list,
/// and a fuzzy score for the filter. Preferences are client-local and stored through the plugin's preference, never in a project.
/// </summary>
internal sealed record ModelPreferences(IReadOnlyList<string> Favorites, IReadOnlyList<string> Recents, IReadOnlyList<string> Hidden)
{
    public const int RecentLimit = 5;
    public static readonly ModelPreferences Empty = new([], [], []);

    public static ModelPreferences Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Empty;
        try
        {
            var parsed = JsonSerializer.Deserialize<ModelPreferences>(json);
            return parsed is null ? Empty : parsed with { Favorites = parsed.Favorites ?? [], Recents = parsed.Recents ?? [], Hidden = parsed.Hidden ?? [] };
        }
        catch (JsonException) { return Empty; }
    }

    public string Serialize() => JsonSerializer.Serialize(this);

    public ModelPreferences ToggleFavorite(string id) => this with { Favorites = Flip(Favorites, id), Recents = Recents.Where(recent => recent != id).ToList() };

    public ModelPreferences ToggleHidden(string id) => this with { Hidden = Flip(Hidden, id), Favorites = Favorites.Where(favorite => favorite != id).ToList() };

    /// <summary>A chosen model leads the recents, unless it is a favourite already.</summary>
    public ModelPreferences Picked(string id) => Favorites.Contains(id) ? this : this with { Recents = [id, .. Recents.Where(recent => recent != id)] };

    private static List<string> Flip(IReadOnlyList<string> list, string id) => list.Contains(id) ? [.. list.Where(item => item != id)] : [.. list, id];
}

internal enum PickRowKind { Header, Model }

/// <summary>One row of the picker: a section or group header, or a model.</summary>
internal sealed record PickRow(PickRowKind Kind, string Title, ChatModel? Model = null, string Section = "", bool Favorite = false, bool Hidden = false, bool Expanded = true, int Count = 0);

internal static class ModelPicks
{
    private static readonly System.Text.RegularExpressions.Regex DateSuffix = new(@"-\d{8}$");

    /// <summary>An id or label reduced to what two spellings of one model share: no <c>claude-</c> prefix, no date suffix, case, spaces and dots folded.</summary>
    internal static string Normalize(string text)
    {
        var folded = text.Trim().ToLowerInvariant().Replace("(recommended)", "").Trim();
        foreach (var mark in new[] { ' ', '.', '_' }) folded = folded.Replace(mark, '-');
        while (folded.Contains("--", StringComparison.Ordinal)) folded = folded.Replace("--", "-", StringComparison.Ordinal);
        folded = folded.Trim('-');
        if (folded.StartsWith("claude-", StringComparison.Ordinal)) folded = folded[7..];
        return DateSuffix.Replace(folded, "");
    }

    /// <summary>Whether a header's model id names this list entry, leniently: <c>default</c> is the Default entry, and ids and labels meet after <see cref="Normalize"/>.</summary>
    public static bool Matches(ChatModel model, string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (model.Id == id) return true;
        var wanted = Normalize(id);
        if (wanted == "default") return IsDefault(model);
        return Normalize(model.Id) == wanted || Normalize(model.Label) == wanted;
    }

    public static bool IsDefault(ChatModel model) => Normalize(model.Id) == "default" || Normalize(model.Label).StartsWith("default", StringComparison.Ordinal);

    /// <summary>The name to show for a model id: the list entry's label when one matches, else the raw id.</summary>
    public static string Label(string id, IReadOnlyList<ChatModel> models) => models.FirstOrDefault(model => Matches(model, id))?.Label ?? id;

    /// <summary>Whether the host names more than one provider, the only case where the list is grouped.</summary>
    internal static bool Grouped(IReadOnlyList<ChatModel> models) => models.Select(model => model.Provider).Where(provider => !string.IsNullOrEmpty(provider)).Distinct().Count() > 1;

    /// <summary>The rows to show: Favorites and Recent when they hold anything, then every model in the CLI's order with the Default entry first; ranked by score when a query is typed. Hidden models appear only when asked for. Only a second provider adds group headers.</summary>
    public static List<PickRow> Rows(IReadOnlyList<ChatModel> models, ModelPreferences prefs, string query, string? current, bool showHidden, ISet<string> toggled)
    {
        var rows = new List<PickRow>();
        var visible = models.Where(model => showHidden || !prefs.Hidden.Contains(model.Id)).OrderBy(model => IsDefault(model) ? 0 : 1).ToList();
        var searching = query.Trim().Length > 0;
        if (searching)
        {
            var ranked = visible.Select(model => (model, score: Score(model.Label + " " + model.Id, query.Trim()))).Where(pair => pair.score > 0).OrderByDescending(pair => pair.score).Select(pair => pair.model);
            rows.AddRange(ranked.Select(model => Model(model, "match", prefs)));
            return rows;
        }
        var favorites = visible.Where(model => prefs.Favorites.Contains(model.Id)).ToList();
        if (favorites.Count > 0)
        {
            rows.Add(new(PickRowKind.Header, "Favorites", Count: favorites.Count));
            rows.AddRange(favorites.Select(model => Model(model, "favorite", prefs)));
        }
        var recents = prefs.Recents.Select(id => visible.FirstOrDefault(model => model.Id == id)).OfType<ChatModel>().Where(model => !prefs.Favorites.Contains(model.Id)).ToList();
        if (recents.Count > 0)
        {
            rows.Add(new(PickRowKind.Header, "Recent", Count: recents.Count));
            rows.AddRange(recents.Select(model => Model(model, "recent", prefs)));
        }
        var rest = visible.Where(model => !prefs.Favorites.Contains(model.Id)).ToList();
        if (!Grouped(models)) { rows.AddRange(rest.Select(model => Model(model, "all", prefs))); return rows; }
        foreach (var group in rest.GroupBy(model => model.Provider ?? ""))
        {
            var open = !toggled.Contains(group.Key);
            rows.Add(new(PickRowKind.Header, group.Key, Expanded: open, Count: group.Count()));
            if (open) rows.AddRange(group.Select(model => Model(model, "all", prefs)));
        }
        return rows;
    }

    private static PickRow Model(ChatModel model, string section, ModelPreferences prefs) =>
        new(PickRowKind.Model, model.Label, model, section, prefs.Favorites.Contains(model.Id), prefs.Hidden.Contains(model.Id));

    // The fuzzy score of ThinkRail's picker (a command-score port): consecutive and word-start matches rank high, skipped letters cost.
    private const double ContinueMatch = 1, SpaceWordJump = 0.9, NonSpaceWordJump = 0.8, CharacterJump = 0.17, Transposition = 0.1,
        Skipped = 0.999, CaseMismatch = 0.9999, NotComplete = 0.99;

    private static bool IsGap(char c) => "\\/_+.#\"@[({&".Contains(c);
    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c == '-';

    public static double Score(string text, string query) =>
        ScoreFrom(text, query, text.ToLowerInvariant().Replace('-', ' '), query.ToLowerInvariant().Replace('-', ' '), 0, 0, []);

    private static double ScoreFrom(string text, string query, string lowerText, string lowerQuery, int textIndex, int queryIndex, Dictionary<(int, int), double> memo)
    {
        if (queryIndex == query.Length) return textIndex == text.Length ? ContinueMatch : NotComplete;
        if (memo.TryGetValue((textIndex, queryIndex), out var known)) return known;
        var queryChar = lowerQuery[queryIndex];
        var index = lowerText.IndexOf(queryChar, textIndex);
        var best = 0.0;
        while (index >= 0)
        {
            var score = ScoreFrom(text, query, lowerText, lowerQuery, index + 1, queryIndex + 1, memo);
            if (score > best)
            {
                if (index == textIndex) score *= ContinueMatch;
                else if (IsGap(text[index - 1]))
                {
                    score *= NonSpaceWordJump;
                    if (textIndex > 0) score *= Math.Pow(Skipped, text[textIndex..(index - 1)].Count(IsGap));
                }
                else if (IsSpace(text[index - 1]))
                {
                    score *= SpaceWordJump;
                    if (textIndex > 0) score *= Math.Pow(Skipped, text[textIndex..(index - 1)].Count(IsSpace));
                }
                else
                {
                    score *= CharacterJump;
                    if (textIndex > 0) score *= Math.Pow(Skipped, index - textIndex);
                }
                if (text[index] != query[queryIndex]) score *= CaseMismatch;
            }
            if (queryIndex + 1 < query.Length && (score < Transposition && lowerText[index - 1 < 0 ? 0 : index - 1] == lowerQuery[queryIndex + 1] ||
                lowerQuery[queryIndex + 1] == lowerQuery[queryIndex] && lowerText[index - 1 < 0 ? 0 : index - 1] != lowerQuery[queryIndex]))
            {
                var transposed = ScoreFrom(text, query, lowerText, lowerQuery, index + 1, queryIndex + 2, memo);
                if (transposed * Transposition > score) score = transposed * Transposition;
            }
            if (score > best) best = score;
            index = lowerText.IndexOf(queryChar, index + 1);
        }
        memo[(textIndex, queryIndex)] = best;
        return best;
    }
}