using System.Net.Http;
using System.Text.Json;
using EverythingonStart.Models;

namespace EverythingonStart.Services;

public sealed class WebSuggestionService
{
    private readonly HttpClient _httpClient;
    private readonly AppSettingsService _settingsService;

    public WebSuggestionService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2.5)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
        );
    }

    public SearchResultItem CreateDirectWebSearchItem(string query)
    {
        var trimmed = query.Trim();
        var url = _settingsService.BuildWebSearchUrl(trimmed);
        return new SearchResultItem
        {
            Title = $"{trimmed} - 查看更多搜索结果",
            Subtitle = $"使用 {_settingsService.Current.SearchEngine} 搜索 \"{trimmed}\"",
            FullPath = url,
            DirectoryPath = "Web 搜索",
            Kind = SearchResultKind.WebDirect
        };
    }

    public async Task<IReadOnlyList<SearchResultItem>> GetWebSuggestionsAsync(
        string query,
        int maxCount,
        CancellationToken cancellationToken
    )
    {
        var trimmed = query.Trim();
        if (string.IsNullOrEmpty(trimmed) || cancellationToken.IsCancellationRequested)
            return Array.Empty<SearchResultItem>();

        var results = new List<SearchResultItem>(maxCount + 1)
        {
            CreateDirectWebSearchItem(trimmed)
        };

        var suggestions = await FetchBingSuggestionsAsync(trimmed, cancellationToken);
        if (suggestions.Count == 0 && trimmed.Length > 3)
        {
            var shortPrefix = trimmed[..Math.Min(3, trimmed.Length)];
            suggestions = await FetchBingSuggestionsAsync(shortPrefix, cancellationToken);
        }

        foreach (var text in suggestions)
        {
            if (results.Count >= maxCount + 1)
                break;

            if (string.Equals(text, trimmed, StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(
                new SearchResultItem
                {
                    Title = text,
                    Subtitle = $"Web 搜索建议 · {_settingsService.Current.SearchEngine}",
                    FullPath = _settingsService.BuildWebSearchUrl(text),
                    DirectoryPath = "Web 搜索建议",
                    Kind = SearchResultKind.WebSuggestion
                }
            );
        }

        return results;
    }

    private async Task<List<string>> FetchBingSuggestionsAsync(string queryText, CancellationToken cancellationToken)
    {
        var url = $"https://api.bing.com/osjson.aspx?query={Uri.EscapeDataString(queryText)}&language=zh-CN";
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return [];

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2)
            return [];

        var suggestionsElement = root[1];
        if (suggestionsElement.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>();
        foreach (var elem in suggestionsElement.EnumerateArray())
        {
            if (elem.ValueKind == JsonValueKind.String)
            {
                var val = elem.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                    list.Add(val);
            }
        }

        return list;
    }
}
