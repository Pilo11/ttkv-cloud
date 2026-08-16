using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace CloudServices.Proxy;

internal sealed record ProxyUpstream(
    string ClusterId,
    string Host,
    string PathPrefix,
    string OriginHttps,
    string OriginHttp)
{
    public static readonly ProxyUpstream ClickTt = new(
        "click-tt",
        "ttvn.click-tt.de",
        "/click-tt",
        "https://ttvn.click-tt.de",
        "http://ttvn.click-tt.de");

    public static readonly ProxyUpstream LigaId = new(
        "liga-id",
        "ttde-id.liga.nu",
        "/liga-id",
        "https://ttde-id.liga.nu",
        "http://ttde-id.liga.nu");

    public static readonly ProxyUpstream[] All = [ClickTt, LigaId];

    public static ProxyUpstream? FromClusterId(string? clusterId) =>
        All.FirstOrDefault(u => string.Equals(u.ClusterId, clusterId, StringComparison.OrdinalIgnoreCase));

    public static ProxyUpstream? FromHost(string host) =>
        All.FirstOrDefault(u => string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase));

    public static ProxyUpstream? FromPath(string path)
    {
        foreach (var upstream in All)
        {
            if (path.Equals(upstream.PathPrefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(upstream.PathPrefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return upstream;
            }
        }

        return null;
    }
}

internal static class ClickTtProxyTransforms
{
    private static readonly Regex RootRelativeAttribute = new(
        @"(?<=\b(?:href|src|action|formaction|poster|cite)\s*=\s*['""])/(?!/|(?:click-tt|liga-id)(?:/|['""]|$))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CssRootRelativeUrl = new(
        @"(?<=url\(\s*['""]?)/(?!/|(?:click-tt|liga-id)(?:/|['"")]|$))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProtocolRelativeAttribute = new(
        @"(?<=\b(?:href|src)\s*=\s*['""])//(?=[^/])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CgiBinPath = new(
        @"(?<!/click-tt)/cgi-bin/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WebObjectsPath = new(
        @"(?<!cgi-bin)(?<!/click-tt)/WebObjects/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BannerPath = new(
        @"(?<!/click-tt)/banner/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LigaOauthPath = new(
        @"(?<!/liga-id)/oauth2/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RedirectUriParameter = new(
        @"redirect_uri=[^&""'\s]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Register(TransformBuilderContext context)
    {
        var upstream = ProxyUpstream.FromClusterId(context.Route.ClusterId)
            ?? ProxyUpstream.ClickTt;
        context.AddRequestTransform(transformContext => ApplyRequestAsync(transformContext, upstream));
        context.AddResponseTransform(transformContext => ApplyResponseAsync(transformContext, upstream));
    }

    private static ValueTask ApplyRequestAsync(RequestTransformContext context, ProxyUpstream upstream)
    {
        var request = context.ProxyRequest;
        request.Headers.Host = upstream.Host;
        request.Headers.Remove("X-Forwarded-For");
        request.Headers.Remove("X-Forwarded-Host");
        request.Headers.Remove("X-Forwarded-Proto");
        request.Headers.Remove("Forwarded");

        request.Headers.Remove("Origin");
        request.Headers.TryAddWithoutValidation("Origin", upstream.OriginHttps);

        if (request.Headers.TryGetValues("Referer", out var referers))
        {
            var referer = referers.FirstOrDefault();
            if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            {
                request.Headers.Remove("Referer");
                request.Headers.TryAddWithoutValidation("Referer", RewriteReferer(refererUri, upstream));
            }
        }

        if (!request.Headers.Contains("User-Agent"))
        {
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (compatible; TTKV-Cloud/1.0)");
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask ApplyResponseAsync(ResponseTransformContext context, ProxyUpstream upstream)
    {
        var response = context.HttpContext.Response;
        var requestIsHttps = context.HttpContext.Request.IsHttps;
        var publicOrigin = $"{context.HttpContext.Request.Scheme}://{context.HttpContext.Request.Host.Value}";

        response.Headers.Remove("X-Frame-Options");
        response.Headers.Remove("Strict-Transport-Security");
        RewriteContentSecurityPolicy(response.Headers, "Content-Security-Policy", publicOrigin);
        RewriteContentSecurityPolicy(response.Headers, "Content-Security-Policy-Report-Only", publicOrigin);

        RewriteHeaderUrls(response.Headers, "Location", publicOrigin, upstream);
        RewriteSetCookies(response.Headers, requestIsHttps, upstream.PathPrefix);

        if (context.ProxyResponse is null)
        {
            return;
        }

        var mediaType = context.ProxyResponse.Content.Headers.ContentType?.MediaType;
        if (!ShouldRewriteBody(mediaType))
        {
            return;
        }

        var rawBytes = await context.ProxyResponse.Content.ReadAsByteArrayAsync(context.HttpContext.RequestAborted);
        var contentEncoding = context.ProxyResponse.Content.Headers.ContentEncoding.FirstOrDefault()
            ?? response.Headers.ContentEncoding.FirstOrDefault();
        var decodedBytes = DecodeBody(rawBytes, contentEncoding);
        var charset = context.ProxyResponse.Content.Headers.ContentType?.CharSet?.Trim().Trim('"');
        var encoding = ResolveEncoding(charset);
        var original = encoding.GetString(decodedBytes);
        var rewritten = RewriteBody(original, publicOrigin, upstream);
        if (rewritten == original && string.IsNullOrEmpty(contentEncoding))
        {
            return;
        }

        var newBytes = encoding.GetBytes(rewritten);
        context.SuppressResponseBody = true;
        response.Headers.Remove("Content-Encoding");
        response.Headers.Remove("Content-Length");
        response.ContentLength = newBytes.Length;
        await response.Body.WriteAsync(newBytes, context.HttpContext.RequestAborted);
    }

    internal static string RewriteBody(string content, string publicOrigin, ProxyUpstream current)
    {
        var savedRedirectUris = new List<string>();
        var protectedContent = RedirectUriParameter.Replace(content, match =>
        {
            savedRedirectUris.Add(match.Value);
            return $"redirect_uri=__REDIRECT_URI_{savedRedirectUris.Count - 1}__";
        });

        var rewritten = protectedContent;
        foreach (var upstream in ProxyUpstream.All)
        {
            rewritten = RewriteUpstreamUrls(rewritten, publicOrigin, upstream);
        }

        if (current.ClusterId == ProxyUpstream.ClickTt.ClusterId)
        {
            rewritten = CgiBinPath.Replace(rewritten, current.PathPrefix + "/cgi-bin/");
            rewritten = WebObjectsPath.Replace(rewritten, current.PathPrefix + "/WebObjects/");
            rewritten = BannerPath.Replace(rewritten, current.PathPrefix + "/banner/");
        }
        else if (current.ClusterId == ProxyUpstream.LigaId.ClusterId)
        {
            rewritten = LigaOauthPath.Replace(rewritten, current.PathPrefix + "/oauth2/");
        }

        rewritten = RootRelativeAttribute.Replace(rewritten, current.PathPrefix + "/");
        rewritten = CssRootRelativeUrl.Replace(rewritten, current.PathPrefix + "/");
        rewritten = ProtocolRelativeAttribute.Replace(rewritten, "https://");

        for (var i = 0; i < savedRedirectUris.Count; i++)
        {
            rewritten = rewritten.Replace(
                $"redirect_uri=__REDIRECT_URI_{i}__",
                savedRedirectUris[i],
                StringComparison.Ordinal);
        }

        return rewritten;
    }

    internal static string RewriteUpstreamUrls(string value, string publicOrigin, ProxyUpstream upstream)
    {
        var replacement = publicOrigin + upstream.PathPrefix;
        var encodedReplacement = Uri.EscapeDataString(replacement);
        return value
            .Replace(upstream.OriginHttps, replacement, StringComparison.OrdinalIgnoreCase)
            .Replace(upstream.OriginHttp, replacement, StringComparison.OrdinalIgnoreCase)
            .Replace("//" + upstream.Host, replacement, StringComparison.OrdinalIgnoreCase)
            .Replace(Uri.EscapeDataString(upstream.OriginHttps), encodedReplacement, StringComparison.OrdinalIgnoreCase)
            .Replace(Uri.EscapeDataString(upstream.OriginHttp), encodedReplacement, StringComparison.OrdinalIgnoreCase);
    }

    internal static string RewriteLocationValue(string location, string publicOrigin, ProxyUpstream current)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var upstream = ProxyUpstream.FromHost(uri.Host);
            if (upstream is null)
            {
                return location;
            }

            return publicOrigin + upstream.PathPrefix + uri.PathAndQuery;
        }

        if (location.StartsWith('/') && ProxyUpstream.FromPath(location) is null)
        {
            return current.PathPrefix + location;
        }

        return location;
    }

    internal static string RewriteSetCookie(string cookie, bool requestIsHttps, string pathPrefix)
    {
        var parts = cookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return cookie;
        }

        var rebuilt = new List<string> { parts[0] };
        var hasPath = false;
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.StartsWith("Domain", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (part.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
            {
                hasPath = true;
                var path = part["Path=".Length..];
                if (!path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    path = path == "/" ? pathPrefix + "/" : pathPrefix + path;
                }

                rebuilt.Add("Path=" + path);
                continue;
            }

            if (!requestIsHttps && part.Equals("Secure", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!requestIsHttps && part.Equals("SameSite=None", StringComparison.OrdinalIgnoreCase))
            {
                rebuilt.Add("SameSite=Lax");
                continue;
            }

            rebuilt.Add(part);
        }

        if (!hasPath)
        {
            rebuilt.Add("Path=" + pathPrefix + "/");
        }

        return string.Join("; ", rebuilt);
    }

    private static string RewriteReferer(Uri refererUri, ProxyUpstream current)
    {
        var path = refererUri.AbsolutePath;
        var refererUpstream = ProxyUpstream.FromPath(path) ?? current;
        if (path.StartsWith(refererUpstream.PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[refererUpstream.PathPrefix.Length..];
        }

        if (path.Length == 0)
        {
            path = "/";
        }

        return refererUpstream.OriginHttps + path + refererUri.Query;
    }

    private static void RewriteHeaderUrls(
        IHeaderDictionary headers,
        string name,
        string publicOrigin,
        ProxyUpstream current)
    {
        if (!headers.TryGetValue(name, out var values) || values.Count == 0)
        {
            return;
        }

        var rewritten = values
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => RewriteLocationValue(v!, publicOrigin, current))
            .ToArray();
        headers[name] = rewritten;
    }

    private static void RewriteSetCookies(IHeaderDictionary headers, bool requestIsHttps, string pathPrefix)
    {
        if (!headers.TryGetValue("Set-Cookie", out var values) || values.Count == 0)
        {
            return;
        }

        var rewritten = values
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => RewriteSetCookie(v!, requestIsHttps, pathPrefix))
            .ToArray();
        headers["Set-Cookie"] = rewritten;
    }

    private static void RewriteContentSecurityPolicy(IHeaderDictionary headers, string name, string publicOrigin)
    {
        if (!headers.TryGetValue(name, out var values) || values.Count == 0)
        {
            return;
        }

        var rewritten = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var updated = value;
            foreach (var upstream in ProxyUpstream.All)
            {
                updated = updated.Replace(upstream.OriginHttps, publicOrigin, StringComparison.OrdinalIgnoreCase);
                updated = updated.Replace(upstream.OriginHttp, publicOrigin, StringComparison.OrdinalIgnoreCase);
            }

            var directives = updated
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(d => !d.StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (directives.Length > 0)
            {
                rewritten.Add(string.Join("; ", directives));
            }
        }

        if (rewritten.Count == 0)
        {
            headers.Remove(name);
        }
        else
        {
            headers[name] = rewritten.ToArray();
        }
    }

    private static bool ShouldRewriteBody(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return false;
        }

        return mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/css", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static byte[] DecodeBody(byte[] rawBytes, string? contentEncoding)
    {
        if (string.IsNullOrEmpty(contentEncoding))
        {
            return rawBytes;
        }

        try
        {
            using var input = new MemoryStream(rawBytes);
            Stream decompressed = contentEncoding.ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(input, CompressionMode.Decompress),
                "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                "br" => new BrotliStream(input, CompressionMode.Decompress),
                _ => input
            };

            if (ReferenceEquals(decompressed, input))
            {
                return rawBytes;
            }

            using (decompressed)
            using (var output = new MemoryStream())
            {
                decompressed.CopyTo(output);
                return output.ToArray();
            }
        }
        catch (InvalidDataException)
        {
            return rawBytes;
        }
    }
}
