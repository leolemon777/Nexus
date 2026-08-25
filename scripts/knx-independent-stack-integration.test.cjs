const assert = require("node:assert/strict");
const dgram = require("node:dgram");
const path = require("node:path");
const test = require("node:test");
const knx = require("knx");
const KnxProtocol = require("knx/src/KnxProtocol");
const KnxConstants = require("knx/src/KnxConstants");

const {
  COMMANDS,
  RustCoreClient,
} = require("../electron/rust-core-client.cjs");

const SILENT_LOGGER = Object.freeze({
  debug() {},
  info() {},
  warn() {},
  error() {},
});

const CHANNEL_ID = 0x15;
const GROUP_ADDRESS = "1/2/3";
const RESPONSE_PAYLOAD = Buffer.from([0x12, 0x34]);

function decodeDatagram(buffer) {
  const reader = KnxProtocol.createReader(buffer);
  reader.KNXNetHeader("datagram");
  return reader.next().datagram;
}

function encodeDatagram(datagram) {
  const writer = KnxProtocol.createWriter();
  writer.KNXNetHeader(datagram);
  return writer.buffer;
}

function encodeDisconnectResponse(channelId, status) {
  // knx 2.5.4 can parse DISCONNECT_RESPONSE but its high-level writer switch
  // only handles DISCONNECT_REQUEST. Keep the independent codec involved by
  // writing the six-byte common header and its ConnState field directly.
  const writer = KnxProtocol.createWriter();
  writer
    .UInt8(0x06)
    .UInt8(0x10)
    .UInt16BE(KnxConstants.SERVICE_TYPE.DISCONNECT_RESPONSE)
    .UInt16BE(0x08)
    .ConnState({ channel_id: channelId, status });
  return writer.buffer;
}

function groupValueResponse(sequence) {
  return encodeDatagram({
    service_type: KnxConstants.SERVICE_TYPE.TUNNELING_REQUEST,
    tunnstate: {
      channel_id: CHANNEL_ID,
      seqnum: sequence,
      rsvd: 0,
    },
    cemi: {
      msgcode: KnxConstants.MESSAGECODES["L_Data.ind"],
      addinfo_length: 0,
      ctrl: {
        frameType: 1,
        reserved: 0,
        repeat: 1,
        broadcast: 1,
        priority: 3,
        acknowledge: 0,
        confirm: 0,
        destAddrType: 1,
        hopCount: 6,
        extendedFrame: 0,
      },
      src_addr: "1.1.1",
      dest_addr: GROUP_ADDRESS,
      apdu: {
        apci: "GroupValue_Response",
        tpci: 0,
        data: RESPONSE_PAYLOAD,
      },
    },
  });
}

function waitFor(events, name, timeoutMs = 2_000) {
  return new Promise((resolve, reject) => {
    const startedAt = Date.now();
    const timer = setInterval(() => {
      if (events.includes(name)) {
        clearInterval(timer);
        resolve();
      } else if (Date.now() - startedAt > timeoutMs) {
        clearInterval(timer);
        reject(new Error(`Timed out waiting for peer event ${name}; got ${events.join(",")}`));
      }
    }, 10);
  });
}

