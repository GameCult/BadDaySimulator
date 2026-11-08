// Static build. BASE is the URL path prefix the page will be served under (explicit,
// default /void/baddaysim-pain-box/); pass BASE=./ for a relocatable build.
import { build } from "esbuild";
import { cp, mkdir, readFile, rm, writeFile } from "node:fs/promises";

const base = process.env.BASE ?? "/void/baddaysim-pain-box/";
if (!base.endsWith("/")) throw new Error(`BASE must end with "/": ${base}`);
const out = process.env.OUT ?? "dist";
await rm(out, { recursive: true, force: true });
await mkdir(out, { recursive: true });
await build({ entryPoints: ["src/main.ts"], bundle: true, format: "esm", target: "es2022", minify: true, outfile: `${out}/app.js` });
await cp("src/style.css", `${out}/style.css`);
await writeFile(`${out}/index.html`, (await readFile("index.template.html", "utf8")).replaceAll("%BASE%", base));
console.log(`built ${out}/ with base ${base}`);
