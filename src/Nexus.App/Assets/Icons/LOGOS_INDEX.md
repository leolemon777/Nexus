# Nexus 工业通信 Logo 资源清单

> 最后更新：2026-06-06 · 自动化收集 + 人工整理
>
> 本目录汇总了 Nexus 项目用到的 **PLC 厂商品牌** 与 **工业通信协议** 品牌图标。
> 所有文件均为透明背景 PNG（WPF pack URI 资源）。

---

## 1. PLC 厂商品牌（`src/Nexus.App/Assets/Icons/`）

| 文件名 | 厂商 | 备注 |
|---|---|---|
| `siemens.png` | Siemens | ★ 已存在，保留 |
| `rockwell.png` | Rockwell / Allen-Bradley | ★ 已存在，保留 |
| `mitsubishi_electric.png` | Mitsubishi Electric | ★ 已存在，保留 |
| `omron.png` | Omron | ★ 已存在，保留 |
| `delta.png` | Delta (台达) | ★ 已存在，保留 |
| `keyence.png` | Keyence (基恩士) | ★ 已存在，保留 |
| `panasonic.png` | Panasonic | ★ 已存在，保留 |
| `fuji.png` | Fuji Electric | ★ 已存在，保留 |
| `schneider_electric.png` | Schneider Electric | 🆕 |
| `abb.png` | ABB | 🆕 |
| `beckhoff.png` | Beckhoff Automation | 🆕 |
| `bosch_rexroth.png` | Bosch Rexroth | 🆕 |
| `br_industrial.png` | B&R Industrial Automation | 🆕 |
| `phoenix_contact.png` | Phoenix Contact | 🆕 |
| `honeywell.png` | Honeywell | 🆕 |
| `yokogawa.png` | Yokogawa Electric | 🆕 |
| `wago.png` | WAGO | 🆕 |
| `emerson.png` | Emerson Electric | 🆕 |
| `idec.png` | IDEC Corporation | 🆕 |
| `inovance.png` | Inovance (汇川) | 🆕 |
| `inovance_hd.png` | Inovance (高清版) | 🆕 备用 |
| `hitachi.png` | Hitachi | 🆕 |
| `toshiba.png` | Toshiba | 🆕 |

## 2. 通讯协议（`src/Nexus.App/Assets/Icons/Protocols/`）

| 文件名 | 协议 | 备注 |
|---|---|---|
| `modbus.png` | **Modbus** | ⭐ 用户指定核心 |
| `profinet.png` | **PROFINET** | ⭐ 用户指定核心 |
| `ethercat.png` | **EtherCAT** | ⭐ 用户指定核心 |
| `opc_ua.png` | **OPC UA** | ⭐ 用户指定核心 |
| `profibus.png` | PROFIBUS | |
| `ethernet_ip.png` | EtherNet/IP (ODVA) | |
| `sercos.png` | Sercos | |
| `cc_link.png` | CC-Link (CLPA) | |
| `canopen.png` | CANopen (CiA) | |
| `devicenet.png` | DeviceNet (ODVA) | |
| `bacnet.png` | BACnet (ASHRAE) | |
| `io_link.png` | IO-Link Consortium | |
| `hart.png` | HART (FieldComm Group) | |
| `mqtt.png` | MQTT (Eclipse) | |
| `powerlink.png` | Ethernet POWERLINK | |
| `mechatrolink.png` | MECHATROLINK (MMA) | |
| `flnet.png` | FL-net (JEMA 日本) | |
| `as_interface.png` | AS-Interface (AS-i) | |
| `foundation_fieldbus.png` | Foundation Fieldbus | |

---

## 3. WPF 资源引用

`Nexus.App.csproj` 已配置：

```xml
<ItemGroup>
  <Resource Include="Assets\Icons\*.png" />           <!-- 顶层 vendor -->
  <Resource Include="Assets\Icons\**\*.png" />        <!-- 含 Protocols/ 子目录 -->
</ItemGroup>
```

### 现有 vendor 加载（`ViewModels/MainViewModel.cs`）：

```csharp
// BrandLogos.TryLoad("schneider_electric.png")
bmp.UriSource = new Uri($"pack://application:,,,/Assets/Icons/{filename}");
```

### 协议加载建议（参考，未实现）

在 `MainViewModel.cs` 中加一个 helper：

```csharp
internal static class ProtocolLogos
{
    public static ImageSource? TryLoad(string filename)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/Assets/Icons/Protocols/{filename}");
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 48;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
```

---

## 4. 来源与版权

| 类别 | 主要来源 | License 状态 |
|---|---|---|
| Wikimedia Commons | https://commons.wikimedia.org/ | 多数为 CC BY-SA / Public Domain，使用前请查阅各文件 page |
| 官方协议组织 | modbus.org / opcfoundation.org / ethercat.org / as-interface.net / mechatrolink.org / clpa.or.jp / bacnet.org | 官方品牌资产，**nominative fair use** 标识产品兼容 |
| 品牌镜像站 | logo.wine / freebiesupply.com / logos-world.net / companieslogo.com / 1000logos.net | 第三方整理，license 状态各站不同；本项目仅用于 UI 标识（不二次分发） |
| 图标聚合 | getvectorlogo / findvectorlogo / pngwing / pngegg | 仅作 fallback；本项目首选官方源 |

> ⚠️ **商标声明**：所有厂商名、协议名、对应 logo 均为各自所有者的商标。
> 本项目（Nexus）以 nominative fair use 方式使用这些资产来标识对应的产品兼容性与协议支持，不暗示任何官方背书。
> 商业分发前请替换为各品牌方提供的官方 brand kit，或在 README/About 中注明来源。

---

## 5. 未收录项（待补）

- 🔧 **Festo**（SSL 握手超时，建议手动从 festo.com 官方 press kit 下载）
- 🔧 **LS Electric**（官网 logo 路径经常变化）
- 🔧 **Yaskawa**（同上）
- 🔧 **Fatek / Xinje / Hollysys**（国内厂商 CDN 经常 404/超时）
- 🔧 **BACnet**（bacnet.org 主域名 path 经常变）

## 6. 自动化收集脚本

`%TEMP%\*.py` 下保留了一套可重跑的 Python 脚本：
- `matrix_search_images` 走 MCP 搜图
- 解析 + 评分（避开 Wikimedia/SeekLogo 等被 block 的源）
- 短超时下载（8s/文件）+ SSL 验证关闭
- 失败重试（30s/45s 备用）

如需扩展新的厂商/协议，复制 `reimg4.json` 模板改 task_name + query，再跑 `download3.py`。
