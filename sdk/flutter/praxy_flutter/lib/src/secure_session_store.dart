import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:praxy_core/praxy_core.dart';

/// Keychain (iOS/macOS) / Keystore (Android) backed [SessionStore], keyed by
/// project id. Not a cookie jar — Appwrite's `PersistCookieJar`-in-app-documents
/// approach is, per research/flutter-sdk.md, "the single largest source of
/// user-reported breakage" in their mobile SDK. Session restore across an app
/// restart is exactly: read this key once at startup.
final class SecureSessionStore implements SessionStore {
  SecureSessionStore({required this.projectId, FlutterSecureStorage? storage})
    : _storage =
          storage ??
          const FlutterSecureStorage(
            // allowBackup=false: a session secret restored from a stale Google
            // Drive/iCloud backup onto a different device is a stolen-session bug,
            // not a convenience.
            aOptions: AndroidOptions(),
          );

  final String projectId;
  final FlutterSecureStorage _storage;

  String get _key => 'praxy_session_$projectId';

  @override
  Future<Session?> read() async {
    final raw = await _storage.read(key: _key);
    if (raw == null) return null;
    try {
      final json = jsonDecode(raw) as Map<String, dynamic>;
      return Session(
        projectId: json['projectId'] as String,
        userId: json['userId'] as String,
        sessionId: json['sessionId'] as String,
        secret: json['secret'] as String,
        expiresAt: DateTime.parse(json['expiresAt'] as String),
      );
    } catch (_) {
      // Corrupted or foreign value under our key — treat as "no session" rather
      // than fail app startup, but don't leave the bad value behind either.
      await _storage.delete(key: _key);
      return null;
    }
  }

  @override
  Future<void> write(Session session) => _storage.write(
    key: _key,
    value: jsonEncode({
      'projectId': session.projectId,
      'userId': session.userId,
      'sessionId': session.sessionId,
      'secret': session.secret,
      'expiresAt': session.expiresAt.toIso8601String(),
    }),
  );

  @override
  Future<void> clear() => _storage.delete(key: _key);
}
