# 测试策略

## 单元测试

- 地址解析测试。
- CRC/LRC/BCC golden vectors。
- PDU/ADU 编解码测试。
- 错误码映射测试。

## 集成测试

- Docker/本地模拟器。
- 真实 PLC/仪表测试矩阵。
- 串口回环测试。
- 网络断线、半包、粘包、超时、重连测试。

## 现场安全测试

- 默认只读。
- 写入白名单。
- 危险功能码禁用。
- 速率限制。
- 操作审计。
- 回滚方案。

## 兼容性数据

每个协议插件维护 `compatibility.yaml`：

```yaml
vendor: Siemens
protocol: S7comm
models:
  - name: S7-1200
    tested: true
    firmware: "V4.x"
    features: [read-db, write-db, read-m, write-m]
```
