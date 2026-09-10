import 'dart:io';

import 'package:praxy_sdk_gen/praxy_sdk_gen.dart';

/// Regenerates every service listed in `services.dart` into `praxy_core`.
///
/// Run from the workspace root (`sdk/flutter/`): `dart run praxy_sdk_gen`.
///
/// There is no `--check` mode on purpose. The console's `check:api-types` already
/// established the pattern this mirrors: regenerate into the working tree and let
/// `git diff --exit-code` be the gate. One code path means the check can never
/// disagree with what a regeneration would actually write.
Future<void> main(List<String> args) async {
  final documentPath = File('../../docs/openapi/v1.json');
  if (!documentPath.existsSync()) {
    stderr.writeln(
      'Could not find ${documentPath.path} — run this from the sdk/flutter workspace root.',
    );
    exit(2);
  }

  final document = decodeDocument(documentPath.readAsStringSync());
  final written = <String>[];

  for (final spec in services) {
    final target = File('praxy_core/lib/src/services/generated/${fileNameFor(spec)}');
    target.parent.createSync(recursive: true);
    target.writeAsStringSync(generateService(document, spec));
    written.add(target.path);
    stdout.writeln('Generated ${target.path}');
  }

  // Formatted by the real formatter rather than by careful string building. Emitting
  // pre-wrapped code means every future template change risks producing something
  // that is valid Dart but does not match what a developer's editor would write, and
  // the resulting churn shows up as noise in the diff that gates this.
  final format = await Process.run('dart', [
    'format',
    '--page-width=110',
    ...written,
  ]);
  if (format.exitCode != 0) {
    stderr.writeln('dart format failed:\n${format.stderr}');
    exit(format.exitCode);
  }
}
