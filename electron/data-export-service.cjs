/**
 * 数据导出服务 —— CSV / JSON 导出。
 * 对标 .NET Nexus 的 DataExportService。
 */

const fs = require("node:fs");
const path = require("node:path");

class DataExportService {
  /**
   * 导出为 CSV。
   * @param {{ rows: Array<Record<string, any>>, filename: string }} param0
   */
  exportCsv({ rows, filename }) {
    if (!Array.isArray(rows) || rows.length === 0) {
      throw new Error("数据行为空");
    }
    const headers = Object.keys(rows[0]);
    const csvLines = [headers.join(",")];
    for (const row of rows) {
      csvLines.push(
        headers
          .map((h) => {
            const val = row[h];
            if (val === null || val === undefined) return "";
            const str = String(val);
            // 含逗号或引号的需转义
            if (str.includes(",") || str.includes('"') || str.includes("\n")) {
              return `"${str.replace(/"/g, '""')}"`;
            }
            return str;
          })
          .join(","),
      );
    }
    const csv = "\uFEFF" + csvLines.join("\n"); // BOM for Excel UTF-8
    const fullPath = this._resolvePath(filename, ".csv");
    fs.writeFileSync(fullPath, csv, "utf8");
    return { path: fullPath, bytes: Buffer.byteLength(csv, "utf8") };
  }

  /**
   * 导出为 JSON。
   */
  exportJson({ data, filename }) {
    const json = JSON.stringify(data, null, 2);
    const fullPath = this._resolvePath(filename, ".json");
    fs.writeFileSync(fullPath, json, "utf8");
    return { path: fullPath, bytes: Buffer.byteLength(json, "utf8") };
  }

  /**
   * 导出 TX/RX 追踪日志为 CSV。
   */
  exportTraceLog({ frames, filename }) {
    const rows = (frames ?? []).map((f) => ({
      time: new Date(f.timestamp).toISOString(),
      direction: f.direction,
      hex: f.hex,
      byteCount: f.bytes?.length ?? 0,
    }));
    return this.exportCsv({ rows, filename });
  }

  _resolvePath(filename, ext) {
    const safeName = (filename || "nexus_export").replace(/[^a-zA-Z0-9_\-]/g, "_");
    const dir = path.join(require("node:os").homedir(), "Desktop");
    return path.join(dir, safeName.endsWith(ext) ? safeName : safeName + ext);
  }
}

module.exports = { DataExportService };
