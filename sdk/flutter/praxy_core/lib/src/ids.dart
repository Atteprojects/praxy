import 'dart:math';

/// Row-id helpers. Rows created with no client-supplied id are server-generated
/// (a UUIDv7) — [unique] exists for callers who want the id *before* the create
/// call returns (optimistic UI, offline queues), matching Appwrite's `ID.unique()`.
abstract final class Uid {
  static final Random _random = Random.secure();

  /// A client-generated, dashed-lowercase UUID (v4). Valid as a row id — the server
  /// accepts either its own `n`-format (no dashes) or `d`-format (dashed) uuids.
  static String unique() {
    final bytes = List<int>.generate(16, (_) => _random.nextInt(256));
    bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
    bytes[8] = (bytes[8] & 0x3f) | 0x80; // RFC 4122 variant
    final hex = bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
    return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-${hex.substring(12, 16)}-'
        '${hex.substring(16, 20)}-${hex.substring(20)}';
  }

  /// Pass-through escape hatch — pairs with [unique] so both spellings exist even
  /// though a plain string literal already works as a custom id.
  static String custom(String id) => id;
}
