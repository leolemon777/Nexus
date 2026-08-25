"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");

async function loadPresenter() {
  return import("../src/gx3-presenter.js");
}

function sampleAnalysis() {
  return {
    sourcePath: "E:\\PLC\\Y29 FX5U-轴控制FB块.gx3",
    sourceUnchanged: true,
    sourceSha256: "abc123",
    analysisRoot: "C:\\Temp\\NexusGX3\\abc123",
    cliVersion: "gx3-cli 0.1.0",
    programMap: {
      program_files: ["MAIN", "FBFILE", "d怀(怀"],
      pous: [
        { name: "", program_file: "", pou_dir: "15877400483245685178", lddb_hex: "c0cf2128e32890ed" },
        { name: "ProgPou", program_file: "", pou_dir: "14675959385169700880", lddb_hex: "4e55e3923f7bccd6" },
      ],
      warnings: ["program 15445654971221113397: 1 POUs but 0 names decoded"],
    },
    reliability: {
      device_types: [
        { category: "label reference", count: 155, device_type: "LABEL", status: "known" },
        { category: "internal/link/special bit", count: 12, device_type: "SM", status: "known" },
      ],
      gap_rate: 0.038462,
      gap_rows: 2,
      instruction_rows: 52,
      partial_parse_rows: 36,
      unsupported_instructions: [
        { count: 1, opcode: "DDRVA", partial_parse_rows: 1, status: "unknown" },
        { count: 2, opcode: "DDRVI", partial_parse_rows: 2, status: "unknown" },
        { count: 1, opcode: "DSZR", partial_parse_rows: 1, status: "unknown" },
      ],
    },
    steps: [{ name: "预检查", durationMs: 100, stdout: "doctor ok", stderr: "" }],
    doctor: "doctor ok",
  };
}

test("GX3 presenter turns parser evidence into a cautious Chinese overview", async () => {
  const { buildGx3Presentation } = await loadPresenter();
  const view = buildGx3Presentation(sampleAnalysis());

  assert.equal(view.projectName, "Y29 FX5U-轴控制FB块.gx3");
  assert.equal(view.sourceUnchanged, true);
  assert.equal(view.programFiles.length, 3);
  assert.equal(view.pous[0].displayName, "未命名 POU 1");
  assert.equal(view.pous[1].displayName, "ProgPou");
  assert.equal(view.quality.coverageLabel, "96.15%");
  assert.equal(view.quality.instructionRows, 52);
  assert.equal(view.quality.partialRows, 36);
  assert.equal(view.quality.gapRows, 2);
  assert.equal(view.quality.tone, "warn");
  assert.match(view.purposeHints[0].title, /轴定位/);
  assert.match(view.purposeHints[0].evidence, /DDRVA/);
  assert.equal(view.unsupported[0].name, "双精度绝对定位");
  assert.equal(view.deviceTypes[0].category, "标签引用");
  assert.match(view.warnings[0], /名称尚未解码/);
  assert.match(view.purposeHints[0].caution, /规则推断/);
});

test("GX3 device presenter explains index and xref output without losing locations", async () => {
  const { parseGx3DevicePresentation } = await loadPresenter();
  const view = parseGx3DevicePresentation({
    device: "SM5628",
    index: [
      "SM5628 定位脉冲停止指令(轴1)",
      "occurrences=1 driver_rows=1 condition_uses=0 roles=c:1",
      "",
      "Driver rows:",
    ].join("\n"),
    xref: [
      "SM5628 定位脉冲停止指令(轴1)",
      "",
      "Writers (1):",
      "  pou0_dir1544565497 st640   c         write [Z2 indexed]",
      "",
      "Readers (0):",
    ].join("\n"),
  });

  assert.equal(view.device, "SM5628");
  assert.equal(view.description, "定位脉冲停止指令(轴1)");
  assert.equal(view.occurrences, 1);
  assert.equal(view.driverRows, 1);
  assert.equal(view.writers.length, 1);
  assert.equal(view.writers[0].direction, "写入");
  assert.equal(view.writers[0].statement, 640);
  assert.match(view.writers[0].location, /POU 1/);
  assert.equal(view.writers[0].detail, "[Z2 indexed]");
  assert.equal(view.readers.length, 0);
});

test("GX3 technical report keeps hashes, internal identifiers and raw reliability data", async () => {
  const { formatGx3TechnicalReport } = await loadPresenter();
  const text = formatGx3TechnicalReport(sampleAnalysis());
  assert.match(text, /SHA-256：abc123/);
  assert.match(text, /pou_dir=15877400483245685178/);
  assert.match(text, /DDRVA/);
  assert.match(text, /最终 doctor/);
});
