using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Core.Services;

public sealed class SearchService : ISearchService
{
    private readonly IGameLibraryService _gameLibrary;

    public SearchService(IGameLibraryService gameLibrary)
    {
        _gameLibrary = gameLibrary;
    }

    public async Task<IReadOnlyList<GameEntry>> SearchAsync(string? query, GameVisibility scope, CancellationToken ct = default)
    {
        IReadOnlyList<GameEntry> games = scope == GameVisibility.Public
            ? await _gameLibrary.GetPublicGamesAsync(ct)
            : await _gameLibrary.GetSecretGamesAsync(ct);

        return FilterInMemory(games, query);
    }

    public IReadOnlyList<GameEntry> FilterInMemory(IReadOnlyList<GameEntry> source, string? query)
    {
        if (source.Count == 0)
            return Array.Empty<GameEntry>();

        string normalizedQuery = TextNormalization.NormalizeForSearch(query);
        if (string.IsNullOrEmpty(normalizedQuery))
            return source;

        var results = new List<GameEntry>(source.Count);
        foreach (GameEntry game in source)
        {
            string key = string.IsNullOrEmpty(game.SearchKey)
                ? TextNormalization.NormalizeForSearch(game.DisplayName)
                : game.SearchKey;

            if (key.Contains(normalizedQuery, StringComparison.Ordinal))
                results.Add(game);
        }

        return results;
    }
}
