"use strict";

/**
 * Modbus RTU 一键扫描服务 —— 站号 × 波特率 × 奇偶校验 组合扫描。
 *
 * 编排:外层「波特率×校验」档位(常见档在前),内层完整遍历站号;
 * 探测帧 = readHoldingRegistersOnce(FC03 读 1 点)。不能用“首站离线”推断整档
 * 离线：Modbus RTU 从站只响应自己的站号，实际设备可能是 2..247 任意地址。
 * 与 main.cjs 内联的 scanBaudRate/scanSerialStations 不同,本模块为独立服务
 * (依赖注入),可被 node --test 直接 require(spec-plan-serial-one-key-scan.md A1)。
 *
 * 设计要点:
 * - 探测配置自带全部必填字段(portName/baudRate/dataBits/parity/stopBits/
 *   flowControl/readTimeoutMs/writeTimeoutMs/dtrMode/rtsMode),不依赖"当前已打开
 *   的串口配置"——串口从未打开时也能扫(修复 scanBaudRate 的 originalConfig 隐患);
 * - 扫描前保存原状态(wasOpen+config),结束/取消/异常后恢复;
 * - firstHit 任一站号应答即停;full 收集全部命中;
 * - 单档 open 失败重试 1 次后跳档(快速开关串口的 CH340 容错),档间 50ms 间隔;
 * - hits 元素结构与 scanSerialStations 的 found 兼容(渲染层 renderScanResults 可复用),
 *   并补 firstResponseMs。
 */

const DEFAULT_SCAN_BAUDS = [9600, 19200, 38400, 115200, 4800];
const DEFAULT_SCAN_PARITIES = ["none", "even", "odd"];
const COMBO_GAP_MS = 50;
const OPEN_RETRY_DELAY_MS = 80;

const PARITY_LABELS = { none: "8N1", even: "8E1", odd: "8O1" };

