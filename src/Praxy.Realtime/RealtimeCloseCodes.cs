using System.Net.WebSockets;

namespace Praxy.Realtime;

/// <summary>Close codes per research/appwrite-api.md's realtime section.</summary>
public static class RealtimeCloseCodes
{
    /// <summary>Malformed client message (bad JSON, unknown/missing <c>type</c>).</summary>
    public const WebSocketCloseStatus BadMessage = WebSocketCloseStatus.InvalidMessageType;

    /// <summary>Auth/connection-level denial settled before or during accept: unknown project, invalid or expired ticket, a key missing the realtime scope.</summary>
    public const WebSocketCloseStatus PolicyViolation = WebSocketCloseStatus.PolicyViolation;

    /// <summary>Slow consumer (bounded outbound buffer full) or the per-project connection quota. No named enum member for 1013 exists — cast the raw code.</summary>
    public const WebSocketCloseStatus Overloaded = (WebSocketCloseStatus)1013;
}
