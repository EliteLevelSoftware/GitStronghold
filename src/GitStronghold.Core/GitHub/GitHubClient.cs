using System.Net;
using System.Net.Http.Headers;

namespace GitStronghold.Core.GitHub;

/// <summary>
/// Talks only to allow-listed GitHub hosts over https.
/// The token is attached on api.github.com and is not copied onto a cross-host redirect.
/// </summary>
public sealed class GitHubClient : IDisposable
{
    public const string JsonAccept = "application/vnd.github+json";
    public const string RawAccept = "application/vnd.github.raw";
    public const string AssetAccept = "application/octet-stream";

    private readonly HttpClient _http;
    private readonly string? _token;
    private readonly GitHubClientOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GitHubClient(HttpMessageHandler handler, string? token, GitHubClientOptions? options = null, bool disposeHandler = false)
    {
        if (handler is HttpClientHandler httpHandler)
        {
            httpHandler.AllowAutoRedirect = false;
        }

        if (handler is SocketsHttpHandler sockets)
        {
            sockets.AllowAutoRedirect = false;
        }

        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        if (_token != null && (_token.Contains('\r') || _token.Contains('\n')))
        {
            throw new ArgumentException("The token cannot contain a line break.", nameof(token));
        }

        _options = options ?? new GitHubClientOptions();
        _delay = _options.Delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _http = new HttpClient(handler, disposeHandler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public static GitHubClient Create(string? token, GitHubClientOptions? options = null)
    {
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };
        return new GitHubClient(sockets, token, options, disposeHandler: true);
    }

    public event Action<TimeSpan>? RateLimited;

    public async Task<GitHubResult> GetJsonAsync(Uri uri, CancellationToken cancellationToken) =>
        await SendBufferedAsync(uri, JsonAccept, cancellationToken);

    public async Task<GitHubResult> CopyAsync(Uri uri, string accept, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        using var response = await SendFollowingRedirectsAsync(uri, accept, cancellationToken);
        if (response is null)
        {
            return GitHubResult.Fail("The download was refused.");
        }

        var status = (int)response.StatusCode;
        if (status is < 200 or >= 300)
        {
            return new GitHubResult { Status = status, Error = "HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }

        var length = response.Content.Headers.ContentLength;
        if (length > maxBytes)
        {
            return new GitHubResult { Status = status, Error = "The file is larger than the backup will store." };
        }

        var buffer = new byte[81920];
        var written = 0L;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            written += read;
            if (written > maxBytes)
            {
                return new GitHubResult { Status = status, Error = "The file is larger than the backup will store." };
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return new GitHubResult
        {
            Status = status,
            ContentType = response.Content.Headers.ContentType?.MediaType,
            Bytes = []
        };
    }

    public async Task<IReadOnlyList<System.Text.Json.JsonElement>> GetAllAsync(Uri first, CancellationToken cancellationToken)
    {
        var list = new List<System.Text.Json.JsonElement>();
        var next = first;
        for (var page = 0; page < 500 && next is not null; page++)
        {
            var result = await GetJsonAsync(next, cancellationToken);
            if (!result.IsSuccess)
            {
                throw new GitHubCallException(result.Status, result.Error ?? "HTTP " + result.Status);
            }

            using var doc = System.Text.Json.JsonDocument.Parse(result.Bytes);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new GitHubCallException(result.Status, "GitHub did not return a list.");
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                list.Add(item.Clone());
            }

            next = NextLink(result.LinkHeader, next);
        }

        return list;
    }

    public void Dispose() => _http.Dispose();

    private async Task<GitHubResult> SendBufferedAsync(Uri uri, string accept, CancellationToken cancellationToken)
    {
        using var response = await SendFollowingRedirectsAsync(uri, accept, cancellationToken);
        if (response is null)
        {
            return GitHubResult.Fail("The request was refused.");
        }

        var status = (int)response.StatusCode;
        var success = status is >= 200 and < 300;
        var bytes = success ? await response.Content.ReadAsByteArrayAsync(cancellationToken) : [];
        return new GitHubResult
        {
            Status = status,
            Bytes = bytes,
            ContentType = response.Content.Headers.ContentType?.MediaType,
            LinkHeader = response.Headers.TryGetValues("Link", out var link) ? string.Join(", ", link) : null,
            Error = success ? null : "HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private async Task<HttpResponseMessage?> SendFollowingRedirectsAsync(Uri uri, string accept, CancellationToken cancellationToken)
    {
        var waits = 0;
        var totalWait = TimeSpan.Zero;
        for (var hop = 0; hop < 6; hop++)
        {
            var refusal = Refuse(uri);
            if (refusal is not null)
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "GitStronghold/" + AppVersion.Current);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (_token != null && GitHubHosts.ReceivesToken(uri.Host))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
            }

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                {
                    return null;
                }

                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (Refuse(next) is not null)
                {
                    return null;
                }

                uri = next;
                continue;
            }

            if (IsRateLimited(response) && waits < _options.MaxRateLimitWaits)
            {
                var wait = Cap(ComputeWait(response));
                if (wait <= TimeSpan.Zero || totalWait + wait > _options.MaxTotalWait)
                {
                    return response;
                }

                response.Dispose();
                waits++;
                totalWait += wait;
                RateLimited?.Invoke(wait);
                await _delay(wait, cancellationToken);
                continue;
            }

            return response;
        }

        return null;
    }

    private static string? Refuse(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "Only https is allowed.";
        }

        if (!GitHubHosts.IsAllowed(uri.Host))
        {
            return "That host is not allow-listed.";
        }

        return null;
    }

    private TimeSpan Cap(TimeSpan wait)
    {
        if (wait < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return wait > _options.MaxSingleWait ? _options.MaxSingleWait : wait;
    }

    private TimeSpan ComputeWait(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta)
        {
            return delta;
        }

        if (retry?.Date is { } date)
        {
            var until = date - _options.Clock.GetUtcNow();
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out var reset))
        {
            var moment = DateTimeOffset.FromUnixTimeSeconds(reset);
            var until = moment - _options.Clock.GetUtcNow();
            return until > TimeSpan.Zero ? until : TimeSpan.FromSeconds(1);
        }

        return TimeSpan.FromSeconds(2);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status == 429)
        {
            return true;
        }

        if (status != 403)
        {
            return false;
        }

        if (response.Headers.RetryAfter is not null)
        {
            return true;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.Any(value => value.Trim() == "0"))
        {
            return true;
        }

        return false;
    }

    internal static Uri? NextLink(string? link, Uri current)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        foreach (var part in link.Split(','))
        {
            var bits = part.Split(';');
            if (bits.Length < 2)
            {
                continue;
            }

            var rel = bits[1];
            if (!rel.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)
                && !rel.Contains("rel=next", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = bits[0].Trim().TrimStart('<').TrimEnd('>').Trim();
            if (!Uri.TryCreate(current, raw, out var uri))
            {
                return null;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !GitHubHosts.IsAllowed(uri.Host))
            {
                return null;
            }

            return uri;
        }

        return null;
    }
}
