using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using EverythingonStart.Models;
using HtmlAgilityPack;

namespace EverythingonStart.Services;

public sealed class WebPreviewService : IDisposable
{
    private const int MaxHtmlBytes = 2_000_000;
    private const int MaxResults = 8;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly object _cacheLock = new();
    private readonly Dictionary<(WebSearchEngine, string), (DateTimeOffset Time, WebPreviewResponse Response)> _cache = [];
    private bool _disposed;

    public WebPreviewService(HttpClient? client = null)
    {
        _ownsClient = client == null;
        _client = client ?? new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        }) { Timeout = TimeSpan.FromSeconds(8) };
        if (_ownsClient)
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.7");
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string GetSearchUrl(string query, WebSearchEngine engine)
    {
        string text = Uri.EscapeDataString(query.Trim());
        return engine switch
        {
            WebSearchEngine.Google => $"https://www.google.com/search?q={text}",
            WebSearchEngine.Baidu => $"https://www.baidu.com/s?wd={text}",
            WebSearchEngine.DuckDuckGo => $"https://duckduckgo.com/?q={text}",
            _ => $"https://www.bing.com/search?q={text}"
        };
    }

    public static string GetQuery(SearchResultItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.WebSearchQuery))
            return item.WebSearchQuery.Trim();
        if (Uri.TryCreate(item.FullPath, UriKind.Absolute, out var url))
        {
            string query = Parameter(url, "q");
            if (query.Length == 0) query = Parameter(url, "wd");
            if (query.Length > 0) return query.Trim();
        }
        return item.Kind == SearchResultKind.WebSuggestion ? item.Title.Trim() : string.Empty;
    }

    public static bool IsSafeWebUrl(string text) => Uri.TryCreate(text, UriKind.Absolute, out var url)
        && text.Length <= 8192 && url.Scheme is "http" or "https" && url.UserInfo.Length == 0 && url.Host.Length > 0;

    public void ClearCache()
    {
        lock (_cacheLock) _cache.Clear();
    }

    public async Task<WebPreviewResponse> GetResultsAsync(string query, WebSearchEngine engine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = query.Trim();
        if (_disposed || query.Length == 0 || query.Length > 512 || !Enum.IsDefined(engine))
            return new(WebPreviewStatus.Unavailable, [], "查询为空、过长或搜索引擎不可用，请在浏览器继续。");
        var key = (engine, query);
        lock (_cacheLock)
            if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromMinutes(2))
                return cached.Response;
        string requestUrl = engine == WebSearchEngine.DuckDuckGo
            ? $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}"
            : GetSearchUrl(query, engine);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await _client.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return new(WebPreviewStatus.NeedsBrowser, [], "搜索引擎要求浏览器验证，请在浏览器查看结果。");
            if ((int)response.StatusCode == 429)
                return new(WebPreviewStatus.Unavailable, [], "搜索引擎暂时限流，请稍后重试或在浏览器继续。");
            if (!response.IsSuccessStatusCode)
                return new(WebPreviewStatus.Unavailable, [], "未能连接搜索引擎，请重试或在浏览器继续。");
            if (response.Content.Headers.ContentLength > MaxHtmlBytes)
                return new(WebPreviewStatus.Unavailable, [], "搜索页面过大，请在浏览器查看。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[16_384];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > MaxHtmlBytes)
                    return new(WebPreviewStatus.Unavailable, [], "搜索页面过大，请在浏览器查看。");
                bytes.Write(buffer, 0, read);
            }
            Encoding encoding = Encoding.UTF8;
            try
            {
                string? charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
                if (!string.IsNullOrEmpty(charset)) encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException) { }
            cancellationToken.ThrowIfCancellationRequested();
            string html = encoding.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            var results = ParseResults(html, engine);
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count == 0)
            {
                bool challenge = html.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("请输入验证码", StringComparison.Ordinal)
                    || response.RequestMessage?.RequestUri?.AbsolutePath.Contains("/sorry", StringComparison.OrdinalIgnoreCase) == true;
                return new(challenge ? WebPreviewStatus.NeedsBrowser : WebPreviewStatus.NoResults, [],
                    challenge ? "搜索引擎要求浏览器验证，请在浏览器查看结果。" : "暂未提取到可预览的网页结果，请在浏览器继续搜索。");
            }
            var ready = new WebPreviewResponse(WebPreviewStatus.Ready, results, string.Empty);
            lock (_cacheLock)
            {
                if (_disposed) return new(WebPreviewStatus.Unavailable, [], "网页预览已关闭。");
                if (_cache.Count >= 24)
                    _cache.Remove(_cache.OrderBy(pair => pair.Value.Time).First().Key);
                _cache[key] = (DateTimeOffset.UtcNow, ready);
            }
            return ready;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(WebPreviewStatus.Unavailable, [], "网页预览请求超时，请重试或在浏览器继续。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException)
        {
            return new(WebPreviewStatus.Unavailable, [], "网页预览暂不可用，请重试或在浏览器继续。");
        }
    }

    public static IReadOnlyList<WebPagePreview> ParseResults(string html, WebSearchEngine engine)
    {
        if (html.Length > MaxHtmlBytes) return [];
        var document = new HtmlDocument { OptionCheckSyntax = false, OptionFixNestedTags = true, OptionMaxNestedChildNodes = 500 };
        try { document.LoadHtml(html); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return []; }
        foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//svg")?.ToArray() ?? []) node.Remove();
        var results = new List<WebPagePreview>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string xpath = engine switch
        {
            WebSearchEngine.Bing => "//li[contains(concat(' ',normalize-space(@class),' '),' b_algo ')]",
            WebSearchEngine.Google => "//h3",
            WebSearchEngine.Baidu => "//div[contains(@class,'result') or @tpl]",
            WebSearchEngine.DuckDuckGo => "//div[contains(concat(' ',normalize-space(@class),' '),' result ')]",
            _ => string.Empty
        };
        if (xpath.Length == 0) return [];
        foreach (var candidate in document.DocumentNode.SelectNodes(xpath) ?? new HtmlNodeCollection(null))
        {
            HtmlNode block = candidate;
            HtmlNode? link;
            if (engine == WebSearchEngine.Google)
            {
                link = candidate.SelectSingleNode("ancestor::a[@href][1]");
                block = candidate.SelectSingleNode("ancestor::div[contains(concat(' ',normalize-space(@class),' '),' MjjYud ')][1]")
                    ?? candidate.SelectSingleNode("ancestor::div[contains(concat(' ',normalize-space(@class),' '),' g ')][1]")
                    ?? link?.ParentNode ?? candidate;
            }
            else link = engine == WebSearchEngine.DuckDuckGo
                ? block.SelectSingleNode(".//a[contains(@class,'result__a')]") : block.SelectSingleNode(".//h2//a[@href]|.//h3//a[@href]");
            if (link == null) continue;
            string title = Plain(engine == WebSearchEngine.Google ? candidate : link, 200);
            string? url = ResolveUrl(link.GetAttributeValue("href", ""), engine);
            if (title.Length == 0 || url == null || !seen.Add(url)) continue;
            var uri = new Uri(url);
            var summary = block.SelectSingleNode(".//*[contains(@class,'b_caption')]//p|.//*[contains(@class,'VwiC3b')]|.//*[@data-sncf]|.//*[contains(@class,'c-abstract')]|.//*[contains(@class,'content-right')]|.//*[contains(@class,'result__snippet')]");
            string site = Plain(block.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' tptt ')]|.//*[contains(@class,'VuuXrf')]"), 80);
            if (site.Length == 0) site = uri.IdnHost;
            string display = Plain(block.SelectSingleNode(".//cite|.//*[contains(@class,'c-showurl')]|.//*[contains(@class,'result__url')]"), 200);
            if (display.Length == 0) display = uri.GetLeftPart(UriPartial.Path);
            var links = new List<WebPreviewLink>();
            var linkUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { url };
            foreach (var extra in block.SelectNodes(".//*[contains(@class,'b_sitelinks') or contains(@class,'b_vList')]//a[@href]") ?? new HtmlNodeCollection(null))
            {
                string text = Plain(extra, 80);
                string? target = ResolveUrl(extra.GetAttributeValue("href", ""), engine);
                if (text.Length > 0 && target != null && linkUrls.Add(target)) links.Add(new(text, target));
                if (links.Count == 4) break;
            }
            results.Add(new(title, url, site, display, Plain(summary, 600), links.ToArray()));
            if (results.Count == MaxResults) break;
        }
        return results.ToArray();
    }

    private static string Plain(HtmlNode? node, int limit)
    {
        string text = Regex.Replace(HtmlEntity.DeEntitize(node?.InnerText ?? ""), @"\s+", " ").Trim();
        return text.Length <= limit ? text : text[..limit];
    }

    private static string? ResolveUrl(string href, WebSearchEngine engine)
    {
        href = HtmlEntity.DeEntitize(href).Trim();
        if (href.Length == 0 || href.Length > 8192) return null;
        if (!Uri.TryCreate(new Uri(GetSearchUrl("", engine)), href, out var uri) || !IsSafeWebUrl(uri.AbsoluteUri)) return null;
        string target = string.Empty;
        bool Host(string host) => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith('.' + host, StringComparison.OrdinalIgnoreCase);
        if (Host("bing.com") && uri.AbsolutePath.StartsWith("/ck/a", StringComparison.Ordinal))
        {
            string encoded = Parameter(uri, "u");
            if (encoded.StartsWith("a1", StringComparison.Ordinal))
            {
                try
                {
                    string base64 = encoded[2..].Replace('-', '+').Replace('_', '/');
                    target = Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
                }
                catch (FormatException) { }
            }
        }
        else if (Host("google.com") && uri.AbsolutePath == "/url")
            target = Parameter(uri, "q") is { Length: > 0 } q ? q : Parameter(uri, "url");
        else if (Host("duckduckgo.com") && uri.AbsolutePath.StartsWith("/l/", StringComparison.Ordinal))
            target = Parameter(uri, "uddg");
        if (target.Length > 0) return IsSafeWebUrl(target) ? new Uri(target).AbsoluteUri : null;
        return uri.AbsoluteUri;
    }

    private static string Parameter(Uri uri, string name)
    {
        foreach (string part in uri.Query.TrimStart('?').Split('&'))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
        }
        return string.Empty;
    }

    public void Dispose()
    {
        _disposed = true;
        ClearCache();
        if (_ownsClient) _client.Dispose();
    }
}
