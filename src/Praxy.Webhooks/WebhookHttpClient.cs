namespace Praxy.Webhooks;

/// <summary>
/// Typed client for webhook deliveries — timeout and the SSRF-guarded <c>SocketsHttpHandler</c> are
/// configured entirely where it's registered (Program.cs), mirroring <c>GoogleOAuthProvider</c>'s
/// typed-client shape rather than a named client, since there's exactly one delivery pipeline.
/// </summary>
public sealed class WebhookHttpClient(HttpClient http)
{
    public HttpClient Http { get; } = http;
}
