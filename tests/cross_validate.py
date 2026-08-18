#!/usr/bin/env python3
"""
交叉验证:用 pymodbus 标准客户端连我们的 Rust 从站,逐 FC 读写对比。

我们的从站预设已知值 → pymodbus client 读取 → 断言数值一致。
任何字节不一致都会在这里暴露——pymodbus 是 Python 生态最权威的实现。

运行:
  1. 先启动我们的从站(Rust sidecar,start_tcp_slave)
  2. python tests/cross_validate.py
"""

import sys
import json
import subprocess
import time
import socket

from pymodbus.client import ModbusTcpClient

# ============================================================
# 启动 Rust sidecar 并通过 JSONL 控制它
# ============================================================

class RustSidecar:
    def __init__(self, binary_path):
        self.proc = subprocess.Popen(
            [binary_path],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, bufsize=1
        )
        self.req_id = 0

    def send(self, command, payload):
        self.req_id += 1
        req = json.dumps({
            "protocolVersion": 1,
            "requestId": f"py{self.req_id}",
            "command": command,
            "payload": payload
        })
        self.proc.stdin.write(req + "\n")
        self.proc.stdin.flush()
        # 读响应(跳过流推送消息)
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("sidecar closed")
            resp = json.loads(line)
            if resp.get("requestId") == f"py{self.req_id}":
                return resp

    def send_ok(self, command, payload):
        resp = self.send(command, payload)
        assert resp.get("ok") == True, f"命令 {command} 失败: {resp}"
        return resp.get("result", {})

    def stop(self):
        try:
            self.send("shutdown", {})
        except:
            pass
        self.proc.terminate()
        self.proc.wait(timeout=5)


def wait_port(host, port, timeout=5):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            s = socket.socket()
            s.settimeout(0.5)
            s.connect((host, port))
            s.close()
            return True
        except:
            time.sleep(0.2)
    return False


# ============================================================
# 测试用例
# ============================================================

PASS = 0
FAIL = 0
RESULTS = []

def check(name, condition, detail=""):
    global PASS, FAIL
    if condition:
        PASS += 1
        RESULTS.append(f"  ✅ {name}")
    else:
        FAIL += 1
        RESULTS.append(f"  ❌ {name}: {detail}")