test("Rust KNXnet/IP read-only client interoperates with knx independent codec", { timeout: 30_000 }, async (t) => {
  assert.equal(typeof knx.Connection, "function", "knx package must expose its protocol stack");

  const peer = dgram.createSocket({ type: "udp4" });
  const ready = new Promise((resolve, reject) => {
    const onError = (error) => reject(error);
    peer.once("error", onError);
    peer.bind(0, "127.0.0.1", () => {
      peer.off("error", onError);
      resolve(peer.address());
    });
  });
  peer.on("error", (error) => assert.fail(`independent-stack UDP peer failed: ${error}`));

  const address = await ready;
  const peerEndpoint = `${address.address}:${address.port}`;
  const events = [];
  const decoded = [];

  peer.on("message", (message, remote) => {
    const datagram = decodeDatagram(message);
    decoded.push({ datagram, remote, bytes: message });

    switch (datagram.service_type) {
      case KnxConstants.SERVICE_TYPE.CONNECT_REQUEST: {
        events.push("connect-request");
        const response = encodeDatagram({
          service_type: KnxConstants.SERVICE_TYPE.CONNECT_RESPONSE,
          connstate: { channel_id: CHANNEL_ID, status: 0 },
          hpai: {
            protocol_type: KnxConstants.PROTOCOL_TYPE.IPV4_UDP,
            tunnel_endpoint: peerEndpoint,
          },
          cri: {
            connection_type: KnxConstants.CONNECTION_TYPE.TUNNEL_CONNECTION,
            knx_layer: KnxConstants.KNX_LAYER.LINK_LAYER,
            unused: 0,
          },
        });
        assert.equal(response.length, 20);
        peer.send(response, remote.port, remote.address);
        break;
      }

      case KnxConstants.SERVICE_TYPE.TUNNELING_REQUEST: {
        events.push("group-read-request");
        const acknowledgement = encodeDatagram({
          service_type: KnxConstants.SERVICE_TYPE.TUNNELING_ACK,
          tunnstate: {
            channel_id: CHANNEL_ID,
            seqnum: datagram.tunnstate.seqnum,
            rsvd: 0,
          },
        });
        assert.equal(acknowledgement.length, 10);
        peer.send(acknowledgement, remote.port, remote.address, () => {
          const response = groupValueResponse(datagram.tunnstate.seqnum);
          assert.equal(response.length, 23);
          peer.send(response, remote.port, remote.address);
        });
        break;
      }

      case KnxConstants.SERVICE_TYPE.TUNNELING_ACK: {
        events.push("client-response-ack");
        break;
      }

      case KnxConstants.SERVICE_TYPE.DISCONNECT_REQUEST: {
        events.push("disconnect-request");
        peer.send(
          encodeDisconnectResponse(CHANNEL_ID, 0),
          remote.port,
          remote.address,
        );
        break;
      }

      default:
        break;
    }
  });

  const binaryPath = process.env.NEXUS_RUST_CORE_PATH
    ?? path.resolve(__dirname, "..", "rust-core", "target", "debug", "nexus-rust-core.exe");
  const core = new RustCoreClient({
    binaryPath,
    requestTimeoutMs: 10_000,
    shutdownGraceMs: 2_000,
    logger: SILENT_LOGGER,
  });
  let connectionOpen = false;

  t.after(async () => {
    if (connectionOpen && core.state === "ready") {
      await core.request(COMMANDS.KNX_DISCONNECT, { connectionId: "knx-independent-stack" }).catch(() => {});
    }
    if (core.state === "ready") await core.shutdown().catch(() => {});
    if (peer.address()) peer.close();
  });

  await core.start();
  const opened = await core.request(COMMANDS.OPEN_KNX_CONNECTION, {
    connectionId: "knx-independent-stack",
    host: address.address,
    port: address.port,
    timeoutMs: 500,
  });
  connectionOpen = true;
  assert.equal(opened.udpConnected, true);
  assert.equal(opened.readOnly, true);
  assert.equal(opened.groupWriteEnabled, false);
  assert.equal(opened.sceneControlEnabled, false);
  assert.equal(opened.routingEnabled, false);
  assert.equal(opened.deviceManagementEnabled, false);
  assert.equal(opened.secureEnabled, false);
  assert.deepEqual(opened.response, {
    channelId: CHANNEL_ID,
    status: 0,
    dataEndpoint: [127, 0, 0, 1],
    dataPort: address.port,
    // knx 2.5.4 reuses its CRI writer for CRD. Its link-layer bytes 02 00 are
    // therefore interpreted by the Rust parser as assigned address 0x0200.
    individualAddress: 0x0200,
  });
  assert.equal(opened.requestFrame.length, 26);

  const read = await core.request(COMMANDS.KNX_GROUP_READ, {
    connectionId: "knx-independent-stack",
    address: GROUP_ADDRESS,
    timeoutMs: 500,
  });
  assert.equal(read.readOnly, true);
  assert.equal(read.address.main, 1);
  assert.equal(read.address.middle, 2);
  assert.equal(read.address.sub, 3);
  assert.equal(read.address.value, 0x0a03);
  assert.equal(read.source, 0x1101);
  assert.deepEqual(read.payload, Array.from(RESPONSE_PAYLOAD));
  assert.equal(read.normalizedSmallValue, null);
  assert.equal(read.sequence, 0);
  assert.equal(read.attempts, 1);
  assert.equal(read.requestFrame.length, 21);
  assert.equal(read.acknowledgementFrameHex, "06 10 04 21 00 0A 04 15 00 00");
  assert.equal(read.responseFrameHex, "06 10 04 20 00 17 04 15 00 00 29 00 BC E0 11 01 0A 03 03 00 40 12 34");
  assert.equal(read.responseAckFrameHex, "06 10 04 21 00 0A 04 15 00 00");
  await waitFor(events, "client-response-ack");

  const disconnected = await core.request(COMMANDS.KNX_DISCONNECT, {
    connectionId: "knx-independent-stack",
    timeoutMs: 500,
  });
  connectionOpen = false;
  assert.equal(disconnected.disconnected, true);
  assert.deepEqual(disconnected.response, { channelId: CHANNEL_ID, status: 0 });
  await core.shutdown();

  assert.deepEqual(events, [
    "connect-request",
    "group-read-request",
    "client-response-ack",
    "disconnect-request",
  ]);

  const connect = decoded[0].datagram;
  assert.equal(connect.service_type, KnxConstants.SERVICE_TYPE.CONNECT_REQUEST);
  assert.equal(connect.hpai.tunnel_endpoint, `${decoded[0].remote.address}:${decoded[0].remote.port}`);
  assert.equal(connect.tunn.tunnel_endpoint, `${decoded[0].remote.address}:${decoded[0].remote.port}`);
  assert.equal(connect.cri.connection_type, KnxConstants.CONNECTION_TYPE.TUNNEL_CONNECTION);
  assert.equal(connect.cri.knx_layer, KnxConstants.KNX_LAYER.LINK_LAYER);

  const groupRead = decoded[1].datagram;
  assert.equal(groupRead.tunnstate.channel_id, CHANNEL_ID);
  assert.equal(groupRead.tunnstate.seqnum, 0);
  assert.equal(groupRead.cemi.msgcode, KnxConstants.MESSAGECODES["L_Data.req"]);
  assert.equal(groupRead.cemi.dest_addr, GROUP_ADDRESS);
  assert.equal(groupRead.cemi.apdu.apci, "GroupValue_Read");
  assert.deepEqual(Array.from(groupRead.cemi.apdu.data), [0]);

  const clientAck = decoded[2].datagram;
  assert.equal(clientAck.service_type, KnxConstants.SERVICE_TYPE.TUNNELING_ACK);
  assert.equal(clientAck.tunnstate.channel_id, CHANNEL_ID);
  assert.equal(clientAck.tunnstate.seqnum, 0);
  assert.equal(clientAck.tunnstate.rsvd, 0);

  const disconnect = decoded[3].datagram;
  assert.equal(disconnect.service_type, KnxConstants.SERVICE_TYPE.DISCONNECT_REQUEST);
  assert.equal(disconnect.connstate.channel_id, CHANNEL_ID);
  assert.equal(disconnect.hpai.tunnel_endpoint, `${decoded[3].remote.address}:${decoded[3].remote.port}`);
});
