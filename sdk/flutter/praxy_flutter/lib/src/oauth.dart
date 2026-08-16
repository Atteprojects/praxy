import 'package:flutter_web_auth_2/flutter_web_auth_2.dart';
import 'package:praxy_core/praxy_core.dart';

/// The shape of `FlutterWebAuth2.authenticate` — a seam so tests can stub the
/// browser round trip instead of driving a real one.
typedef WebAuthenticator =
    Future<String> Function({
      required String url,
      required String callbackUrlScheme,
      FlutterWebAuth2Options options,
    });

/// Google sign-in via the OAuth *token* flow (architecture.md/research/appwrite-
/// api.md): a browser tab opens the provider's consent screen, the callback lands
/// on [callbackUrlScheme]`://oauth/{success,failure}` carrying `userId`+`secret`
/// (or `error`), and that pair is exchanged at the same
/// `POST /v1/account/sessions/token` every token flow converges on
/// ([AccountService.createOAuth2Session]) — never a cookie-based session.
final class PraxyOAuth {
  PraxyOAuth(this._client, {WebAuthenticator? authenticator})
    : _authenticate = authenticator ?? FlutterWebAuth2.authenticate;

  final Praxy _client;
  final WebAuthenticator _authenticate;

  /// Convenience for the only provider Praxy's app-user auth supports today
  /// (CLAUDE.md: "email+password and Google OAuth only until the owner says
  /// otherwise").
  Future<CreatedSession> signInWithGoogle({required String callbackUrlScheme, bool preferEphemeral = false}) =>
      signIn(provider: 'google', callbackUrlScheme: callbackUrlScheme, preferEphemeral: preferEphemeral);

  /// The general form, in case a second provider lands later without an SDK change.
  Future<CreatedSession> signIn({
    required String provider,
    required String callbackUrlScheme,
    bool preferEphemeral = false,
  }) async {
    final success = '$callbackUrlScheme://oauth/success';
    final failure = '$callbackUrlScheme://oauth/failure';
    final authorizeUrl = _client.endpoint.replace(
      path: '/v1/account/sessions/oauth2/$provider',
      queryParameters: {'project': _client.projectId, 'success': success, 'failure': failure},
    );

    final String callbackUrl;
    try {
      callbackUrl = await _authenticate(
        url: authorizeUrl.toString(),
        callbackUrlScheme: callbackUrlScheme,
        options: FlutterWebAuth2Options(preferEphemeral: preferEphemeral),
      );
    } catch (error, stackTrace) {
      Error.throwWithStackTrace(PraxyNetworkException(cause: error, stackTrace: stackTrace), stackTrace);
    }

    final result = Uri.parse(callbackUrl);
    final providerError = result.queryParameters['error'];
    if (providerError != null) {
      throw PraxyAuthException(
        message: 'The provider rejected sign-in: $providerError',
        status: 401,
        type: providerError,
      );
    }

    final userId = result.queryParameters['userId'];
    final secret = result.queryParameters['secret'];
    if (userId == null || secret == null) {
      throw PraxyDecodeException(
        field: 'callback',
        expected: 'userId and secret query parameters',
        message: 'The OAuth callback was missing userId/secret: $callbackUrl',
      );
    }
    return _client.account.createOAuth2Session(userId: userId, secret: secret);
  }
}
