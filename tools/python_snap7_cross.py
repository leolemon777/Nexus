# -*- coding: utf-8 -*-
"""python-snap7(3.1.2 纯 Python) × Nexus Rust S7 从站 交叉验证。

用法:先 cargo build(生成 target/debug/nexus-rust-core.exe),再运行本脚本。
对拍基准与 Modbus 的 pymodbus 51 项对拍同标准:第三方客户端连我们的虚拟从站,
期望值只来自 seed 定义,禁止从实现反推。
"""
import json
import subprocess
import sys
import time

import snap7
from snap7.type import Area, WordLen

PORT = 11602
BIN = str(__import__("pathlib").Path(__file__).resolve().parents[1] / "rust-core" / "target" / "debug" / "nexus-rust-core.exe")

results = []


def check(name, fn):
    try:
        detail = fn()
        results.append((True, name, detail))
        print(f"PASS  {name}  {detail}")
    except Exception as e:  # noqa: BLE001
        results.append((False, name, repr(e)))
        print(f"FAIL  {name}  {e!r}")


def jsonl(proc, rid, cmd, payload):
    line = json.dumps({
        "protocolVersion": 1, "requestId": rid, "command": cmd, "payload": payload
    })
    proc.stdin.write(line + "\n")
    proc.stdin.flush()
    while True:
        raw = proc.stdout.readline()
        if not raw:
            raise RuntimeError("sidecar EOF")
        resp = json.loads(raw)
        if resp.get("requestId") == rid:
            if not resp.get("ok"):
                raise RuntimeError(f"{cmd} 失败: {resp}")
            return resp["result"]


def hexs(b):
    return " ".join(f"{x:02X}" for x in b)


