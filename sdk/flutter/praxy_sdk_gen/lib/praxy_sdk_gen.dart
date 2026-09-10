/// Generates `praxy_core`'s mechanical service surface from `docs/openapi/v1.json`.
///
/// Repo-internal tooling, not a published package and not something an app depends
/// on. See `sdk/flutter/praxy_sdk_gen/README.md` for what it does and does not
/// generate, and why that line is drawn where it is.
library;

export 'src/emitter.dart' show GeneratorException, ServiceSpec, generateService;
export 'src/services.dart' show decodeDocument, fileNameFor, services;
export 'src/spec.dart' show Operation, Parameter, SchemaRef, parseNamespace, resolveSchema;
