import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * `praxyMiddleware()` (`@praxy/nextjs`) runs on Edge Runtime by default and pulls this package in
 * transitively — a `node:*` import creeping into a shared code path is easy to miss locally and
 * only surfaces at deploy time. Statically scan every source file for one instead of hoping nobody
 * adds one; this is a build-time (Node) test asserting about the *runtime* (edge) source, not a
 * claim that vitest itself runs on the edge.
 */
function collectSourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return collectSourceFiles(path);
    return entry.name.endsWith(".ts") ? [path] : [];
  });
}

const FORBIDDEN_IMPORT = /from\s+["'](node:|fs|path|crypto|buffer|stream|os$)/;

describe("edge-runtime safety", () => {
  it("src/ never imports a Node-only module", () => {
    const files = collectSourceFiles(join(__dirname, "..", "src"));
    expect(files.length).toBeGreaterThan(0);

    const offenders = files
      .map((file) => ({ file, content: readFileSync(file, "utf8") }))
      .filter(({ content }) => FORBIDDEN_IMPORT.test(content));

    expect(offenders.map((o) => o.file)).toEqual([]);
  });

  it("only uses globals available in browsers/edge runtimes/Node 22+ (fetch, WebSocket, URL — no Buffer/process/require)", () => {
    const files = collectSourceFiles(join(__dirname, "..", "src"));
    const forbiddenGlobal = /\b(Buffer|require|__dirname|__filename)\b/;

    const offenders = files
      .map((file) => ({ file, content: readFileSync(file, "utf8") }))
      .filter(({ content }) => forbiddenGlobal.test(content));

    expect(offenders.map((o) => o.file)).toEqual([]);
  });
});
