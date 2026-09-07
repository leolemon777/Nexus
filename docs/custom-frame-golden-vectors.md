# 自定义帧解析黄金向量(custom frame definition)

日期：2026-09-04（v2 增补动态长度与尾部定界）；2026-09-07（v3 增补脚本模式）
证据等级：S3（软件向量：Rust 内联单测 32 个 + JSONL E2E 6 个，无真实设备参与）
对应规格：[spec-plan-serial-plot-parse-replay.md](./spec-plan-serial-plot-parse-replay.md) 批次 2 + B.8
实现：`rust-core/src/frame_definition.rs`、`rust-core/src/frame_expr.rs`、`rust-core/tests/custom_frame_jsonl_e2e.rs`

## 向量清单

| # | 模式 | 向量 | 期望 | 用例位置 |
|---|---|---|---|---|
| V1 | binary | `01 03 04 12 34 AB CD`，u16@3 + i16@5，无校验 | `a=4660`、`b=-21555`（有符号负值） | 单测 `binary_u16_and_i16_negative` |
| V2 | binary | `00 00 C0 3F 01 00`，f32 le@0 ×1 + u16 le@4 ×0.1 | `f=1.5`、`u=0.1`（缩放） | 单测 `binary_byte_order_and_scale_and_f32` |
| V3 | binary | 帧头 `01 03`，帧 `01 03 2A` 匹配 / `02 03 2A` 不匹配 | 后者 `HEAD_MISMATCH` | 单测 `binary_head_match_and_mismatch` |
| V4 | binary | 定长 4，帧 3 字节 | `LENGTH_MISMATCH` | 单测 + E2E（E2E 用 9 字节定义收 3 字节帧） |
| V5 | binary | sum8 `0A 0B 15`（0A+0B=15）通过；`0A 0B 99` 失败 | `CHECKSUM_MISMATCH` | 单测 `binary_sum8_and_xor8` |
| V6 | binary | xor8 `0A 0B 01`（0A^0B=01）通过 | ok | 同上 |
| V7 | binary | `00 64 00 C8 + crc16-modbus(LE)`，i16×2，定长 6 | `t1=100`、`t2=200`；CRC 破坏 1 字节 → `CHECKSUM_MISMATCH` | 单测 + E2E（E2E: `01 03 02 01 F4 01 90 + CRC` → temp1=50.0℃/temp2=40.0℃） |
| V8 | binary | 字段 @2 u16 超出数据区（校验尾之后） | `FIELD_OUT_OF_RANGE` | 单测 `binary_field_out_of_range_respects_checksum_tail` |
| V9 | ascii | `ST,251,760\n`，序号 1，scale 0.1，unit ℃ | `temp=25.1℃` | 单测 + E2E（E2E: `ST,25.5,760\n` → 25.5kg） |
| V10 | ascii | `12.5\r\n`（CRLF）；序号越界 | ok；`ASCII_FIELD_MISSING` | 单测 `ascii_crlf_and_missing_field` |
| V11 | ascii | `abc\n` 非数字 | `ASCII_PARSE_FAILED` | 单测 + E2E |
| V12 | 校验 | 未知模式 `websocket` / 未知校验 / 字段缺偏移序号 | `FRAME_DEF_INVALID` / validate 问题列表 | 单测 + E2E（越界定长 + 缺序号） |
| V13 | serde | 完整 JSON 定义 camelCase 往返；未知字段拒绝 | ok / Err | 单测 `serde_camel_case_roundtrip` |
| V14 | E2E | hello 能力清单含 `custom_frame_parse`/`custom_frame_validate` | 两条命令均在 capabilities | E2E `hello_advertises_the_custom_frame_commands` |
| V15 | binary 动态长度 | 帧 `AA 04 01 F4 2A`（去尾后），长度字段 u8@1 raw=4 + 补偿 1 = 5；u16BE@2 ×0.1 | `value=50.0` | 单测 + E2E |
| V16 | binary 动态长度 | 同定义但帧体只有 4 字节（raw+补偿≠实际） | `LENGTH_MISMATCH` | 单测 + E2E |
| V17 | binary 动态长度 | 帧短到读不出长度字段 | `FRAME_TOO_SHORT` | 单测 |
| V18 | binary 动态长度 | u16 LE@0=0x0100(256)，补偿 -253 → 总长 3 | ok（数据@2=123） | 单测 `binary_dynamic_length_u16_little_endian` |
| V19 | binary 尾部定界 | `01 02 03 06 0D 0A`：sum8=06 覆盖剥离 tail 前的帧体，tail `0D 0A` 不计入校验 | ok；尾部 `0D 0B` → `TAIL_MISMATCH` | 单测 + E2E |
| V20 | binary 组合 | 动态长度(raw=3+补偿1=4) + tail `0D 0A` | ok | 单测 `binary_tail_delimiter_and_crc_interaction` |
| V21 | validate | 定长与长度字段同时存在 / 数据字段与长度字段重叠 / 长度字段撞帧头 / 长度字段类型或字节序非法 / tail HEX 非法 | 对应问题逐条报出 | 单测 `validate_length_field_conflicts_and_overlaps` |
| V22 | parse | 数据字段与长度字段字节重叠 | `FIELD_OUT_OF_RANGE`（消息注明与长度字段重叠） | 单测 `parse_rejects_field_overlapping_length_field` |
| V23 | script | 帧 `55 25 12 8C`：accept `frame[0]==0x55`、verify `(sum(0,len-2)&0xFF)==frame[len-1]`（55+25+12=8C）、字段 `bcd(frame[1])`×0.01 | `temp=0.25℃`；帧头 54 → `FRAME_REJECTED`；末字节翻转 → `SCRIPT_VERIFY_FAILED` | 单测 `script_mode_bcd_with_accept_and_verify` + E2E |
| V24 | script | 字段 `frame[0] << 8 \| frame[1]`（位合成）于帧 `12 34` | `raw=4660` | 单测 `script_mode_missing_expr_and_syntax_error` |
| V25 | script | 字段缺表达式 / 表达式 `frame[0] ++` 语法错 | `FRAME_DEF_INVALID` / `EXPR_SYNTAX` | 单测 + E2E |
| V26 | script | 表达式 `frame[9]` 索引越界 | `EXPR_EVAL` | 单测 + E2E |
| V27 | script | validate：脚本模式混入 length/checksum、字段缺表达式、accept 引用未知标识符 `foo` | 三类问题逐条报出 | 单测 `validate_definition_script_issues` + E2E |
| V28 | script serde | `expr`/`accept`/`verify` camelCase 往返；旧 schema（无这三键）反序列化为 None | ok | 单测 `serde_script_fields_roundtrip` |
| V29 | 表达式引擎 | 优先级/短路/三目右结合/十六进制/位运算/移位/一元/函数族（bit/bcd/sum/xor/crc16/abs/min/max）矩阵 | 见 `frame_expr::tests`（8 组用例） | 单测（含 C 优先级陷阱 `1 & 2 == 2` = 1） |
| V30 | 表达式引擎 | 限额：>256 字符 / 嵌套 >16 层 / 区间函数按字节计步超 4096 / 结果非有限（2^62 连乘） | `EXPR_LIMIT` / `EXPR_EVAL` | 单测 `compile_errors`、`eval_limits_and_nonfinite` |

