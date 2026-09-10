#!/usr/bin/env node
// @ts-check
/**
 * Regenerates the SDK surfaces from `docs/openapi/v1.json`.
 *
 *   node sdk/generator/bin/generate.mjs                 # every target
 *   node sdk/generator/bin/generate.mjs --target dart   # one target
 *   node sdk/generator/bin/generate.mjs --no-format     # skip the language formatters
 *
 * Zero dependencies and no build step, deliberately: Node is preinstalled on every CI runner, so
 * each language's own job can run this with nothing installed and format only its own target,
 * where that formatter already exists. A generator hosted in any one target language would have
 * needed that toolchain in every other language's job.
 *
 * There is no `--check` mode. CI regenerates into the working tree and runs `git diff --exit-code`,
 * mirroring the console's `check:api-types`: one code path means the check can never disagree with
 * what a real regeneration would write.
 */

import { spawnSync } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { parseNamespace } from "../src/spec.mjs";
import { services } from "../src/services.mjs";
import { targets } from "../src/targets/index.mjs";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");

const args = process.argv.slice(2);
const requested = args.includes("--target") ? args[args.indexOf("--target") + 1] : null;
const shouldFormat = !args.includes("--no-format");

if (requested && !targets[requested]) {
  console.error(`Unknown target "${requested}". Known targets: ${Object.keys(targets).join(", ")}.`);
  process.exit(2);
}

const document = JSON.parse(readFileSync(join(repoRoot, "docs/openapi/v1.json"), "utf8"));
const selected = requested ? [targets[requested]] : Object.values(targets);

/** @type {Map<string, string[]>} */
const writtenByTarget = new Map();

for (const target of selected) {
  for (const service of services) {
    const config = service.targets[target.name];
    if (!config) continue; // This service is not offered in this language.

    const operations = parseNamespace(document, service.namespace);
    if (operations.length === 0) {
      console.error(
        `No operations for namespace "${service.namespace}". The document uses operationIds of ` +
          `the form "<namespace>.<method>" — check the spelling.`,
      );
      process.exit(1);
    }

    const relative = target.outputPath(service);
    const absolute = join(repoRoot, relative);
    mkdirSync(dirname(absolute), { recursive: true });
    writeFileSync(absolute, target.render(service, operations, document));
    console.log(`Generated ${relative}`);

    writtenByTarget.set(target.name, [...(writtenByTarget.get(target.name) ?? []), relative]);
  }
}

if (shouldFormat) {
  for (const target of selected) {
    const written = writtenByTarget.get(target.name);
    if (!written || !target.formatCommand) continue;

    const [command, commandArgs, cwd] = target.formatCommand(
      written.map((f) => (cwd_relative(f, target) ?? f)),
    );
    const result = spawnSync(command, commandArgs, { cwd: join(repoRoot, cwd), stdio: "inherit" });
    if (result.error?.code === "ENOENT") {
      // Not installed here. Skipping is correct rather than fatal: the drift gate for this target
      // runs in the job that does have the toolchain, and a Dart-less machine should still be able
      // to regenerate the C# surface.
      console.warn(`Skipped ${target.name} formatting: \`${command}\` is not on PATH.`);
      continue;
    }
    if (result.status !== 0) process.exit(result.status ?? 1);
  }
}

/** Formatters run inside their own toolchain's directory, so paths are rewritten relative to it. */
function cwd_relative(file, target) {
  const [, , cwd] = target.formatCommand([]);
  return file.startsWith(`${cwd}/`) ? file.slice(cwd.length + 1) : file;
}
