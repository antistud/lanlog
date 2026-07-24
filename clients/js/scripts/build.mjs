// Build the @logrr/client package: an ESM bundle, a CJS bundle, and type declarations.
import { build } from "esbuild";
import { execSync } from "node:child_process";
import { rmSync } from "node:fs";

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

// Type declarations (emit-only; type-checking is `npm run typecheck`).
execSync("tsc -p tsconfig.json", { stdio: "inherit" });

console.log("@logrr/client build complete → dist/");
