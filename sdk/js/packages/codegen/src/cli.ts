#!/usr/bin/env node
import { mkdir, writeFile } from "node:fs/promises";
import { dirname } from "node:path";
import { parseArgs } from "node:util";
import { CodegenError, generate } from "./generate";

async function main(argv: string[]): Promise<void> {
  const { values } = parseArgs({
    args: argv,
    options: {
      endpoint: { type: "string" },
      project: { type: "string" },
      "api-key": { type: "string" },
      database: { type: "string" },
      table: { type: "string" },
      output: { type: "string" },
      "class-name": { type: "string" },
    },
  });

  const apiKey = values["api-key"] ?? process.env.PRAXY_API_KEY;
  const missing = ["endpoint", "project", "database", "table", "output"].filter((name) => !values[name as keyof typeof values]);
  if (!apiKey) missing.push("api-key (or set PRAXY_API_KEY)");
  if (missing.length > 0) {
    console.error(`Missing required argument(s): ${missing.join(", ")}\n`);
    console.error(
      "Usage: praxy-codegen --endpoint <url> --project <id> --api-key <key> --database <key> --table <key> --output <path> [--class-name <Name>]",
    );
    process.exitCode = 1;
    return;
  }

  const code = await generate({
    endpoint: values.endpoint!,
    projectId: values.project!,
    apiKey: apiKey!,
    database: values.database!,
    table: values.table!,
    className: values["class-name"],
  });

  await mkdir(dirname(values.output!), { recursive: true });
  await writeFile(values.output!, code, "utf8");
  console.log(`Wrote ${values.output}`);
}

main(process.argv.slice(2)).catch((error: unknown) => {
  if (error instanceof CodegenError) {
    console.error(`praxy-codegen: ${error.message}`);
  } else {
    console.error("praxy-codegen:", error instanceof Error ? error.message : error);
  }
  process.exitCode = 1;
});
