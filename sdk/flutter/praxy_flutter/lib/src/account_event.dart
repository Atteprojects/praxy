/// One event on the `account` realtime channel (the caller's own user/session
/// lifecycle — the server rewrites a bare `account` subscribe to `account.<userId>`).
/// Not row-shaped, so it isn't a [RowEvent] — just the raw envelope fields.
final class AccountEvent {
  const AccountEvent({
    required this.events,
    required this.channels,
    required this.payload,
    required this.timestamp,
  });

  final List<String> events;
  final List<String> channels;
  final Map<String, dynamic> payload;
  final DateTime timestamp;
}