function sleep(ms) {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

function parityLabel(parity) {
  return PARITY_LABELS[parity] ?? String(parity).toUpperCase();
}

function createModbusScanService({ serialService, readHoldingRegistersOnce }) {
  let cancelRequested = false;

  function requestCancel() {
    cancelRequested = true;
  }

  async function restoreOriginal(originalConfig, wasOpen, calls) {
    try {
      if (serialService.getStatus()?.isOpen) await serialService.close();
      if (wasOpen && originalConfig) await serialService.open(originalConfig);
      calls.push({ step: "restore", reopened: Boolean(wasOpen && originalConfig) });
    } catch {
      calls.push({ step: "restore-failed" });
    }
  }

  async function openCombo(comPort, baud, parity, timeoutMs, lineConfig, calls) {
    const config = {
      portName: comPort,
      baudRate: baud,
      dataBits: 8,
      parity,
      stopBits: 1,
      flowControl: lineConfig.flowControl,
      readTimeoutMs: timeoutMs,
      writeTimeoutMs: timeoutMs,
      dtrMode: lineConfig.dtrMode,
      rtsMode: lineConfig.rtsMode,
    };
    for (let attempt = 1; attempt <= 2; attempt++) {
      try {
        if (serialService.getStatus()?.isOpen) await serialService.close();
        await serialService.open(config);
        calls.push({ step: "open", baudRate: baud, parity, attempt });
        return true;
      } catch {
        if (attempt === 1) await sleep(OPEN_RETRY_DELAY_MS);
      }
    }
    calls.push({ step: "open-failed", baudRate: baud, parity });
    return false;
  }

  async function probeStation(ctx, unitId, timeoutMs) {
    try {
      const result = await readHoldingRegistersOnce(ctx, {
        unitId,
        startAddress: 0,
        quantity: 1,
        timeoutMs,
      });
      if (result.ok) {
        return { online: true, exception: null, firstResponseMs: result.elapsedMs ?? null };
      }
      if (result.error?.code === "MODBUS_EXCEPTION" || result.exceptionCode != null) {
        return {
          online: true,
          exception: result.exceptionCode ?? null,
          firstResponseMs: result.elapsedMs ?? null,
        };
      }
      return { online: false, exception: null, firstResponseMs: null };
    } catch {
      return { online: false, exception: null, firstResponseMs: null };
    }
  }

  /**
   * 一键扫描。args:
   *   comPort        必填,如 "COM3"
   *   stationStart   默认 1;stationEnd 默认 16
   *   bauds          默认 [9600,19200,38400,115200,4800](常见档在前)
   *   parities       默认 ["none","even","odd"](8N1/8E1/8O1,dataBits=8/stopBits=1 固定)
   *   timeoutMs      单次探测超时,默认 200
   *   mode           "firstHit"(默认,任一命中即停)|"full"(全矩阵收集)
   *   lineConfig     可选 { flowControl, dtrMode, rtsMode },默认 none/preserve/preserve
   * onProgress({ baud, parity, comboIndex, totalCombos, stationId, elapsedMs }) 每次探测后回调。
   */
  async function scanAll(ctx, args, onProgress) {
    const startedAt = Date.now();
    cancelRequested = false;
    const {
      comPort,
      stationStart = 1,
      stationEnd = 16,
      bauds,
      parities,
      timeoutMs = 200,
      mode = "firstHit",
      lineConfig = {},
    } = args ?? {};
    if (!comPort) {
      return { ok: false, error: { code: "INVALID_PARAM", message: "缺少 comPort 参数" } };
    }
    if (!Number.isInteger(stationStart) || !Number.isInteger(stationEnd)
      || stationStart < 1 || stationEnd > 247 || stationStart > stationEnd) {
      return { ok: false, error: { code: "INVALID_PARAM", message: "站号范围必须为 1..247 且起点不大于终点" } };
    }
    const baudList = Array.isArray(bauds) && bauds.length ? bauds : DEFAULT_SCAN_BAUDS;
    const parityList = Array.isArray(parities) && parities.length ? parities : DEFAULT_SCAN_PARITIES;
    const line = {
      flowControl: lineConfig.flowControl ?? "none",
      dtrMode: lineConfig.dtrMode ?? "preserve",
      rtsMode: lineConfig.rtsMode ?? "preserve",
    };

    const originalStatus = serialService.getStatus();
    const originalConfig = originalStatus?.config;
    const wasOpen = originalStatus?.isOpen ?? false;
    const calls = [];

    const combos = [];
    for (const baud of baudList) {
      for (const parity of parityList) {
        combos.push({ baud, parity });
      }
    }

    const stations = [];
    for (let unitId = stationStart; unitId <= stationEnd; unitId++) stations.push(unitId);
    const totalCombos = combos.length;
    const hits = [];
    let triedCombos = 0;
    let triedProbes = 0;
    let cancelled = false;
    let firstHitFound = false;

    try {
      for (let comboIndex = 0; comboIndex < combos.length; comboIndex++) {
        if (cancelRequested) {
          cancelled = true;
          break;
        }
        const { baud, parity } = combos[comboIndex];
        triedCombos++;

        const opened = await openCombo(comPort, baud, parity, timeoutMs, line, calls);
        if (!opened) continue;

        for (let i = 0; i < stations.length; i++) {
          if (cancelRequested) {
            cancelled = true;
            break;
          }
          const unitId = stations[i];
          const probe = await probeStation(ctx, unitId, timeoutMs);
          triedProbes++;
          onProgress?.({
            baud,
            parity,
            comboIndex,
            totalCombos,
            stationId: unitId,
            probeIndex: triedProbes,
            totalProbes: totalCombos * stations.length,
            elapsedMs: Date.now() - startedAt,
          });
          if (probe.online) {
            hits.push(buildHit(unitId, baud, parity, probe));
            if (mode === "firstHit") {
              firstHitFound = true;
              break;
            }
          }
        }
        if (cancelled || firstHitFound) break;
        if (comboIndex < combos.length - 1) await sleep(COMBO_GAP_MS);
      }
    } finally {
      await restoreOriginal(originalConfig, wasOpen, calls);
    }

    return {
      ok: true,
      found: hits.length > 0,
      cancelled,
      hits,
      hit: hits.length ? hits[0] : null,
      triedCombos,
      triedProbes,
      totalCombos,
      totalProbes: totalCombos * stations.length,
      elapsedMs: Date.now() - startedAt,
      trace: calls,
    };
  }

  function buildHit(stationId, baud, parity, probe) {
    return {
      stationId,
      baudRate: baud,
      parity,
      dataBits: 8,
      stopBits: 1,
      parityLabel: parityLabel(parity),
      format: "RTU",
      firstResponse: probe.exception == null ? "FC03 OK" : `异常码 ${probe.exception}`,
      firstResponseMs: probe.firstResponseMs,
      functionCode: 3,
      status: probe.exception == null ? "在线" : "在线(异常响应)",
    };
  }

  return { scanAll, requestCancel };
}

module.exports = { createModbusScanService, DEFAULT_SCAN_BAUDS, DEFAULT_SCAN_PARITIES };