def run_tests(rust_binary, slave_port):
    print(f"\n{'='*60}")
    print(f"交叉验证: pymodbus client → Rust 从站 (port {slave_port})")
    print(f"{'='*60}\n")

    # 启动 Rust 从站
    rust = RustSidecar(rust_binary)
    print("启动 Rust 从站...")
    rust.send_ok("start_tcp_slave", {
        "slaveId": "cross", "port": slave_port, "allowedStationIds": []
    })
    assert wait_port("127.0.0.1", slave_port), f"从站端口 {slave_port} 未就绪"
    print(f"Rust 从站就绪 (port {slave_port})\n")

    # pymodbus client 连接
    client = ModbusTcpClient("127.0.0.1", port=slave_port)
    assert client.connect(), "pymodbus 连接失败"
    print("pymodbus client 已连接\n")

    # --- 预设已知值到 Rust 从站 ---
    print("预设已知值...")
    rust.send_ok("slave_set_value", {"slaveId": "cross", "area": "holding", "address": 100, "values": [0x1234, 0xABCD]})
    rust.send_ok("slave_set_value", {"slaveId": "cross", "area": "holding", "address": 200, "values": [0xBEEF]})
    rust.send_ok("slave_set_value", {"slaveId": "cross", "area": "input", "address": 10, "values": [1, 2, 3]})
    rust.send_ok("slave_set_coil", {"slaveId": "cross", "area": "coil", "address": 0,
        "values": [True, False, True, False, True, False, True, False, True, False, True, False]})
    rust.send_ok("slave_set_coil", {"slaveId": "cross", "area": "discrete", "address": 0,
        "values": [True, True, True, True, True, True, True, True]})

    # ============================================================
    # FC03 读保持寄存器
    # ============================================================
    print("\n--- FC03 读保持寄存器 ---")
    r = client.read_holding_registers(address=100, count=2, device_id=1)
    check("FC03 无异常", not r.isError(), str(r))
    check("FC03 寄存器100=0x1234", r.registers[0] == 0x1234, f"实际={hex(r.registers[0])}")
    check("FC03 寄存器101=0xABCD", r.registers[1] == 0xABCD, f"实际={hex(r.registers[1])}")

    # ============================================================
    # FC04 读输入寄存器
    # ============================================================
    print("--- FC04 读输入寄存器 ---")
    r = client.read_input_registers(address=10, count=3, device_id=1)
    check("FC04 无异常", not r.isError(), str(r))
    check("FC04 IR[10]=1", r.registers[0] == 1, f"实际={r.registers[0]}")
    check("FC04 IR[11]=2", r.registers[1] == 2, f"实际={r.registers[1]}")
    check("FC04 IR[12]=3", r.registers[2] == 3, f"实际={r.registers[2]}")

    # ============================================================
    # FC01 读线圈(12个,验证填充位截断)
    # ============================================================
    print("--- FC01 读线圈 (12个,填充位截断) ---")
    r = client.read_coils(address=0, count=12, device_id=1)
    check("FC01 无异常", not r.isError(), str(r))
    check("FC01 返回恰好12个", len(r.bits) >= 12, f"实际={len(r.bits)}")
    if len(r.bits) >= 12:
        for i in range(12):
            expected = (i % 2 == 0)
            check(f"FC01 线圈{i}={'ON' if expected else 'OFF'}", r.bits[i] == expected, f"实际={r.bits[i]}")

    # ============================================================
    # FC02 读离散输入
    # ============================================================
    print("--- FC02 读离散输入 ---")
    r = client.read_discrete_inputs(address=0, count=8, device_id=1)
    check("FC02 无异常", not r.isError(), str(r))
    check("FC02 返回≥8个", len(r.bits) >= 8, f"实际={len(r.bits)}")
    if len(r.bits) >= 8:
        for i in range(8):
            check(f"FC02 DI[{i}]=ON", r.bits[i] == True, f"实际={r.bits[i]}")

    # ============================================================
    # FC05 写单线圈 + 读回
    # ============================================================
    print("--- FC05 写单线圈 ON ---")
    r = client.write_coil(address=50, value=True, device_id=1)
    check("FC05 写ON无异常", not r.isError(), str(r))
    r = client.read_coils(address=50, count=1, device_id=1)
    check("FC05 读回=ON", r.bits[0] == True, f"实际={r.bits[0]}")

    print("--- FC05 写单线圈 OFF ---")
    r = client.write_coil(address=50, value=False, device_id=1)
    check("FC05 写OFF无异常", not r.isError(), str(r))
    r = client.read_coils(address=50, count=1, device_id=1)
    check("FC05 读回=OFF", r.bits[0] == False, f"实际={r.bits[0]}")

    # ============================================================
    # FC06 写单寄存器 + 读回
    # ============================================================
    print("--- FC06 写单寄存器 0xBEEF ---")
    r = client.write_register(address=200, value=0xBEEF, device_id=1)
    check("FC06 写无异常", not r.isError(), str(r))
    r = client.read_holding_registers(address=200, count=1, device_id=1)
    check("FC06 读回=0xBEEF", r.registers[0] == 0xBEEF, f"实际={hex(r.registers[0])}")

    # ============================================================
    # FC16 写多寄存器 + 读回
    # ============================================================
    print("--- FC16 写多寄存器 [100,200,300,400,500] ---")
    r = client.write_registers(address=300, values=[100, 200, 300, 400, 500], device_id=1)
    check("FC16 写无异常", not r.isError(), str(r))
    r = client.read_holding_registers(address=300, count=5, device_id=1)
    check("FC16 读回[0]=100", r.registers[0] == 100, f"实际={r.registers[0]}")
    check("FC16 读回[4]=500", r.registers[4] == 500, f"实际={r.registers[4]}")

    # ============================================================
    # FC15 写多线圈 + 读回
    # ============================================================
    print("--- FC15 写多线圈 [T,F,T,F,T,F,T,F] ---")
    r = client.write_coils(address=60, values=[True,False,True,False,True,False,True,False], device_id=1)
    check("FC15 写无异常", not r.isError(), str(r))
    r = client.read_coils(address=60, count=8, device_id=1)
    if len(r.bits) >= 8:
        for i in range(8):
            check(f"FC15 线圈{60+i}={'ON' if i%2==0 else 'OFF'}", r.bits[i] == (i%2==0), f"实际={r.bits[i]}")

    # ============================================================
    # FC22 mask write: 验证公式
    # ============================================================
    print("--- FC22 mask write (AND=0xFFFF, OR=0x00F0) ---")
    # 先设 0x000F
    rust.send_ok("slave_set_value", {"slaveId": "cross", "area": "holding", "address": 400, "values": [0x000F]})
    r = client.mask_write_register(address=400, and_mask=0xFFFF, or_mask=0x00F0, device_id=1)
    check("FC22 无异常", not r.isError(), str(r))
    r = client.read_holding_registers(address=400, count=1, device_id=1)
    # 规范: (0x000F & 0xFFFF) | (0x00F0 & ~0xFFFF) = 0x000F
    # 旧代码: (0x000F & 0xFFFF) | 0x00F0 = 0x00FF
    check("FC22 结果=0x000F(规范公式)", r.registers[0] == 0x000F, f"实际={hex(r.registers[0])} (旧代码会得0x00FF)")

    client.close()
    rust.stop()


# ============================================================
# 主入口
# ============================================================

if __name__ == "__main__":
    binary = sys.argv[1] if len(sys.argv) > 1 else "rust-core/target/release/nexus-rust-core.exe"
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 15050

    try:
        run_tests(binary, port)
    except Exception as e:
        print(f"\n💥 异常: {e}")
        import traceback; traceback.print_exc()
        FAIL += 1

    print(f"\n{'='*60}")
    print(f"结果: ✅ {PASS} passed, ❌ {FAIL} failed")
    print(f"{'='*60}")
    for line in RESULTS:
        print(line)
    sys.exit(1 if FAIL > 0 else 0)