## 错误码一览

`FRAME_DEF_INVALID`（定义本身非法）、`HEAD_MISMATCH`、`LENGTH_MISMATCH`、`TAIL_MISMATCH`（尾部定界不匹配）、`CHECKSUM_MISMATCH`（sum8/xor8/crc16-modbus）、`FIELD_OUT_OF_RANGE`（字段越过数据区，数据区不含校验尾；或与长度字段重叠）、`ASCII_FIELD_MISSING`、`ASCII_PARSE_FAILED`、`FRAME_TOO_SHORT`（含读不出长度字段）、`FRAME_REJECTED`（script 模式 accept 拒帧）、`SCRIPT_VERIFY_FAILED`（script 模式 verify 失败）、`EXPR_SYNTAX`/`EXPR_EVAL`/`EXPR_LIMIT`（表达式编译/求值/限额）。解析失败显式返回，绝不静默。

## v3 语义（脚本模式，2026-09-07）

- `mode:"script"`：字段级 `expr`、定义级 `accept`/`verify`（均可选，expr 必填）；字段值 = 表达式求值 × `scale`。脚本模式不支持 head/length/lengthField/tail/checksum（validate 显式报告）。
- 表达式语言：数值单类型，变量 `frame[i]`/`len`，运算符与 C 同优先级（注意 `& | ^` 低于 `==`，位与比较需括号），三目右结合，`&&`/`||` 短路；纯函数 `bit/bcd/sum/xor/crc16/abs/min/max`。限额 256 字符 / 16 层 / 4096 步（区间函数按覆盖字节数计步）。
- 沙箱即语言：不可表达赋值/循环/函数定义/IO，无第三方脚本运行时依赖。
- 复用 `custom_frame_parse`/`custom_frame_validate` 命令，IPC 白名单与 `.nexus.json` schemaVersion（仍为 2）不变。

## v2 语义（动态长度 + 尾部定界，2026-09-04）

- `lengthField`：`{offset, fieldType: u8|u16, byteOrder: be|le, adjust}`；帧总长 = 长度字段原始值 + adjust，**不含尾部定界**。与定长 `length` 互斥（validate 与 parse 双重把关）。
- `tail`：HEX 字符串（如 `"0D 0A"`）。存在且非空时帧必须以它结尾；解析前先剥离，长度计算与校验均以剥离后的帧体为准。空字符串 = 不使用（与帧头 `head` 约定一致）。
- 数据字段不允许与长度字段字节重叠；长度字段不允许与帧头重叠。
- v1 定义（无 `lengthField`/`tail`）行为完全不变，`.nexus.json` schemaVersion 仍为 2。

## L2 边界

以上全部为软件证据。真实设备（RS-485 温度模块等）的帧定义适配与温度曲线验收属于 L2，依赖硬件在场，见 P0 证据分级表；硬件缺席时不宣称设备级完成。
