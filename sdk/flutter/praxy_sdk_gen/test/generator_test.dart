import 'dart:convert';
import 'dart:io';

import 'package:praxy_sdk_gen/praxy_sdk_gen.dart';
import 'package:test/test.dart';

/// A miniature document with the shapes that actually matter: a path parameter, a
/// body with one required and one optional property, a 204, a `$ref` response, and
/// .NET's integer-or-string union.
Map<String, dynamic> fixture() => jsonDecode(r'''
{
  "paths": {
    "/v1/things": {
      "get": {"operationId": "things.list",
        "responses": {"200": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/ThingListResponse"}}}}}},
      "post": {"operationId": "things.create",
        "requestBody": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/CreateThingRequest"}}}},
        "responses": {"201": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/ThingResponse"}}}}}}
    },
    "/v1/things/{thingId}": {
      "delete": {"operationId": "things.delete", "parameters": [{"name": "thingId", "in": "path", "required": true, "schema": {"type": "string"}}],
        "responses": {"204": {"description": "No Content"}}}
    }
  },
  "components": {"schemas": {
    "ThingResponse": {"type": "object", "required": ["id"], "properties": {"id": {"type": "string"}}},
    "ThingListResponse": {"type": "object", "required": ["total", "things"],
      "properties": {"total": {"type": ["integer", "string"], "format": "int32"},
                     "things": {"type": "array", "items": {"$ref": "#/components/schemas/ThingResponse"}},
                     "cursor": {"type": "string"}}},
    "CreateThingRequest": {"type": "object", "required": ["name"],
      "properties": {"name": {"type": "string"}, "colour": {"type": "string"}}}
  }}
}
''') as Map<String, dynamic>;

const spec = ServiceSpec(
  namespace: 'things',
  className: 'ThingsService',
  description: 'Things.',
  modelTypes: {'ThingResponse': 'Thing'},
  generateModels: {'ThingListResponse': 'ThingList'},
);

void main() {
  test('emits one method per operation, named from the operationId', () {
    final out = generateService(fixture(), spec);

    expect(out, contains('Future<ThingList> list()'));
    expect(out, contains('Future<Thing> create('));
    expect(out, contains('Future<void> delete(String thingId)'));
  });

  test('a path parameter becomes a positional argument interpolated into the path', () {
    final out = generateService(fixture(), spec);

    expect(out, contains(r"path: '/v1/things/$thingId'"));
  });

  test('a required body property is required, an optional one is null-aware', () {
    final out = generateService(fixture(), spec);

    expect(out, contains('required String name'));
    expect(out, contains('String? colour'));
    // `?colour` omits the key entirely when null rather than sending an explicit
    // null — the wire shape the API expects, and what the hand-written services do.
    expect(out, contains("'colour': ?colour"));
  });

  test('a 204 operation returns Future<void> and decodes nothing', () {
    final out = generateService(fixture(), spec);

    expect(out, contains('Future<void> delete(String thingId) async {'));
    expect(out, isNot(contains('void.fromJson')));
  });

  test('reuses a hand-written model instead of generating a second one', () {
    // The whole "generate the mechanical surface, hand-write what has design in it"
    // split depends on this: praxy_core already models these, and a generated
    // duplicate would either shadow the good type or silently diverge from it.
    final out = generateService(fixture(), spec);

    expect(out, contains('Future<Thing> create('));
    expect(out, isNot(contains('final class Thing {')));
    expect(out, contains('final class ThingList {'));
  });

  test("resolves .NET's integer-or-string union to int", () {
    // The document types every int32 as ["integer","string"] though the wire only
    // ever sends a number. Taking the union literally would type every `total` in
    // the SDK as Object; openapi-typescript resolves it to number for the console.
    final out = generateService(fixture(), spec);

    expect(out, contains('final int total;'));
  });

  test('an unmapped schema fails loudly rather than guessing a type', () {
    const incomplete = ServiceSpec(
      namespace: 'things',
      className: 'ThingsService',
      description: 'Things.',
      modelTypes: {},
      generateModels: {'ThingListResponse': 'ThingList'},
    );

    expect(
      () => generateService(fixture(), incomplete),
      throwsA(
        isA<GeneratorException>().having((e) => e.message, 'message', contains('ThingResponse')),
      ),
    );
  });

  test('a documentGaps entry naming no real operation fails', () {
    // A stale note is worse than none: it asserts something untrue about the
    // document, and it would survive the gap actually being fixed.
    const stale = ServiceSpec(
      namespace: 'things',
      className: 'ThingsService',
      description: 'Things.',
      modelTypes: {'ThingResponse': 'Thing'},
      generateModels: {'ThingListResponse': 'ThingList'},
      documentGaps: {'things.search': 'gone'},
    );

    expect(
      () => generateService(fixture(), stale),
      throwsA(isA<GeneratorException>().having((e) => e.message, 'message', contains('things.search'))),
    );
  });

  test('an unknown namespace fails rather than emitting an empty service', () {
    const missing = ServiceSpec(
      namespace: 'widgets',
      className: 'WidgetsService',
      description: 'Widgets.',
      modelTypes: {},
    );

    expect(() => generateService(fixture(), missing), throwsA(isA<GeneratorException>()));
  });

  test('the committed users service is what the real document produces', () {
    // The drift gate in miniature: CI regenerates and `git diff --exit-code`s, but
    // this fails inside `dart test` too, where the cause is easier to read.
    final document = decodeDocument(File('../../docs/openapi/v1.json').readAsStringSync());
    final generated = generateService(document, services.single);

    expect(generated, contains('final class UsersService'));
    expect(generated, contains('Future<AppUser> updateEmail(String userId, {required String email})'));
    expect(generated, contains('Known gap in the OpenAPI document'));
  });
}
