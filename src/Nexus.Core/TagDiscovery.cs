using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nexus;

/// <summary>
/// Tag auto-discovery — scans a PLC device for available addresses/tags.
/// </summary>
public static class TagDiscovery
{
    /// <summary>
    /// Discover tags by probing common address ranges.
    /// Returns a list of discovered addresses with their data types.
    /// </summary>
    public static async Task<List<DiscoveredTag>> DiscoverAsync(IReadWriteDevice device, string protocol, int maxAddress = 1000)
    {
        var tags = new List<DiscoveredTag>();
        
        switch (protocol.ToLowerInvariant())
        {
            case "modbus":
                tags = await DiscoverModbusAsync(device, maxAddress).ConfigureAwait(false);
                break;
            case "siemens":
                tags = await DiscoverSiemensAsync(device, maxAddress).ConfigureAwait(false);
                break;
            default:
                tags = await DiscoverGenericAsync(device, maxAddress).ConfigureAwait(false);
                break;
        }
        
        return tags;
    }
    
    private static async Task<List<DiscoveredTag>> DiscoverModbusAsync(IReadWriteDevice device, int maxAddress)
    {
        var tags = new List<DiscoveredTag>();
        
        // Probe holding registers (40001-40xxx)
        for (int addr = 0; addr < maxAddress; addr += 10)
        {
            var result = await device.ReadBytesAsync($"{40001 + addr}", 20).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                tags.Add(new DiscoveredTag
                {
                    Address = $"{40001 + addr}",
                    DataType = "Int16",
                    Description = $"保持寄存器 {addr}-{addr + 9}"
                });
            }
            else break; // Stop at first failure
        }
        
        // Probe coils (00001-00xxx)
        for (int addr = 0; addr < Math.Min(maxAddress, 100); addr += 16)
        {
            var result = await device.ReadBytesAsync($"{addr + 1:D5}", 2).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                tags.Add(new DiscoveredTag
                {
                    Address = $"{addr + 1:D5}",
                    DataType = "Bool",
                    Description = $"线圈 {addr}-{addr + 15}"
                });
            }
        }
        
        return tags;
    }
    
    private static async Task<List<DiscoveredTag>> DiscoverSiemensAsync(IReadWriteDevice device, int maxAddress)
    {
        var tags = new List<DiscoveredTag>();
        
        // Probe M area
        for (int addr = 0; addr < Math.Min(maxAddress, 100); addr += 2)
        {
            var result = await device.ReadInt16Async($"MW{addr}").ConfigureAwait(false);
            if (result.IsSuccess)
                tags.Add(new DiscoveredTag { Address = $"MW{addr}", DataType = "Int16", Description = $"Merker Word {addr}" });
        }
        
        // Probe DB1
        for (int addr = 0; addr < Math.Min(maxAddress, 100); addr += 2)
        {
            var result = await device.ReadInt16Async($"DB1.DBW{addr}").ConfigureAwait(false);
            if (result.IsSuccess)
                tags.Add(new DiscoveredTag { Address = $"DB1.DBW{addr}", DataType = "Int16", Description = $"DB1 Word {addr}" });
        }
        
        return tags;
    }
    
    private static async Task<List<DiscoveredTag>> DiscoverGenericAsync(IReadWriteDevice device, int maxAddress)
    {
        var tags = new List<DiscoveredTag>();
        for (int addr = 0; addr < Math.Min(maxAddress, 100); addr++)
        {
            var result = await device.ReadInt16Async(addr.ToString()).ConfigureAwait(false);
            if (result.IsSuccess)
                tags.Add(new DiscoveredTag { Address = addr.ToString(), DataType = "Int16", Description = $"地址 {addr}" });
        }
        return tags;
    }
}

public class DiscoveredTag
{
    public string Address { get; set; } = string.Empty;
    public string DataType { get; set; } = "Int16";
    public string Description { get; set; } = string.Empty;
    public object? SampleValue { get; set; }
}
