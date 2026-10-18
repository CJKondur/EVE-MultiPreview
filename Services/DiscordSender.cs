using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EveMultiPreview.Services;

/// <summary>Sends one message to a webhook and says what came of it.</summary>
public interface IPushSender
{
    Task<PushResult> SendAsync(Uri webhook, string content, CancellationToken cancel);
}

/// <summary>IDEAS #2: the only class of the app that talks to Discord. It sends one JSON <c>POST</c> to the
/// webhook link it is handed (<c>content</c>, <c>username</c>, and an empty <c>allowed_mentions</c> so nothing is
/// pinged), after checking again with <see cref="DiscordWebhook.IsSendable"/> that the link is Discord's. It reads
/// no response body, follows no redirect (a 3xx is a failure), and keeps nothing of an exception: an exception's
/// message can name the link, so only the outcome is returned. Failures are returned, never thrown.</summary>
public sealed class DiscordSender : IPushSender, IDisposable
{
    /// <summary>The longest a request may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    /// <summary>The name Discord shows for the message.</summary>
    public const string UserName = "EVE MultiPreview";
    private static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(5);

    private readonly HttpClient _client;

    /// <summary>Takes the handler the requests go through (a test hands in a fake; the app hands in
    /// <see cref="RealHandler"/>). The sender disposes it.</summary>
    public DiscordSender(HttpMessageHandler handler) => _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout };

    /// <summary>The handler the app uses: no redirect, no cookies, a short connect time and a connection that is
    /// renewed so a changed address is noticed.</summary>
    public static SocketsHttpHandler RealHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    };

    public async Task<PushResult> SendAsync(Uri webhook, string content, CancellationToken cancel)
    {
        if (!DiscordWebhook.IsSendable(webhook)) return new PushResult(PushOutcome.Refused);
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                content,
                username = UserName,
                allowed_mentions = new { parse = Array.Empty<string>() },
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, webhook)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.UserAgent.TryParseAdd("EVE-MultiPreview/" + (typeof(DiscordSender).Assembly.GetName().Version?.ToString(3) ?? "0"));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            return Map(response);
        }
        catch (Exception e) when (e is HttpRequestException || e is IOException || e is OperationCanceledException)
        {
            return new PushResult(PushOutcome.Unreachable);
        }
    }

    private static PushResult Map(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        if (status >= 200 && status < 300) return new PushResult(PushOutcome.Sent, status);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return new PushResult(PushOutcome.RateLimited, status, RetryAfter(response));
        if (status == 401 || status == 403 || status == 404) return new PushResult(PushOutcome.Gone, status);
        if (status >= 500) return new PushResult(PushOutcome.Unreachable, status);
        return new PushResult(PushOutcome.Refused, status);
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is DateTimeOffset at ? at - DateTimeOffset.UtcNow : null);
        return wait is { } w && w > TimeSpan.Zero ? w : DefaultRetry;
    }

    public void Dispose() => _client.Dispose();
}