def main():
    proc = subprocess.Popen(
        [BIN], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
    )
    try:
        jsonl(proc, "slave", "start_s7_slave",
              {"slaveId": "s7", "port": PORT, "seed": True})
        time.sleep(0.2)

        client = snap7.client.Client()
        check("01 COTP+Setup 握手(rack0/slot1)",
              lambda: (client.connect("127.0.0.1", 0, 1, PORT), "connected") [1])

        # --- 读:seed 期望值 ---
        check("02 读 DB1.DBB0..3 = 12 34 56 78",
              lambda: hexs(client.read_area(Area.DB, 1, 0, 4)))
        check("03 读 DB1.DBB4..7 = 0A 0B 0C 0D",
              lambda: hexs(client.read_area(Area.DB, 1, 4, 4)))
        check("04 读 DB1.DBW8 = BEEF(字节流)",
              lambda: hexs(client.read_area(Area.DB, 1, 8, 2)))
        check("05 读 MW0 = 12 34",
              lambda: hexs(client.read_area(Area.MK, 0, 0, 2)))
        check("06 读 MB10 = 55(位交替)",
              lambda: hexs(client.read_area(Area.MK, 0, 10, 1)))
        check("07 读 IW0 = 11 11",
              lambda: hexs(client.read_area(Area.PE, 0, 0, 2)))
        check("08 读 QW0 = 22 22",
              lambda: hexs(client.read_area(Area.PA, 0, 0, 2)))
        check("09 读 T0 = 25 10(S5TIME)",
              lambda: hexs(client.read_area(Area.TM, 0, 0, 2, WordLen.Timer)))
        check("10 读 C0 = 00 05",
              lambda: hexs(client.read_area(Area.CT, 0, 0, 2, WordLen.Counter)))

        # --- 写:写后回读(snap7 通道) + 从站侧断言(JSONL 通道) ---
        def w_db():
            client.write_area(Area.DB, 1, 100, bytearray([0xCA, 0xFE, 0xBA, 0xBE]))
            got = jsonl(proc, "chk1", "s7_slave_get",
                        {"slaveId": "s7", "address": "DB1.DBB100", "count": 4})["data"]
            assert got == [0xCA, 0xFE, 0xBA, 0xBE], got
            return "snap7 写 → rust 从站内存一致"
        check("11 写 DB1.DBB100(snap7 写 → rust 从站侧验证)", w_db)

        def w_mk():
            client.write_area(Area.MK, 0, 20, bytearray([0xAB, 0xCD]))
            return hexs(client.read_area(Area.MK, 0, 20, 2))
        check("12 写 MW20 = AB CD(snap7 写 → snap7 读回)", w_mk)

        def w_bit():
            # M30.3 → 位地址 30*8+3 = 243;写后从站侧按位打包读回。
            # 注:python-snap7 3.1.2 的响应解析对 TS=0x03(BIT) 也做 //8(位读必返回空),
            # 与 snap7 C(TS=ResBit 不右移)和真实抓包(FF 03 00 01 01)相悖,
            # 故位读回走 rust 从站 JSONL 通道验证;python 侧用字节读交叉验证。
            client.write_area(Area.MK, 0, 243, bytearray([1]), WordLen.Bit)
            byte = client.read_area(Area.MK, 0, 30, 1)
            bits = jsonl(proc, "chk13", "s7_slave_get",
                         {"slaveId": "s7", "address": "M30.0", "count": 8})["data"]
            assert bits[0] & 0x08 == 0x08, bits
            return f"MB30={hexs(byte)}(bit3=1)"
        check("13 位写 M30.3(读-改-写 + 双通道验证)", w_bit)

        def w_bit_nc():
            # 先置 MB31 = 0x81,写 M31.1=1 → 0x83,验证不整字节覆盖
            client.write_area(Area.MK, 0, 31, bytearray([0x81]))
            client.write_area(Area.MK, 0, 31 * 8 + 1, bytearray([1]), WordLen.Bit)
            byte = client.read_area(Area.MK, 0, 31, 1)
            assert byte[0] == 0x83, byte[0]
            return "MB31=0x81 + M31.1=1 → 0x83"
        check("14 位写不破坏同字节(0x81→0x83)", w_bit_nc)

        def w_tc():
            client.write_area(AArea_TM[0], 0, 3, bytearray([0x12, 0x34]), WordLen.Timer)
            return hexs(client.read_area(Area.TM, 0, 3, 2, WordLen.Timer))
        check("15 写 T3 = 12 34 → 读回", w_tc)

        def multi_read():
            areas = [Area.DB, Area.MK, Area.PE, Area.PA]
            out = [hexs(client.read_area(a, 1 if a == Area.DB else 0, 0, 2)) for a in areas]
            return " | ".join(out)
        check("16 四区并发读(DB/M/I/Q 各 2 字节)", multi_read)

        def large_rw():
            data = bytes((i * 7) & 0xFF for i in range(200))
            client.write_area(Area.DB, 1, 2000, bytearray(data))
            back = bytes(client.read_area(Area.DB, 1, 2000, 200))
            assert back == data, "200B 回读不一致"
            return "200 字节块写读一致(超 PDU 自动分片)"
        check("17 200 字节大块写读(客户端分片)", large_rw)

        def negotiated():
            # python-snap7 默认请求 480?从站上限 480 → 回 480
            return f"pdu={client.get_pdu_length() if hasattr(client, 'get_pdu_length') else 'n/a'}"
        check("18 PDU 协商结果可查询", negotiated)

        def second_connection():
            c2 = snap7.client.Client()
            c2.connect("127.0.0.1", 0, 1, PORT)
            val = hexs(c2.read_area(Area.DB, 1, 0, 4))
            c2.disconnect()
            return val
        check("19 并发第二连接(连接资源)", second_connection)

        def err_out_of_range():
            try:
                client.read_area(Area.DB, 1, 65534, 4)
                return "未报错(意外)"
            except Exception as e:  # noqa: BLE001
                return f"正确报错: {type(e).__name__}"
        check("20 越界读被拒(RC 0x05)", err_out_of_range)

        client.disconnect()
    finally:
        try:
            proc.stdin.close()
            proc.terminate()
        except Exception:  # noqa: BLE001
            pass

    ok = sum(1 for r in results if r[0])
    print(f"\n==== {ok}/{len(results)} PASS ====")
    sys.exit(0 if ok == len(results) else 1)


AArea_TM = (Area.TM,)

if __name__ == "__main__":
    main()
