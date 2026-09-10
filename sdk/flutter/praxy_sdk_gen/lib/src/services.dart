import 'dart:convert';

import 'emitter.dart';

Map<String, dynamic> decodeDocument(String json) => jsonDecode(json) as Map<String, dynamic>;

String fileNameFor(ServiceSpec spec) => '${spec.namespace}_service.dart';

/// Every service generated into `praxy_core`, and the schema-to-Dart decisions behind
/// each. Adding a service here is the whole change; the generator does the rest.
///
/// Only the **server** surface is generated so far. The client-facing services
/// (`account`, `tables`, `teams`, `functions`, `storage`) stay hand-written: they
/// carry the query DSL, row codecs, session persistence and upload handling that a
/// generator would flatten. That is the deliberate divergence from Appwrite, who
/// generate everything because sixteen SDKs cannot be hand-maintained — see
/// docs/research/sdk-generation.md.
const services = <ServiceSpec>[
  ServiceSpec(
    namespace: 'users',
    className: 'UsersService',
    description:
        'Server-side app-user administration (`/v1/users`) — the surface an API key '
        'reaches and an end-user session never should. Create, inspect, update and '
        'delete a project\'s app users, and revoke their sessions.\n'
        '///\n'
        '/// Requires a client constructed with an `apiKey`; every method here needs the\n'
        '/// `users.read`/`users.write` key scopes. GENERATED from the OpenAPI document.',
    modelTypes: {
      'AppUserResponse': 'AppUser',
      'SessionResponse': 'AppSession',
      'SessionListResponse': 'SessionList',
    },
    generateModels: {'AppUserListResponse': 'AppUserList'},
  ),
];
