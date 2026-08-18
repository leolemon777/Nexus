#!/usr/bin/env python3
"""
pymodbus 3.x 标准从站 —— 交叉验证用。
预设已知值,供 Nexus-Rust 主站读取对比。

运行: python tests/pymodbus_server.py [port]
"""

import sys

from pymodbus.server import StartTcpServer
from pymodbus.datastore import ModbusDeviceContext, ModbusServerContext, ModbusSequentialDataBlock

def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 15050

    # 保持寄存器 HR: 预设已知值
    hr_block = ModbusSequentialDataBlock(0, [0] * 500)
    hr_block.values[100] = 0x1234
    hr_block.values[101] = 0xABCD
    hr_block.values[102] = 0x0001
    hr_block.values[103] = 0x0002
    hr_block.values[104] = 0x0003
    hr_block.values[200] = 0xBEEF
    hr_block.values[300] = 100
    hr_block.values[301] = 200
    hr_block.values[302] = 300
    hr_block.values[303] = 400
    hr_block.values[304] = 500
    hr_block.values[400] = 0x000F

    # 输入寄存器 IR
    ir_block = ModbusSequentialDataBlock(0, [0] * 100)
    ir_block.values[10] = 1
    ir_block.values[11] = 2
    ir_block.values[12] = 3

    # 线圈 COIL: 交替 ON/OFF (12个)
    co_block = ModbusSequentialDataBlock(0, [False] * 100)
    for i in range(12):
        co_block.values[i] = (i % 2 == 0)

    # 离散输入 DI: 全 ON (8个)
    di_block = ModbusSequentialDataBlock(0, [False] * 100)
    for i in range(8):
        di_block.values[i] = True

    slave = ModbusDeviceContext(
        di=di_block, co=co_block, hr=hr_block, ir=ir_block
    )
    context = ModbusServerContext(devices={1: slave}, single=False)

    print(f"pymodbus server on 127.0.0.1:{port} (unit_id=1)")
    print(f"  HR[100-104]=[0x1234,0xABCD,1,2,3] HR[200]=0xBEEF HR[300-304]=[100..500] HR[400]=0x000F")
    print(f"  IR[10-12]=[1,2,3] COIL[0-11]=alt DI[0-7]=all ON")
    sys.stdout.flush()

    StartTcpServer(context=context, address=("127.0.0.1", port))

if __name__ == "__main__":
    main()
