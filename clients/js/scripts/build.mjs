// Build the @logrr/client package: ESM + CJS bundles, a browser <script> global, and types.
import { build } from "esbuild";
import { execSync } from "node:child_process";
import { copyFileSync, mkdirSync, rmSync } from "node:fs";

rmSync(new URL("../dist", import.meta.url), { recursive: true, force: true });

const shared = {
  entryPoints: ["src/index.ts"],
  bundle: true,
  platform: "neutral", // runs in Node, browsers, Deno, and Bun
  target: "es2020",
  sourcemap: true,
};

await build({ ...shared, format: "esm", outfile: "dist/index.js" });
await build({ ...shared, format: "cjs", outfile: "dist/index.cjs" });

// Browser <script> build: exposes a global `Logrr` (e.g. `new Logrr.LogrrClient({...})`).
await build({ ...shared, format: "iife", globalName: "Logrr", outfile: "dist/logrr.global.js" });
await build({
  ...shared,
  format: "iife",
  globalName: "Logrr",
  minify: true,
  sourcemap: false,
  outfile: "dist/logrr.global.min.js",
});

// Also drop the minified global into the server's wwwroot so a self-hosted Logrr serves it
// at /logrr.js — the "just add a <script> tag" path with no CDN or bundler needed.
const wwwroot = new URL("../../../src/Logrr.Server/wwwroot/", import.meta.url);
mkdirSync(wwwroot, { recursive: true });
copyFileSync(new URL("../dist/logrr.global.min.js", import.meta.url), new URL("logrr.js", wwwroot));

// Type declarations (emit-only; type-checking is `npm run typecheck`).
execSync("tsc -p tsconfig.json", { stdio: "inherit" });

console.log("@logrr/client build complete → dist/ (+ src/Logrr.Server/wwwroot/logrr.js)");
