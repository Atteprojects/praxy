using System.Formats.Tar;
using System.Text;

namespace Praxy.Functions;

/// <summary>
/// Builds the Docker build context for a deployment: the uploaded user tar plus a generated
/// Dockerfile and a minimal HTTP wrapper server implementing Praxy's own runtime contract
/// (research/dotnet-stack.md documents the decision — the open-runtimes wire contract is adopted
/// for the parts that are actually public: the <c>x-open-runtimes-secret</c> header and the
/// build/start phase split; the wrapper's request/response envelope and the per-language function
/// signature are Praxy's own minimal design, not a port of open-runtimes' per-language SDKs).
/// Uses <c>System.Formats.Tar</c> (.NET 7+ BCL, no package) for both reading the uploaded tar and
/// writing the combined context.
/// </summary>
public static class RuntimeTemplates
{
    public const int RuntimePort = 3000;
    public const string SecretHeader = "x-open-runtimes-secret";
    public const string HealthPath = "/_health";

    public static async Task<MemoryStream> BuildContextAsync(
        string runtime, string entrypoint, string baseImage, Stream userTar, CancellationToken ct)
    {
        var output = new MemoryStream();
        await using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            await using (var reader = new TarReader(userTar, leaveOpen: true))
            {
                // Re-emit a fresh minimal entry per file rather than forwarding the original
                // TarEntry object: macOS's bsdtar embeds a PAX extended attribute
                // ("com.apple.provenance") that the Linux side of the Docker daemon's context
                // extraction rejects outright ("lsetxattr ... operation not supported"), turning
                // a perfectly valid upload into a build failure before the Dockerfile ever runs.
                // Dropping every attribute but name/mode/data sidesteps that and any similar
                // platform-specific tar quirk from whatever tool produced the upload.
                while (await reader.GetNextEntryAsync(copyData: true, ct) is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.Directory))
                        continue;
                    var clean = new PaxTarEntry(entry.EntryType, entry.Name) { Mode = entry.Mode };
                    if (entry.DataStream is not null)
                        clean.DataStream = entry.DataStream;
                    await writer.WriteEntryAsync(clean, ct);
                }
            }

            await WriteTextEntryAsync(writer, "Dockerfile", Dockerfile(runtime, entrypoint, baseImage), ct);
            await WriteTextEntryAsync(
                writer,
                runtime == FunctionRuntimes.Dart ? "_praxy_server.dart" : "_praxy_server.js",
                runtime == FunctionRuntimes.Dart ? DartWrapper(entrypoint) : NodeWrapper(),
                ct);
        }
        output.Position = 0;
        return output;
    }

    private static async Task WriteTextEntryAsync(TarWriter writer, string name, string content, CancellationToken ct)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        };
        await writer.WriteEntryAsync(entry, ct);
    }

    private static string Dockerfile(string runtime, string entrypoint, string baseImage) => runtime switch
    {
        FunctionRuntimes.Dart => $"""
            FROM {baseImage}
            WORKDIR /function
            COPY . .
            RUN if [ -f pubspec.yaml ]; then dart pub get; fi
            ENV PRAXY_ENTRYPOINT={entrypoint}
            EXPOSE {RuntimePort}
            CMD ["dart", "run", "_praxy_server.dart"]
            """,
        FunctionRuntimes.Node => $"""
            FROM {baseImage}
            WORKDIR /function
            COPY . .
            RUN if [ -f package.json ]; then npm install --omit=dev; fi
            ENV PRAXY_ENTRYPOINT={entrypoint}
            EXPOSE {RuntimePort}
            CMD ["node", "_praxy_server.js"]
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(runtime), runtime, "Unknown runtime."),
    };

    /// <summary>
    /// Dart has no dynamic <c>require</c>, so the entrypoint has to be a static import — this
    /// wrapper is generated fresh per build with the (validated, traversal-free) entrypoint path
    /// interpolated in. Contract: the user file exports <c>Future&lt;Map&lt;String, dynamic&gt;&gt;
    /// main(Map&lt;String, dynamic&gt; context)</c>.
    /// </summary>
    private static string DartWrapper(string entrypoint) => $$"""
        import 'dart:convert';
        import 'dart:io';
        import './{{entrypoint}}' as user_fn;

        void main() async {
          final secret = Platform.environment['OPEN_RUNTIMES_SECRET'] ?? '';
          final server = await HttpServer.bind(InternetAddress.anyIPv4, 3000);
          await for (final request in server) {
            _handle(request, secret);
          }
        }

        Future<void> _handle(HttpRequest request, String secret) async {
          try {
            if (request.uri.path == '/_health') {
              request.response.statusCode = 200;
              request.response.write('OK');
              await request.response.close();
              return;
            }
            if (request.headers.value('x-open-runtimes-secret') != secret) {
              request.response.statusCode = 401;
              await request.response.close();
              return;
            }
            final raw = await utf8.decoder.bind(request).join();
            Map<String, dynamic> envelope = {};
            try {
              if (raw.isNotEmpty) envelope = jsonDecode(raw) as Map<String, dynamic>;
            } catch (_) {}

            final logs = <String>[];
            Map<String, dynamic> result = {};
            String? error;
            try {
              result = await runZoned<Future<Map<String, dynamic>>>(
                () => user_fn.main(envelope),
                zoneSpecification: ZoneSpecification(
                  print: (self, parent, zone, line) => logs.add(line),
                ),
              );
            } catch (e, st) {
              error = '$e\n$st';
            }

            request.response.statusCode = 200;
            request.response.headers.contentType = ContentType.json;
            request.response.write(jsonEncode({
              'statusCode': error == null ? (result['statusCode'] ?? 200) : 500,
              'body': error == null ? (result['body'] ?? '') : '',
              'headers': error == null ? (result['headers'] ?? {}) : {},
              'logs': logs.join('\n'),
              'errors': error ?? '',
            }));
            await request.response.close();
          } catch (_) {
            try {
              request.response.statusCode = 500;
              await request.response.close();
            } catch (_) {}
          }
        }
        """;

    /// <summary>
    /// Node's <c>require</c> can load an arbitrary path at runtime, so the entrypoint travels as
    /// an env var (<c>PRAXY_ENTRYPOINT</c>, set by the generated Dockerfile) instead of being baked
    /// into the wrapper source. Contract: the user file's default (or sole) export is
    /// <c>async (context) => ({ statusCode, body, headers })</c>.
    /// </summary>
    private static string NodeWrapper() => """
        'use strict';
        const http = require('http');
        const path = require('path');

        const entrypoint = process.env.PRAXY_ENTRYPOINT || 'index.js';
        const loaded = require(path.join(__dirname, entrypoint));
        const fn = typeof loaded === 'function' ? loaded : loaded.default;
        const secret = process.env.OPEN_RUNTIMES_SECRET || '';

        const server = http.createServer((req, res) => {
          if (req.url === '/_health') { res.writeHead(200); res.end('OK'); return; }
          if (req.headers['x-open-runtimes-secret'] !== secret) { res.writeHead(401); res.end(); return; }

          const chunks = [];
          req.on('data', (c) => chunks.push(c));
          req.on('end', async () => {
            const raw = Buffer.concat(chunks).toString('utf8');
            let envelope = {};
            try { envelope = raw ? JSON.parse(raw) : {}; } catch (_e) {}

            const logs = [];
            const origLog = console.log;
            console.log = (...args) => logs.push(args.map(String).join(' '));
            let statusCode = 200, body = '', headers = {}, errors = '';
            try {
              const result = (await fn(envelope)) || {};
              statusCode = result.statusCode ?? 200;
              body = result.body ?? '';
              headers = result.headers ?? {};
            } catch (e) {
              statusCode = 500;
              errors = (e && e.stack) ? String(e.stack) : String(e);
            } finally {
              console.log = origLog;
            }

            res.writeHead(200, { 'content-type': 'application/json' });
            res.end(JSON.stringify({ statusCode, body, headers, logs: logs.join('\n'), errors }));
          });
        });
        server.listen(3000, '0.0.0.0');
        """;
}
