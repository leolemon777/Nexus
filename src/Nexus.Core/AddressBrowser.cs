using System.Collections.Generic;
using System.Linq;

namespace Nexus
{
    /// <summary>
    /// 地址浏览器数据项 — 用于 UI 中以树形结构展示 PLC 地址。
    /// </summary>
    public class AddressBrowserItem
    {
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public string DataType { get; set; } = "";
        public string Value { get; set; } = "";
        public bool IsExpanded { get; set; }
        public List<AddressBrowserItem> Children { get; set; } = new List<AddressBrowserItem>();
    }

    /// <summary>
    /// 地址浏览器辅助类 — 为不同协议生成默认地址列表。
    /// </summary>
    public static class AddressBrowserHelper
    {
        public static List<AddressBrowserItem> GetDefaultAddresses(string protocol)
        {
            switch (protocol)
            {
                case "SiemensS7":
                    return new List<AddressBrowserItem>
                    {
                        new AddressBrowserItem
                        {
                            Name = "M 区 (Merker)",
                            Address = "M",
                            IsExpanded = true,
                            Children = Enumerable.Range(0, 10).Select(i => new AddressBrowserItem
                            {
                                Name = $"MW{i * 2}",
                                Address = $"MW{i * 2}",
                                DataType = "Int16"
                            }).ToList()
                        },
                        new AddressBrowserItem
                        {
                            Name = "DB1",
                            Address = "DB1",
                            Children = Enumerable.Range(0, 10).Select(i => new AddressBrowserItem
                            {
                                Name = $"DB1.DBW{i * 2}",
                                Address = $"DB1.DBW{i * 2}",
                                DataType = "Int16"
                            }).ToList()
                        },
                        new AddressBrowserItem
                        {
                            Name = "I 区 (Input)",
                            Address = "I",
                            Children = Enumerable.Range(0, 5).Select(i => new AddressBrowserItem
                            {
                                Name = $"IW{i * 2}",
                                Address = $"IW{i * 2}",
                                DataType = "Int16"
                            }).ToList()
                        },
                        new AddressBrowserItem
                        {
                            Name = "Q 区 (Output)",
                            Address = "Q",
                            Children = Enumerable.Range(0, 5).Select(i => new AddressBrowserItem
                            {
                                Name = $"QW{i * 2}",
                                Address = $"QW{i * 2}",
                                DataType = "Int16"
                            }).ToList()
                        },
                    };

                case "Modbus":
                    return new List<AddressBrowserItem>
                    {
                        new AddressBrowserItem
                        {
                            Name = "保持寄存器 (4xxxx)",
                            Address = "40001",
                            IsExpanded = true,
                            Children = Enumerable.Range(0, 20).Select(i => new AddressBrowserItem
                            {
                                Name = $"{40001 + i}",
                                Address = $"{40001 + i}",
                                DataType = "Int16"
                            }).ToList()
                        },
                        new AddressBrowserItem
                        {
                            Name = "输入寄存器 (3xxxx)",
                            Address = "30001",
                            Children = Enumerable.Range(0, 10).Select(i => new AddressBrowserItem
                            {
                                Name = $"{30001 + i}",
                                Address = $"{30001 + i}",
                                DataType = "Int16"
                            }).ToList()
                        },
                        new AddressBrowserItem
                        {
                            Name = "线圈 (0xxxx)",
                            Address = "00001",
                            Children = Enumerable.Range(0, 16).Select(i => new AddressBrowserItem
                            {
                                Name = $"{i + 1:D5}",
                                Address = $"{i + 1:D5}",
                                DataType = "Bool"
                            }).ToList()
                        },
                    };

                default:
                    return new List<AddressBrowserItem>();
            }
        }
    }
}
