import 'dart:async';
import 'dart:convert';
import 'dart:math';

import 'package:web_socket_channel/web_socket_channel.dart';

import '../connection_state.dart';

final class _Subscription {
  _Subscription(this.channels, this.controller);
  final List<String> channels;
  final StreamController<Map<String, dynamic>> controller;
}

/// One WebSocket, multiplexed across every [subscribe] caller, reference-counted:
/// connects on the first subscription's first listener, disconnects after a grace
/// period once the last one cancels. Reconnects with exponential backoff *and full
/// jitter* (never Appwrite's lockstep step function — every client reconnecting on
/// the same schedule is its own outage), resending every still-active subscription
/// once the new socket's `connected` frame arrives.
///
/// Constructing more than one of these per client would open two sockets — the
/// exact Appwrite bug this type exists to make structurally impossible; callers
/// must go through `PraxyRealtime`'s single lazily-created instance.
final class RealtimeSocket {
  RealtimeSocket({
    required Uri endpoint,
    required String projectId,
    required Future<String?> Function() ticketProvider,
    Duration idleGracePeriod = const Duration(seconds: 5),
    Duration pingInterval = const Duration(seconds: 20),
    Duration maxBackoff = const Duration(seconds: 30),
  }) : _wsBase = _toWebSocketBase(endpoint, projectId),
       _ticketProvider = ticketProvider,
       _idleGracePeriod = idleGracePeriod,
       _pingInterval = pingInterval,
       _maxBackoff = maxBackoff;

  final Uri _wsBase;
  final Future<String?> Function() _ticketProvider;
  final Duration _idleGracePeriod;
  final Duration _pingInterval;
  final Duration _maxBackoff;
  final Random _random = Random();

  WebSocketChannel? _channel;
  StreamSubscription<dynamic>? _channelSub;
  Timer? _pingTimer;
  Timer? _reconnectTimer;
  Timer? _idleCloseTimer;
  int _reconnectAttempts = 0;
  bool _ready = false;
  bool _disposed = false;
  int _nextSubscriptionId = 0;

  final Map<String, _Subscription> _subscriptions = {};
  final StreamController<PraxyConnectionState> _connectionController = StreamController.broadcast();
  PraxyConnectionState _state = PraxyConnectionState.disconnected;

  /// Replays the current state to every new listener, then forwards live changes —
  /// a plain broadcast stream wouldn't tell a listener who joins mid-connection
  /// anything until the *next* transition.
  Stream<PraxyConnectionState> get connectionStates async* {
    yield _state;
    yield* _connectionController.stream;
  }

  /// Subscribes to [channels] as soon as the returned stream gets a listener, and
  /// unsubscribes when it loses its last one — the unit of ref-counting this class
  /// multiplexes over one socket.
  Stream<Map<String, dynamic>> subscribe(List<String> channels) {
    final id = 'sub${_nextSubscriptionId++}';
    late final StreamController<Map<String, dynamic>> controller;
    controller = StreamController<Map<String, dynamic>>.broadcast(
      onListen: () => _activate(id, channels, controller),
      onCancel: () => _deactivate(id),
    );
    return controller.stream;
  }

  void dispose() {
    _disposed = true;
    _idleCloseTimer?.cancel();
    _reconnectTimer?.cancel();
    _teardownChannel();
    for (final sub in _subscriptions.values) {
      sub.controller.close();
    }
    _subscriptions.clear();
    _setState(PraxyConnectionState.disconnected);
  }

  // ---- activation / ref-counting -----------------------------------------------

  void _activate(String id, List<String> channels, StreamController<Map<String, dynamic>> controller) {
    _idleCloseTimer?.cancel();
    _idleCloseTimer = null;
    _subscriptions[id] = _Subscription(channels, controller);
    _ensureConnecting();
    if (_ready) _sendSubscribe({id: channels});
  }

  void _deactivate(String id) {
    final removed = _subscriptions.remove(id);
    if (removed == null) return;
    if (_ready && _channel != null) _sendUnsubscribe([id]);
    if (_subscriptions.isEmpty) _scheduleIdleClose();
  }

  // ---- connection lifecycle ------------------------------------------------------

  void _ensureConnecting() {
    if (_channel != null || _reconnectTimer != null) return;
    unawaited(_connect());
  }

