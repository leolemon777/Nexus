const INSTRUCTION_KNOWLEDGE = Object.freeze({
  DDRVA: {
    name: "双精度绝对定位",
    group: "轴定位",
    description: "与绝对位置目标相关的脉冲定位指令。",
  },
  DDRVI: {
    name: "双精度相对定位",
    group: "轴定位",
    description: "与相对移动量相关的脉冲定位指令。",
  },
  DSZR: {
    name: "原点回归/原点搜索",
    group: "轴定位",
    description: "与轴回零和原点搜索相关的定位指令。",
  },
});

const DEVICE_CATEGORY_NAMES = Object.freeze({
  "label reference": "标签引用",
  "word/register": "字/寄存器",
  "internal/link/special bit": "内部/链接/特殊位",
  "input/output bit": "输入/输出位",
  "timer/counter": "定时器/计数器",
  unknown: "尚未识别",
});

function arrayOf(value) {
  return Array.isArray(value) ? value : [];
}

function finiteNumber(value, fallback = 0) {
  const number = Number(value);
  return Number.isFinite(number) ? number : fallback;
}

function basename(filePath) {
  const parts = String(filePath ?? "").split(/[\\/]/);
  return parts.at(-1) || "未命名 GX3 项目";
}

function percent(value) {
  return `${(finiteNumber(value) * 100).toFixed(2)}%`;
}

function translateProgramWarning(warning) {
  const text = String(warning ?? "").trim();
  const unnamed = text.match(/^program\s+(\d+):\s+(\d+)\s+POUs?\s+but\s+0\s+names decoded$/i);
  if (unnamed) {
    return `内部程序 ${unnamed[1]} 中有 ${unnamed[2]} 个 POU，但名称尚未解码。`;
  }
  return text || "解析器报告了一条未说明的程序映射警告。";
}

function describeInstruction(entry) {
  const opcode = String(entry?.opcode ?? "?").toUpperCase();
  const known = INSTRUCTION_KNOWLEDGE[opcode];
  return {
    opcode,
    count: finiteNumber(entry?.count),
    partialRows: finiteNumber(entry?.partial_parse_rows),
    name: known?.name ?? "暂未收录中文说明",
    group: known?.group ?? "其他未完整支持指令",
    description: known?.description ?? "解析器保留了该指令，但尚不能完整解释其参数和逻辑。",
  };
}

function inferPurposeHints(result, unsupportedInstructions) {
  const hints = [];
  const opcodes = new Set(unsupportedInstructions.map((entry) => entry.opcode));
  const positioningEvidence = ["DDRVA", "DDRVI", "DSZR", "DRVA", "DRVI", "ZRN", "PLSY", "DPLSY"]
    .filter((opcode) => opcodes.has(opcode));
  const sourceText = `${basename(result?.sourcePath)} ${arrayOf(result?.programMap?.program_files).join(" ")}`;
  if (positioningEvidence.length || /轴|定位|伺服|servo/i.test(sourceText)) {
    const evidence = positioningEvidence.length
      ? `识别到 ${positioningEvidence.join("、")} 等定位相关指令`
      : "项目文件名或程序文件名包含轴控/定位特征";
    hints.push({
      title: "包含轴定位控制逻辑",
      description: "工程很可能涉及脉冲轴的定位、相对移动或回零流程。",
      evidence,
      caution: "这是基于文件名和指令特征的规则推断，请结合 GX Works3 梯形图与设备接线确认。",
    });
  }
  if (!hints.length) {
    hints.push({
      title: "暂时无法从现有证据判断具体工艺用途",
      description: "程序结构已经读取，但缺少可直接归类的指令、标签或注释证据。",
      evidence: "未匹配到已收录的功能特征",
      caution: "可以继续查询关键 M、D、X、Y 或特殊软元件，逐步还原程序关系。",
    });
  }
  return hints;
}

export function buildGx3Presentation(result = {}) {
  const programFiles = arrayOf(result.programMap?.program_files);
  const pous = arrayOf(result.programMap?.pous);
  const warnings = arrayOf(result.programMap?.warnings).map(translateProgramWarning);
  const reliability = result.reliability ?? {};
  const instructionRows = finiteNumber(reliability.instruction_rows);
  const partialRows = finiteNumber(reliability.partial_parse_rows);
  const gapRows = finiteNumber(reliability.gap_rows);
  const gapRate = finiteNumber(reliability.gap_rate);
  const unsupported = arrayOf(reliability.unsupported_instructions).map(describeInstruction);
  const coverageRate = Math.max(0, Math.min(1, 1 - gapRate));
  const deviceTypes = arrayOf(reliability.device_types).map((entry) => ({
    deviceType: String(entry?.device_type ?? "?"),
    category: DEVICE_CATEGORY_NAMES[String(entry?.category ?? "unknown")] ?? String(entry?.category ?? "尚未分类"),
    count: finiteNumber(entry?.count),
    known: String(entry?.status ?? "").toLowerCase() === "known",
  }));
  const qualityTone = gapRows === 0 && unsupported.length === 0
    ? "good"
    : gapRate <= 0.05
      ? "warn"
      : "danger";

  return {
    projectName: basename(result.sourcePath),
    sourcePath: String(result.sourcePath ?? ""),
    sourceUnchanged: Boolean(result.sourceUnchanged),
    programFiles,
    pous: pous.map((pou, index) => ({
      displayName: String(pou?.name ?? "").trim() || `未命名 POU ${index + 1}`,
      programFile: String(pou?.program_file ?? "").trim() || "未关联到已解码程序文件",
      internalId: String(pou?.pou_dir ?? ""),
      lddb: String(pou?.lddb_hex ?? ""),
      decoded: Boolean(String(pou?.name ?? "").trim()),
    })),
    warnings,
    purposeHints: inferPurposeHints(result, unsupported),
    deviceTypes,
    unsupported,
    quality: {
      tone: qualityTone,
      coverageRate,
      coverageLabel: percent(coverageRate),
      instructionRows,
      partialRows,
      gapRows,
      gapRateLabel: percent(gapRate),
      summary: instructionRows
        ? `共读取 ${instructionRows} 条指令记录；${partialRows} 条为部分解析，${gapRows} 条存在解析缺口。`
        : "解析器未提供指令行统计。",
    },
    metrics: [
      { label: "程序文件", value: String(programFiles.length), detail: programFiles.length ? programFiles.join("、") : "未识别" },
      { label: "POU", value: String(pous.length), detail: `${pous.filter((pou) => String(pou?.name ?? "").trim()).length} 个名称已解码` },
      { label: "指令记录", value: String(instructionRows), detail: `${partialRows} 条为部分解析` },
      { label: "记录覆盖率", value: percent(coverageRate), detail: `解析缺口 ${gapRows} 条；不是安全认证` },
    ],
  };
}

