/// Realtime transport health, reported on [PraxyRealtime.connection] — separate
/// from data streams, so a reconnect never shows up as a spurious row event.
enum PraxyConnectionState {
  /// No active subscription has requested a socket yet.
  disconnected,

  /// The first connection attempt is in flight.
  connecting,

  /// The server's `connected` frame arrived; subscriptions are live.
  connected,

  /// A previous connection dropped; backoff is running before the next attempt.
  reconnecting,
}
