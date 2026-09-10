import 'dart:io';

import 'package:args/args.dart';
import 'package:praxy_codegen/praxy_codegen.dart';

/// Emits typed `Col<T>` constants for one table into a committed Dart file:
///
/// ```
/// dart run praxy_codegen \
///   --endpoint http://localhost:5090 --project <projectId> --api-key <key> \
///   --database main --table todos --output lib/db/todos_columns.dart
/// ```
///
/// [apiKey] may also come from the `PRAXY_API_KEY` environment variable instead of
/// `--api-key`, so it never has to sit in shell history.
Future<void> main(List<String> arguments) async {
  final parser = ArgParser()
    ..addOption('endpoint', help: 'Praxy API base URL, e.g. http://localhost:5090', mandatory: true)
    ..addOption('project', help: 'Project id (X-Praxy-Project)', mandatory: true)
    ..addOption('api-key', help: 'API key with the databases.read scope; falls back to \$PRAXY_API_KEY')
    ..addOption('database', help: 'Database key', mandatory: true)
    ..addOption('table', help: 'Table key', mandatory: true)
    ..addOption('output', help: 'Output .dart file path', mandatory: true)
    ..addOption('class-name', help: 'Override the generated class name')
    ..addFlag('help', abbr: 'h', negatable: false);

  final ArgResults args;
  try {
    args = parser.parse(arguments);
  } on FormatException catch (error) {
    stderr.writeln(error.message);
    stderr.writeln(parser.usage);
    exitCode = 64;
    return;
  }

  if (args.flag('help')) {
    print(parser.usage);
    return;
  }

  final apiKey = args.option('api-key') ?? Platform.environment['PRAXY_API_KEY'];
  if (apiKey == null || apiKey.isEmpty) {
    stderr.writeln('An API key is required: pass --api-key or set PRAXY_API_KEY.');
    exitCode = 64;
    return;
  }

  try {
    await generate(
      CodegenOptions(
        endpoint: args.option('endpoint')!,
        projectId: args.option('project')!,
        apiKey: apiKey,
        databaseKey: args.option('database')!,
        tableKey: args.option('table')!,
        outputPath: args.option('output')!,
        className: args.option('class-name'),
      ),
    );
    print('Wrote ${args.option('output')}');
  } on CodegenException catch (error) {
    stderr.writeln(error.message);
    exitCode = 1;
  }
}
