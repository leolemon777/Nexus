using System;
using System.Collections.Generic;

namespace Nexus.BrPowerlink
{
    /// <summary>
    /// B&amp;R POWERLINK 协议常量。
    /// <para>POWERLINK 是 EPSG（EtherNET POWERLINK Standardization Group）公开标准的实时以太网协议。
    /// 本库实现基于 TCP 的 SDO（Service Data Object）请求-应答封装，用于访问对象字典（Object Dictionary）。</para>
    /// </summary>
    public static class BrPowerlinkConstants
    {
        /// <summary>SDO 读对象字典命令码。</summary>
        public const byte CmdReadOd = 0x01;

        /// <summary>SDO 写对象字典命令码。</summary>
        public const byte CmdWriteOd = 0x02;

        /// <summary>无错误（成功）。</summary>
        public const uint ErrorNone = 0x00000000;

        /// <summary>
        /// 默认端口。
        /// <para>POWERLINK 真实的实时通信走 UDP 多播，本库采用自定义 TCP SDO 封装端口（非 POWERLINK 标准端口）。</para>
        /// </summary>
        public const int DefaultPort = 34962;

        /// <summary>请求帧固定头部长度: cmd(1) + nodeId(1) + index(2) + subIndex(1) + size(2) = 7 字节。</summary>
        public const int RequestHeaderLength = 7;

        /// <summary>响应帧固定头部长度: error(4) + payloadLen(2) = 6 字节。</summary>
        public const int ResponseHeaderLength = 6;

        /// <summary>默认节点 ID（MN 默认对 CN 1 操作）。</summary>
        public const byte DefaultNodeId = 1;

        /// <summary>设备类型对象字典条目（0x1000），常用于心跳探测。</summary>
        public const ushort OdDeviceType = 0x1000;

        /// <summary>默认通信超时（毫秒）。</summary>
        public const int DefaultTimeout = 5000;
    }

    /// <summary>
    /// POWERLINK SDO Abort 错误码表。
    /// <para>错误码取值参考 EPSG POWERLINK 通信规范 / CANopen SDO Abort Codes（DS301）。</para>
    /// </summary>
    public static class BrPowerlinkError
    {
        /// <summary>成功。</summary>
        public const uint None = 0x00000000;

        /// <summary>读取或写入时发生内部通讯错误。</summary>
        public const uint InternalError = 0x05040000;

        /// <summary>对象字典中不存在该索引。</summary>
        public const uint ObjectDoesNotExist = 0x06020000;

        /// <summary>对象不支持该访问类型（如对只读对象执行写）。</summary>
        public const uint UnsupportedAccess = 0x06010000;

        /// <summary>写入只读对象。</summary>
        public const uint WriteReadOnly = 0x06010002;

        /// <summary>读取只写对象。</summary>
        public const uint ReadWriteOnly = 0x06010001;

        /// <summary>子索引不存在。</summary>
        public const uint SubIndexNotExist = 0x06090011;

        /// <summary>数值/类型不匹配，长度或范围越界。</summary>
        public const uint TypeMismatch = 0x06070010;

        /// <summary>数值超出设备允许范围。</summary>
        public const uint ValueOutOfRange = 0x06090030;

        /// <summary>写入值无效。</summary>
        public const uint InvalidValue = 0x06090031;

        /// <summary>资源不足（如内存不足）。</summary>
        public const uint OutOfMemory = 0x05040005;

        private static readonly Dictionary<uint, string> _messages = new Dictionary<uint, string>
        {
            { None,                 "成功" },
            { InternalError,        "SDO 通讯内部错误" },
            { ObjectDoesNotExist,   "对象字典中不存在该索引" },
            { UnsupportedAccess,    "对象不支持该访问类型" },
            { WriteReadOnly,        "对象只读，不支持写入" },
            { ReadWriteOnly,        "对象只写，不支持读取" },
            { SubIndexNotExist,     "子索引不存在" },
            { TypeMismatch,         "类型/长度不匹配" },
            { ValueOutOfRange,      "数值超出允许范围" },
            { InvalidValue,         "写入值无效" },
            { OutOfMemory,          "节点资源不足" },
        };

        /// <summary>根据错误码返回可读消息；未知错误码返回通用描述。</summary>
        public static string GetMessage(uint errorCode)
        {
            if (_messages.TryGetValue(errorCode, out string msg))
                return msg;
            return $"未知 POWERLINK SDO 错误 (0x{errorCode:X8})";
        }
    }
}
