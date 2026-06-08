using System.Buffers.Binary;
using OpenIndustrialComm.Core;
using OpenIndustrialComm.Transports;

namespace OpenIndustrialComm.Modbus;

public sealed class ModbusTcpClient : IReadWriteDeviceClient
{
    private readonly byte _unitId;
    private readonly TcpTransport _transport;
    private readonly ModbusAddressParser _addressParser = new();
    private ushort _transactionId;

    public ModbusTcpClient(string host, int port = 502, byte unitId = 1, TransportOptions? options = null)
    {
        Endpoint = DeviceEndpoint.Tcp(host, port);
        _unitId = unitId;
        _transport = new TcpTransport(host, port, options);
    }

    public ProtocolDescriptor Descriptor { get; } = new(
        Id: "modbus.tcp",
        DisplayName: "Modbus TCP",
        Aliases: new[] { "modbus-tcp", "modbus" },
        Capabilities: ProtocolCapability.Read | ProtocolCapability.Write | ProtocolCapability.BatchRead | ProtocolCapability.RawFrame,
        DefaultTransports: new[] { "tcp" },
        SpecificationUrl: "https://www.modbus.org/modbus-specifications");

    public DeviceEndpoint Endpoint { get; }
    public bool IsConnected => _transport.IsConnected;

    public async Task<OperationResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("transport.connect_failed", ex.Message, ex);
        }
    }

    public async Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("transport.disconnect_failed", ex.Message, ex);
        }
    }

    public async Task<OperationResult<T>> ReadAsync<T>(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            if (typeof(T) == typeof(ushort))
            {
                var result = await ReadUInt16Async(address, cancellationToken).ConfigureAwait(false);
                return result.Success
                    ? OperationResult<T>.Ok((T)(object)result.Value)
                    : OperationResult<T>.Fail(result.ErrorCode ?? "read.failed", result.Message ?? "Read failed.", result.Exception);
            }

            if (typeof(T) == typeof(bool))
            {
                var result = await ReadBoolAsync(address, cancellationToken).ConfigureAwait(false);
                return result.Success
                    ? OperationResult<T>.Ok((T)(object)result.Value)
                    : OperationResult<T>.Fail(result.ErrorCode ?? "read.failed", result.Message ?? "Read failed.", result.Exception);
            }

            return OperationResult<T>.Fail("type.unsupported", $"Type {typeof(T).Name} is not supported by this sample implementation.");
        }
        catch (Exception ex)
        {
            return OperationResult<T>.Fail("read.exception", ex.Message, ex);
        }
    }

    public async Task<OperationResult<ushort>> ReadUInt16Async(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            var parsed = _addressParser.Parse(address);
            if (parsed.Area is not (ModbusArea.HoldingRegister or ModbusArea.InputRegister))
                return OperationResult<ushort>.Fail("address.area_mismatch", "UInt16 read requires holding/input register area.");

            var function = parsed.Area == ModbusArea.HoldingRegister
                ? ModbusFunction.ReadHoldingRegisters
                : ModbusFunction.ReadInputRegisters;

            var pdu = ModbusPdu.Read(function, parsed.Offset, 1);
            var response = await SendPduAsync(pdu, cancellationToken).ConfigureAwait(false);
            var values = ModbusPdu.DecodeRegisters(response);
            return OperationResult<ushort>.Ok(values[0]);
        }
        catch (Exception ex)
        {
            return OperationResult<ushort>.Fail("modbus.read_uint16_failed", ex.Message, ex);
        }
    }

    public async Task<OperationResult<bool>> ReadBoolAsync(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            var parsed = _addressParser.Parse(address);
            if (parsed.Area is not (ModbusArea.Coil or ModbusArea.DiscreteInput))
                return OperationResult<bool>.Fail("address.area_mismatch", "Boolean read requires coil/discrete input area.");

            var function = parsed.Area == ModbusArea.Coil
                ? ModbusFunction.ReadCoils
                : ModbusFunction.ReadDiscreteInputs;

            var response = await SendPduAsync(ModbusPdu.Read(function, parsed.Offset, 1), cancellationToken).ConfigureAwait(false);
            ModbusPdu.EnsureNotException(response);
            if (response.Length < 3 || response[1] < 1) throw new InvalidDataException("Invalid coil response.");
            return OperationResult<bool>.Ok((response[2] & 0x01) == 0x01);
        }
        catch (Exception ex)
        {
            return OperationResult<bool>.Fail("modbus.read_bool_failed", ex.Message, ex);
        }
    }

    public async Task<OperationResult> WriteUInt16Async(string address, ushort value, CancellationToken cancellationToken = default)
    {
        try
        {
            var parsed = _addressParser.Parse(address);
            if (parsed.Area != ModbusArea.HoldingRegister)
                return OperationResult.Fail("address.area_mismatch", "UInt16 write requires holding register area.");

            var response = await SendPduAsync(ModbusPdu.WriteSingleRegister(parsed.Offset, value), cancellationToken).ConfigureAwait(false);
            ModbusPdu.EnsureNotException(response);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("modbus.write_uint16_failed", ex.Message, ex);
        }
    }

    public async Task<OperationResult> WriteAsync<T>(string address, T value, CancellationToken cancellationToken = default)
    {
        if (value is ushort u16) return await WriteUInt16Async(address, u16, cancellationToken).ConfigureAwait(false);
        return OperationResult.Fail("type.unsupported", $"Type {typeof(T).Name} is not supported by this sample implementation.");
    }

    public async Task<OperationResult<IReadOnlyDictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, object?>();
        foreach (var address in addresses)
        {
            var item = await ReadAsync<ushort>(address, cancellationToken).ConfigureAwait(false);
            if (!item.Success) return OperationResult<IReadOnlyDictionary<string, object?>>.Fail(item.ErrorCode ?? "batch.failed", item.Message ?? "Batch read failed.", item.Exception);
            result[address] = item.Value;
        }
        return OperationResult<IReadOnlyDictionary<string, object?>>.Ok(result);
    }

    private async Task<byte[]> SendPduAsync(byte[] pdu, CancellationToken cancellationToken)
    {
        if (!IsConnected) await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var request = BuildMbapFrame(pdu);
        await _transport.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        var header = await ByteReader.ReadExactAsync(_transport, 7, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        if (length < 1) throw new InvalidDataException("Invalid MBAP length.");
        var remaining = length - 1;
        var body = await ByteReader.ReadExactAsync(_transport, remaining, cancellationToken).ConfigureAwait(false);
        return body;
    }

    private byte[] BuildMbapFrame(byte[] pdu)
    {
        var transactionId = unchecked(++_transactionId);
        var length = checked((ushort)(pdu.Length + 1));
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 0); // Protocol id
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), length);
        frame[6] = _unitId;
        pdu.CopyTo(frame.AsSpan(7));
        return frame;
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
