// Minimal UE4 GVAS .sav reader/writer (properties the game actually uses).
// usage: node gvas.js <file.sav> [--set Name=value ...] [--out out.sav]
const fs = require("fs");

function Reader(buf) {
  let o = 0;
  const R = {
    get off() { return o; },
    set off(v) { o = v; },
    u8: () => buf.readUInt8(o++),
    i32: () => { const v = buf.readInt32LE(o); o += 4; return v; },
    u32: () => { const v = buf.readUInt32LE(o); o += 4; return v; },
    i64: () => { const v = buf.readBigInt64LE(o); o += 8; return v; },
    f32: () => { const v = buf.readFloatLE(o); o += 4; return v; },
    f64: () => { const v = buf.readDoubleLE(o); o += 8; return v; },
    guid: () => { const v = buf.slice(o, o + 16).toString("hex"); o += 16; return v; },
    str: () => {
      const n = buf.readInt32LE(o); o += 4;
      if (n === 0) return "";
      if (n > 0) { const s = buf.toString("latin1", o, o + n - 1); o += n; return s; }
      const chars = -n; const s = buf.toString("utf16le", o, o + chars * 2 - 2); o += chars * 2; return s;
    },
    bytes: (n) => { const v = buf.slice(o, o + n); o += n; return v; },
  };
  return R;
}

function readProp(r, depth) {
  const name = r.str();
  if (name === "None" || name === "") return null;
  const type = r.str();
  const size = Number(r.i64());
  const p = { name, type, size, offset: r.off };
  switch (type) {
    case "IntProperty": r.u8(); p.valueOffset = r.off; p.value = r.i32(); break;
    case "FloatProperty": r.u8(); p.valueOffset = r.off; p.value = r.f32(); break;
    case "BoolProperty": p.valueOffset = r.off; p.value = r.u8() !== 0; r.u8(); break;
    case "StrProperty": case "NameProperty": r.u8(); p.valueOffset = r.off; p.value = r.str(); break;
    case "ObjectProperty": case "SoftObjectProperty": case "ClassProperty":
      r.u8(); p.valueOffset = r.off; p.value = r.str(); break;
    case "TextProperty": r.u8(); p.valueOffset = r.off; p.raw = r.bytes(size); break;
    case "ByteProperty": { p.enumName = r.str(); r.u8(); p.valueOffset = r.off;
      p.value = (p.enumName && p.enumName !== "None") ? r.str() : r.u8(); break; }
    case "EnumProperty": { p.enumName = r.str(); r.u8(); p.valueOffset = r.off; p.value = r.str(); break; }
    case "StructProperty": { p.structType = r.str(); p.structGuid = r.guid(); r.u8(); p.valueOffset = r.off;
      if (p.structType === "Guid" || p.structType === "DateTime" || p.structType === "Vector" ||
          p.structType === "Rotator" || p.structType === "LinearColor" || p.structType === "Color" ||
          p.structType === "IntPoint" || p.structType === "Vector2D" || p.structType === "Quat") {
        p.raw = r.bytes(size);
      } else { // user struct: nested properties until None
        p.fields = []; let f; while ((f = readProp(r, depth + 1))) p.fields.push(f);
      }
      break; }
    case "ArrayProperty": { p.inner = r.str(); r.u8(); p.valueOffset = r.off; const start = r.off;
      p.count = r.i32();
      if (p.inner === "StructProperty") {
        p.elemName = r.str(); p.elemType = r.str(); const innerSize = Number(r.i64());
        p.elemStruct = r.str(); p.elemGuid = r.guid(); r.u8();
        p.elems = [];
        for (let i = 0; i < p.count; i++) { const fields = []; let f; while ((f = readProp(r, depth + 1))) fields.push(f); p.elems.push(fields); }
      } else {
        p.elems = [];
        for (let i = 0; i < p.count; i++) {
          if (p.inner === "IntProperty") p.elems.push(r.i32());
          else if (p.inner === "FloatProperty") p.elems.push(r.f32());
          else if (p.inner === "BoolProperty") p.elems.push(r.u8() !== 0);
          else if (p.inner === "StrProperty" || p.inner === "NameProperty") p.elems.push(r.str());
          else if (p.inner === "ByteProperty") p.elems.push(r.u8());
          else if (p.inner === "ObjectProperty" || p.inner === "SoftObjectProperty") p.elems.push(r.str());
          else { r.off = start + size; break; }
        }
      }
      r.off = start + size;   // trust the declared size
      break; }
    default:
      p.raw = r.bytes(size); p.unknown = true; break;
  }
  return p;
}

function parse(file) {
  const buf = fs.readFileSync(file);
  const r = Reader(buf);
  const magic = buf.toString("latin1", 0, 4);
  if (magic !== "GVAS") throw new Error("not a GVAS save (magic=" + JSON.stringify(magic) + ")");
  r.off = 4;
  // GVAS header: magic, SavegameFileVersion, PackageFileUE4Version, FEngineVersion, custom versions, class.
  const hdr = { saveVer: r.i32(), pkgVer: r.i32() };
  // engine version: u16 major, u16 minor, u16 patch, u32 build, FString buildId
  hdr.major = buf.readUInt16LE(r.off); r.off += 2;
  hdr.minor = buf.readUInt16LE(r.off); r.off += 2;
  hdr.patch = buf.readUInt16LE(r.off); r.off += 2;
  hdr.build = r.u32();
  hdr.buildId = r.str();
  hdr.customFormat = r.i32();
  hdr.customCount = r.i32();
  for (let i = 0; i < hdr.customCount; i++) { r.guid(); r.i32(); }
  hdr.saveClass = r.str();
  const props = [];
  let p, err = null;
  try { while ((p = readProp(r, 0))) props.push(p); } catch (e) { err = e.message; }
  return { buf, hdr, props, endOff: r.off, err, trailing: buf.length - r.off };
}

function fmt(p, indent) {
  const pad = "  ".repeat(indent);
  let v;
  if (p.fields) v = "{\n" + p.fields.map(f => fmt(f, indent + 1)).join("\n") + "\n" + pad + "}";
  else if (p.elems && p.elems.length && Array.isArray(p.elems[0])) v = `[${p.count} structs]`;
  else if (p.elems) v = `[${p.count}] ` + JSON.stringify(p.elems).slice(0, 160);
  else if (p.raw) v = `<${p.raw.length}b ${p.raw.slice(0, 16).toString("hex")}>`;
  else v = JSON.stringify(p.value);
  return `${pad}${p.name} (${p.type}${p.structType ? ":" + p.structType : ""}${p.inner ? "<" + p.inner + ">" : ""}) = ${v}`;
}

if (require.main === module) {
  const file = process.argv[2];
  const s = parse(file);
  console.log(`file: ${file} (${s.buf.length} bytes)`);
  console.log(`class: ${s.hdr.saveClass}`);
  console.log(`engine: ${s.hdr.major}.${s.hdr.minor}.${s.hdr.patch} build ${s.hdr.build} "${s.hdr.buildId}" saveVer=${s.hdr.saveVer} pkgVer=${s.hdr.pkgVer}`);
  console.log(`properties: ${s.props.length}`);
  for (const p of s.props) console.log(fmt(p, 1));
  console.log(`parsed to offset ${s.endOff}; trailing bytes: ${s.trailing}`);
  if (s.err) console.log(`PARSE ERROR: ${s.err}`);
}
module.exports = { parse };
