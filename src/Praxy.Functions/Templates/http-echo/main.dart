import 'dart:convert';

/// The true minimal starter — nothing to configure, nothing to deploy first. Echoes back exactly
/// what Praxy's invocation envelope handed this function: the triggering request's method, path and
/// raw body (see docs/functions-runtimes.md for the full `context` shape).
Future<Map<String, dynamic>> handler(Map<String, dynamic> context) async {
  return {
    'statusCode': 200,
    'body': jsonEncode({
      'method': context['method'],
      'path': context['path'],
      'body': context['body'],
    }),
    'headers': {'content-type': 'application/json'},
  };
}