  Future<void> _connect() async {
    if (_disposed) return;
    _setState(_reconnectAttempts == 0 ? PraxyConnectionState.connecting : PraxyConnectionState.reconnecting);

    final ticket = await _ticketProvider();
    if (_disposed) return;
    var uri = _wsBase;
    if (ticket != null) {
      uri = uri.replace(queryParameters: {...uri.queryParameters, 'ticket': ticket});
    }

    try {
      final channel = WebSocketChannel.connect(uri);
      await channel.ready;
      if (_disposed) {
        await channel.sink.close();
        return;
      }
      _channel = channel;
      _ready = false;
      _channelSub = channel.stream.listen(
        _onMessage,
        onDone: _onSocketDone,
        onError: _onSocketError,
        cancelOnError: true,
      );
    } catch (_) {
      _scheduleReconnect();
    }
  }

  void _onMessage(dynamic raw) {
    final Map<String, dynamic> message;
    try {
      final text = raw is String ? raw : utf8.decode(raw as List<int>);
      message = jsonDecode(text) as Map<String, dynamic>;
    } catch (_) {
      return; // a malformed frame is not this client's problem to crash over
    }

    switch (message['type']) {
      case 'connected':
        _ready = true;
        _reconnectAttempts = 0;
        _setState(PraxyConnectionState.connected);
        _startPing();
        _resubscribeAll();
      case 'event':
        _routeEvent(message['data'] as Map<String, dynamic>?);
      case 'pong':
      case 'response':
      case 'error':
        break; // liveness/acks only; errors have no per-subscription home to route to
      default:
        break; // forward-compatible: an unknown message type is ignored, never thrown on
    }
  }

  void _routeEvent(Map<String, dynamic>? data) {
    if (data == null) return;
    final matched = (data['subscriptions'] as List?)?.cast<String>() ?? const [];
    for (final id in matched) {
      _subscriptions[id]?.controller.add(data);
    }
  }

  void _resubscribeAll() {
    if (_subscriptions.isEmpty) return;
    _sendSubscribe({for (final e in _subscriptions.entries) e.key: e.value.channels});
  }

  void _sendSubscribe(Map<String, List<String>> byId) {
    _send({
      'type': 'subscribe',
      'data': [
        for (final e in byId.entries) {'subscriptionId': e.key, 'channels': e.value},
      ],
    });
  }

  void _sendUnsubscribe(List<String> ids) {
    _send({
      'type': 'unsubscribe',
      'data': [
        for (final id in ids) {'subscriptionId': id},
      ],
    });
  }

  void _send(Map<String, dynamic> message) {
    _channel?.sink.add(jsonEncode(message));
  }

  void _startPing() {
    _pingTimer?.cancel();
    _pingTimer = Timer.periodic(_pingInterval, (_) => _send({'type': 'ping'}));
  }

  void _onSocketDone() => _handleDisconnect();

  void _onSocketError(Object error, StackTrace stackTrace) => _handleDisconnect();

  void _handleDisconnect() {
    _teardownChannel();
    if (_disposed) return;
    if (_subscriptions.isNotEmpty) {
      _scheduleReconnect();
    } else {
      _setState(PraxyConnectionState.disconnected);
    }
  }

  void _teardownChannel() {
    _pingTimer?.cancel();
    _pingTimer = null;
    unawaited(_channelSub?.cancel());
    _channelSub = null;
    _channel = null;
    _ready = false;
  }

  /// Exponential backoff *with full jitter*: `delay = uniform(0, min(maxBackoff, base * 2^attempt))`.
  /// A step function with no jitter (Appwrite's `1s/5s/10s/60s`) puts every
  /// disconnected client back on the wire at the same instant.
  void _scheduleReconnect() {
    _setState(PraxyConnectionState.reconnecting);
    final attempt = _reconnectAttempts++;
    final capMs = min(_maxBackoff.inMilliseconds, 500 * (1 << min(attempt, 10)));
    final delayMs = _random.nextInt(capMs + 1);
    _reconnectTimer?.cancel();
    _reconnectTimer = Timer(Duration(milliseconds: delayMs), () {
      _reconnectTimer = null;
      unawaited(_connect());
    });
  }

  void _scheduleIdleClose() {
    _idleCloseTimer?.cancel();
    _idleCloseTimer = Timer(_idleGracePeriod, () {
      _idleCloseTimer = null;
      if (_subscriptions.isEmpty) _closeSocketOnly();
    });
  }

  void _closeSocketOnly() {
    _reconnectTimer?.cancel();
    _reconnectTimer = null;
    _reconnectAttempts = 0;
    _teardownChannel();
    _setState(PraxyConnectionState.disconnected);
  }

  void _setState(PraxyConnectionState state) {
    _state = state;
    _connectionController.add(state);
  }

  static Uri _toWebSocketBase(Uri endpoint, String projectId) {
    final scheme = endpoint.scheme == 'https' ? 'wss' : 'ws';
    return endpoint.replace(scheme: scheme).resolve('/v1/realtime').replace(
      queryParameters: {'project': projectId},
    );
  }
}