function parseXrefRows(text, heading) {
  const lines = String(text ?? "").split(/\r?\n/);
  const start = lines.findIndex((line) => new RegExp(`^${heading}\\s*\\(\\d+\\):`, "i").test(line.trim()));
  if (start < 0) return [];
  const rows = [];
  for (const rawLine of lines.slice(start + 1)) {
    const line = rawLine.trim();
    if (!line) continue;
    if (/^(Writers|Readers)\s*\(\d+\):/i.test(line)) break;
    const match = line.match(/^(\S+)\s+st(\d+)\s+(\S+)\s+(write|read)\b\s*(.*)$/i);
    if (!match) continue;
    const pouMatch = match[1].match(/^pou(\d+)_dir(.+)$/i);
    rows.push({
      location: pouMatch ? `POU ${Number(pouMatch[1]) + 1}（内部程序 ${pouMatch[2]}）` : match[1],
      statement: finiteNumber(match[2]),
      role: match[3],
      direction: match[4].toLowerCase() === "write" ? "写入" : "读取",
      detail: match[5] || "直接引用",
    });
  }
  return rows;
}

export function parseGx3DevicePresentation(result = {}) {
  const indexText = String(result.index ?? "").trim();
  const xrefText = String(result.xref ?? "").trim();
  const firstLine = indexText.split(/\r?\n/).find((line) => line.trim()) ?? "";
  const titleMatch = firstLine.match(/^(\S+)\s*(.*)$/);
  const stats = indexText.match(/occurrences=(\d+)\s+driver_rows=(\d+)\s+condition_uses=(\d+)/i);
  const writers = parseXrefRows(xrefText, "Writers");
  const readers = parseXrefRows(xrefText, "Readers");
  return {
    device: String(result.device ?? titleMatch?.[1] ?? "").toUpperCase(),
    description: String(titleMatch?.[2] ?? "").trim() || "解析器没有提供中文软元件说明。",
    occurrences: stats ? finiteNumber(stats[1]) : writers.length + readers.length,
    driverRows: stats ? finiteNumber(stats[2]) : 0,
    conditionUses: stats ? finiteNumber(stats[3]) : readers.length,
    writers,
    readers,
    hasStructuredData: Boolean(stats || writers.length || readers.length || titleMatch?.[2]),
  };
}

export function formatGx3TechnicalReport(result = {}) {
  const programFiles = arrayOf(result.programMap?.program_files);
  const pous = arrayOf(result.programMap?.pous);
  const warnings = arrayOf(result.programMap?.warnings);
  const lines = [
    "GX Works3 项目只读解析完成",
    `原始文件：${result.sourcePath || "—"}`,
    `原始文件未修改：${result.sourceUnchanged ? "是" : "否"}`,
    `SHA-256：${result.sourceSha256 || "—"}`,
    `私有缓存：${result.analysisRoot || "—"}`,
    `程序文件：${programFiles.length ? programFiles.join("、") : "未识别"}`,
    "",
    `POU（${pous.length}）：`,
  ];
  if (pous.length) {
    for (const pou of pous) {
      lines.push(`- ${pou.name || "名称未解码"} · program=${pou.program_file || "未关联"} · pou_dir=${pou.pou_dir || "—"} · LDDB=${pou.lddb_hex || "—"}`);
    }
  } else {
    lines.push("- 未识别到 POU");
  }
  lines.push("", `程序映射警告（${warnings.length}）：`);
  lines.push(...(warnings.length ? warnings.map((warning) => `- ${warning}`) : ["- 无"]));
  lines.push("", "执行步骤：");
  for (const step of arrayOf(result.steps)) {
    const detail = step.stdout || step.stderr;
    lines.push(`- ${step.name}：完成（${step.durationMs} ms）${detail ? ` · ${String(detail).split(/\r?\n/, 1)[0]}` : ""}`);
  }
  lines.push("", "最终 doctor：", result.doctor || "无输出");
  lines.push("", "解析可靠性（解析器原始结构）：", JSON.stringify(result.reliability ?? {}, null, 2));
  return lines.join("\n");
}

export const GX3_INSTRUCTION_KNOWLEDGE = INSTRUCTION_KNOWLEDGE;
