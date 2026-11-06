namespace EverythingonStart.Models;

public enum WebPreviewStatus { Ready, NoResults, NeedsBrowser, Unavailable }

public sealed record WebPreviewLink(string Title, string Url);

public sealed record WebPagePreview(string Title, string Url, string SiteName, string DisplayUrl,
    string Summary, IReadOnlyList<WebPreviewLink> SiteLinks)
{
    public bool HasSiteLinks => SiteLinks.Count > 0;
}

public sealed record WebPreviewResponse(WebPreviewStatus Status, IReadOnlyList<WebPagePreview> Results, string Message);
