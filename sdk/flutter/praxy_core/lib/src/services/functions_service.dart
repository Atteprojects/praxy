import '../client.dart';
import '../json_utils.dart';
import '../models.dart';

/// The data-plane function-invocation surface (`/v1/functions`). 2 methods: invoke and
/// poll. Deployment management (create/list functions, upload/activate a deployment) is
/// a console/operator concern and stays out of this SDK — see research/flutter-sdk.md's
/// v1.1 section.
///
/// [createExecution] is gated by the function's `execute` role list, empty (deny-by-
/// default) on a new function — a `401` there is expected behavior for every app user
/// until an operator grants a role, not a bug this service works around.
final class FunctionsService {
  const FunctionsService(this._client);

  final Praxy _client;

  /// Invokes a function synchronously by default (waits for the run and returns its
  /// result); pass `async: true` to get a `202`-backed [FunctionExecution] back
  /// immediately and poll its final state with [getExecution].
  Future<FunctionExecution> createExecution(
    String functionId, {
    String? method,
    String? path,
    String? body,
    bool async = false,
  }) async => FunctionExecution.fromJson(
    requireJson(
      await _client.request(
        method: 'POST',
        path: '/v1/functions/$functionId/executions',
        query: async ? {'async': const ['true']} : const {},
        body: {'method': ?method, 'path': ?path, 'body': ?body},
      ),
      functionId,
    ),
  );

  /// Scoped to the caller's own execution — see FunctionEndpoints.cs's
  /// `GetDataPlaneExecution` remarks. Reading someone else's execution id, including one
  /// from a different API key with the identical scope, is a `404`, not a bug to route
  /// around.
  Future<FunctionExecution> getExecution(String functionId, String executionId) async =>
      FunctionExecution.fromJson(
        requireJson(
          await _client.request(
            method: 'GET',
            path: '/v1/functions/$functionId/executions/$executionId',
          ),
          executionId,
        ),
      );
}
