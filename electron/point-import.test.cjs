"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

const moduleUrl = pathToFileURL(path.join(__dirname, "..", "src", "point-import.js")).href;

test("point imports normalize CSV and JSON while preserving bounded protocol fields", async () => {
  const { parsePointImport } = await import(moduleUrl);
  const csv = parsePointImport({
    fileName: "points.csv",
    byteSize: 120,
    text: "name,unitId,fc,address,quantity,dataType,scale,unit\n温度,2,3,100,2,Float32,0.1,C\n",
  });
  assert.deepEqual(csv, [{
    name: "温度", unitId: 2, fc: 3, address: 100, quantity: 2,
    dataType: "Float32", scale: "0.1", unit: "C",
  }]);

  const json = parsePointImport({
    fileName: "points.json",
    byteSize: 90,
    text: JSON.stringify([{ name: "压力", unitId: 3, fc: 4, address: 200, quantity: 4, dataType: "Signed32" }]),
  });
  assert.equal(json.length, 1);
  assert.equal(json[0].unitId, 3);
  assert.equal(json[0].fc, 4);
  assert.equal(json[0].address, 200);
  assert.equal(json[0].quantity, 4);
  assert.equal(json[0].dataType, "Signed32");
  assert.equal(json[0].scale, "1");
});

test("point import rejects untrusted size, depth, unsafe keys, and invalid fields", async () => {
  const { parsePointImport, readPointImportFile, POINT_IMPORT_LIMITS } = await import(moduleUrl);
  assert.throws(() => parsePointImport({ fileName: "large.json", byteSize: POINT_IMPORT_LIMITS.MAX_IMPORT_BYTES + 1, text: "" }), (error) => {
    assert.equal(error.code, "POINT_IMPORT_TOO_LARGE");
    return true;
  });

  await assert.rejects(() => readPointImportFile({
    name: "large.csv",
    size: POINT_IMPORT_LIMITS.MAX_IMPORT_BYTES + 1,
    get text() { throw new Error("oversized file must not be read"); },
  }), (error) => error.code === "POINT_IMPORT_TOO_LARGE");

  assert.throws(() => parsePointImport({
    fileName: "deep.json",
    text: JSON.stringify({ points: buildDeep(POINT_IMPORT_LIMITS.MAX_JSON_DEPTH + 1) }),
  }), (error) => error.code === "POINT_IMPORT_TOO_DEEP");

  assert.throws(() => parsePointImport({
    fileName: "proto.json",
    text: '{"__proto__":{"polluted":true}}',
  }), (error) => error.code === "POINT_IMPORT_UNSAFE_KEY");

  assert.throws(() => parsePointImport({
    fileName: "points.csv",
    text: "name,unitId,fc,address,quantity,dataType\n温度,999,3,0,1,Float32\n",
  }), (error) => error.code === "POINT_IMPORT_INVALID_FIELD");

  function buildDeep(depth) {
    let value = { name: "leaf" };
    for (let index = 0; index < depth; index += 1) value = { value };
    return value;
  }
});

test("renderer point import uses the guarded service instead of direct JSON or CSV mutation", () => {
  const fs = require("node:fs");
  const renderer = fs.readFileSync(path.join(__dirname, "..", "src", "main.js"), "utf8");
  assert.match(renderer, /import \{ readPointImportFile \} from "\.\/point-import\.js"/);
  const functionMatch = /function importPoints\(\) \{[\s\S]*?\n\}/.exec(renderer);
  assert.ok(functionMatch, "importPoints function is missing");
  assert.match(functionMatch[0], /readPointImportFile\(file\)/);
  assert.doesNotMatch(functionMatch[0], /JSON\.parse\(/);
  assert.doesNotMatch(functionMatch[0], /split\("\\n"\)/);
});
