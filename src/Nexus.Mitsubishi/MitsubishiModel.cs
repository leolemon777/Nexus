using System;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 PLC 型号枚举 — 决定 MC 协议帧格式和默认参数。
    /// </summary>
    public enum MitsubishiModel
    {
        /// <summary>Q 系列 (QnA) 3E 帧 — 最常用</summary>
        Qna_3E = 0,
        /// <summary>Q 系列 2E 帧</summary>
        Qna_2E = 1,
        /// <summary>A 系列 3E 帧</summary>
        A_3E = 2,
        /// <summary>A 系列 1E 帧</summary>
        A_1E = 3,
        /// <summary>FX3U 系列</summary>
        FX_3U = 4,
        /// <summary>FX5U 系列</summary>
        FX_5U = 5,
    }
}
