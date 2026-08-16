import 'errors.dart';

/// Every wire endpoint that returns a body returns a JSON object. A `null` here
/// means the server sent an empty or non-object body where one was expected —
/// always a [PraxyDecodeException], never a null-check crash at the call site.
Map<String, dynamic> requireJson(Map<String, dynamic>? json, String context) {
  if (json == null) {
    throw PraxyDecodeException(field: context, expected: 'a JSON object response body');
  }
  return json;
}
