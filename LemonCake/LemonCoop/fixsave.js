// Restore the four "upgrade purchased" arrays in a Lemon Cake save.
// usage: node fixsave.js <in.sav> <out.sav> [--verify-only]
const fs = require("fs");
const { parse } = require("./gvas.js");

const TARGETS = [
  "SV_IsStoreUpgradePurchased",
  "SV_IsKitchenUpgradePurchased",
  "SV_IsGreenhouseUpgradePurchased",
  "SV_IsBedroomUpgradePurchased",
];

const [,, inFile, outFile] = process.argv;
const verifyOnly = process.argv.includes("--verify-only");
const s = parse(inFile);
if (s.err) { console.error("refusing to patch: source does not parse cleanly: " + s.err); process.exit(1); }

const buf = Buffer.from(s.buf);   // copy
let changed = 0;
for (const name of TARGETS) {
  const p = s.props.find(x => x.name === name);
  if (!p) { console.error(`missing property ${name}`); process.exit(1); }
  if (p.type !== "ArrayProperty" || p.inner !== "BoolProperty") {
    console.error(`${name}: unexpected type ${p.type}<${p.inner}>`); process.exit(1);
  }
  // layout from valueOffset: int32 count, then one byte per element
  const count = buf.readInt32LE(p.valueOffset);
  if (count !== p.count || p.size !== 4 + count) {
    console.error(`${name}: size mismatch (count=${count}, declared size=${p.size})`); process.exit(1);
  }
  const start = p.valueOffset + 4;
  const before = [];
  for (let i = 0; i < count; i++) before.push(buf[start + i]);
  if (verifyOnly) { console.log(`${name}: [${before.join(",")}]`); continue; }
  for (let i = 0; i < count; i++) { if (buf[start + i] !== 1) { buf[start + i] = 1; changed++; } }
  console.log(`${name}: [${before.join(",")}] -> all 1 (${count} entries)`);
}
if (verifyOnly) process.exit(0);

if (buf.length !== s.buf.length) { console.error("length changed; aborting"); process.exit(1); }
fs.writeFileSync(outFile, buf);

// re-parse the result as an independent check
const v = parse(outFile);
if (v.err) { console.error("patched file does not parse: " + v.err); process.exit(1); }
const same = ["SV_Level","SV_Day","SV_Money","SV_Experience","SV_WalkSpeed","SV_SprintRecharge"].every(n => {
  const a = s.props.find(x => x.name === n), b = v.props.find(x => x.name === n);
  return a && b && String(a.value) === String(b.value);
});
console.log(`\nwrote ${outFile} (${buf.length} bytes, ${changed} bytes changed)`);
console.log(`re-parse: ${v.props.length} properties, trailing ${v.trailing}, progress values preserved: ${same}`);
for (const n of TARGETS) {
  const b = v.props.find(x => x.name === n);
  console.log(`  ${n} = [${b.elems.map(e => e ? 1 : 0).join(",")}]`);
}
