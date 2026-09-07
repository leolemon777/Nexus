// Nexus OPC UA PoC —— docs/opcua-blocked.md 复活条件验证
//
// 目的：验证 FreeOpcUa async-opcua 0.19（纯 Rust 加密栈，无 openssl-sys）
// 能否在 Windows MSVC 上编译并完成最小的 server + client 自闭环：
//   1. 内嵌 server：SecurityPolicy::None 端点 + 模拟点表（2 个 f64 温度 + 1 个动态计数器）
//   2. 同进程 client：匿名连接，直接读取静态值，二次读取验证动态值在推进
//
// 判定：进程退出码 0 且 stdout 打印 "POC RESULT {...}"；任何一步失败退出码 1。
// 本 PoC 不涉及加密端点（SignAndEncrypt），加密栈仅以"能编译链接"的形式被验证。

use std::{net::TcpListener, sync::Arc, time::Duration};

use opcua::{
    client::{ClientBuilder, IdentityToken, Session},
    crypto::SecurityPolicy,
    server::{
        address_space::Variable,
        diagnostics::NamespaceMetadata,
        node_manager::memory::{simple_node_manager, SimpleNodeManager},
        ServerBuilder, SubscriptionCache,
    },
    types::{
        AttributeId, BuildInfo, DataValue, DateTime, IntegerId, MessageSecurityMode, NodeId,
        ReadValueId, TimestampsToReturn, UserTokenPolicy, Variant,
    },
};

const NAMESPACE_URI: &str = "urn:NexusOpcuaPoc";

const TEMP1_EXPECTED: f64 = 25.5;
const TEMP2_EXPECTED: f64 = -12.25;

#[tokio::main]
async fn main() {
    match run_poc().await {
        Ok(summary) => {
            println!("POC RESULT {summary}");
            std::process::exit(0);
        }
        Err(error) => {
            eprintln!("POC FAILED: {error}");
            std::process::exit(1);
        }
    }
}

async fn run_poc() -> Result<String, String> {
    // 独立临时工作目录：server pki / client pki / server.conf 都不落在仓库里。
    let work_dir = std::env::temp_dir().join(format!("nexus-opcua-poc-{}", std::process::id()));
    std::fs::create_dir_all(&work_dir).map_err(|e| format!("create work dir: {e}"))?;

    // 端口选择：先绑 0 拿一个空闲端口再释放，随后写进生成的 server.conf。
    let listener = TcpListener::bind("127.0.0.1:0").map_err(|e| format!("bind: {e}"))?;
    let port = listener
        .local_addr()
        .map_err(|e| format!("local_addr: {e}"))?
        .port();
    drop(listener);

    let server_pki = work_dir.join("server-pki");
    let client_pki = work_dir.join("client-pki");
    let server_pki_str = server_pki.to_string_lossy().replace('\\', "/");

    // 最小 server.conf：仅 None 端点 + 匿名令牌。字段与官方 samples/server.conf 同构，
    // 其余字段依赖 async-opcua-server 的 serde 默认值。
    let conf = format!(
        "application_name: Nexus OPC UA PoC Server\n\
         application_uri: urn:NexusOpcuaPocServer\n\
         product_uri: urn:NexusOpcuaPocServer\n\
         create_sample_keypair: true\n\
         certificate_path: own/cert.der\n\
         private_key_path: private/private.pem\n\
         pki_dir: {server_pki_str}\n\
         tcp_config:\n\
         \x20 hello_timeout: 5\n\
         \x20 host: 127.0.0.1\n\
         \x20 port: {port}\n\
         discovery_urls:\n\
         - opc.tcp://127.0.0.1:{port}/\n\
         user_tokens: {{}}\n\
         endpoints:\n\
         \x20 none:\n\
         \x20   path: /\n\
         \x20   security_policy: None\n\
         \x20   security_mode: None\n\
         \x20   security_level: 0\n\
         \x20   user_token_ids:\n\
         \x20   - ANONYMOUS\n"
    );
    let conf_path = work_dir.join("server.conf");
    std::fs::write(&conf_path, conf).map_err(|e| format!("write conf: {e}"))?;

    // ---- server 侧：建地址空间 + 模拟点表 ----
    let (server, handle) = ServerBuilder::new()
        .with_config_from(conf_path.to_string_lossy().as_ref())
        .build_info(BuildInfo {
            product_uri: "urn:NexusOpcuaPocServer".into(),
            manufacturer_name: "Nexus 2.0 PoC".into(),
            product_name: "Nexus OPC UA PoC Server".into(),
            software_version: "0.19.0-poc".into(),
            build_number: "1".into(),
            build_date: DateTime::now(),
        })
        .with_node_manager(simple_node_manager(
            NamespaceMetadata {
                namespace_uri: NAMESPACE_URI.to_owned(),
                ..Default::default()
            },
            "nexus-poc",
        ))
        .trust_client_certs(true)
        .diagnostics_enabled(false)
        .build()
        .map_err(|e| format!("build server: {e}"))?;

    let node_manager = handle
        .node_managers()
        .get_of_type::<SimpleNodeManager>()
        .ok_or("simple node manager not found")?;
    let ns = handle
        .get_namespace_index(NAMESPACE_URI)
        .ok_or("namespace index not found")?;

    let folder_node = NodeId::new(ns, "folder");
    let temp1_node = NodeId::new(ns, "temp1");
    let temp2_node = NodeId::new(ns, "temp2");
    let counter_node = NodeId::new(ns, "counter");

    {
        let address_space = node_manager.address_space();
        let mut address_space = address_space.write();
        address_space.add_folder(
            &folder_node,
            "NexusPoC",
            "NexusPoC",
            &NodeId::objects_folder_id(),
        );
        let added = address_space.add_variables(
            vec![
                Variable::new(&temp1_node, "temp1", "temp1", TEMP1_EXPECTED),
                Variable::new(&temp2_node, "temp2", "temp2", TEMP2_EXPECTED),
                Variable::new(&counter_node, "counter", "counter", 0_i32),
            ],
            &folder_node,
        );
        if !added.iter().all(|&ok| ok) {
            return Err("add variables: some nodes were rejected".to_owned());
        }
    }

    // 动态计数器：300ms 推进一次，模拟轮询点位的活值。
    {
        let manager = node_manager.clone();
        let subscriptions: Arc<SubscriptionCache> = handle.subscriptions().clone();
        let counter_node = counter_node.clone();
        tokio::task::spawn(async move {
            let mut counter = 0_i32;
            let mut interval = tokio::time::interval(Duration::from_millis(300));
            loop {
                interval.tick().await;
                counter += 1;
                if let Err(e) = manager.set_values(
                    &subscriptions,
                    [(&counter_node, None, DataValue::new_now(counter))].into_iter(),
                ) {
                    eprintln!("counter update stopped: {e}");
                    break;
                }
            }
        });
    }

    let server_task = tokio::task::spawn(async move {
        if let Err(e) = server.run().await {
            eprintln!("POC FAILED: server run error: {e}");
            std::process::exit(1);
        }
    });

    // ---- client 侧：匿名 + SecurityPolicy::None，带重试等 server 就绪 ----
    let mut client = ClientBuilder::new()
        .application_name("Nexus OPC UA PoC Client")
        .application_uri("urn:NexusOpcuaPocClient")
        .product_uri("urn:NexusOpcuaPocClient")
        .trust_server_certs(true)
        .create_sample_keypair(true)
        .pki_dir(&client_pki)
        .session_retry_limit(3)
        .client()
        .map_err(|errors| format!("client config: {errors:?}"))?;

    let endpoint_url = format!("opc.tcp://127.0.0.1:{port}/");
    let mut session = None;
    for attempt in 1..=40_u32 {
        match client
            .connect_to_matching_endpoint(
                (
                    endpoint_url.as_str(),
                    SecurityPolicy::None.to_str(),
                    MessageSecurityMode::None,
                    UserTokenPolicy::anonymous(),
                ),
                IdentityToken::Anonymous,
            )
            .await
        {
            Ok((connected, event_loop)) => {
                let _run_handle = event_loop.spawn();
                connected.wait_for_connection().await;
                session = Some(connected);
                break;
            }
            Err(e) => {
                if attempt == 40 {
                    return Err(format!("connect after {attempt} attempts: {e}"));
                }
                tokio::time::sleep(Duration::from_millis(250)).await;
            }
        }
    }
    let session: Arc<Session> = session.ok_or("no session")?;

    // ---- 验证读取 ----
    let first = read_values(
        &session,
        &[temp1_node.clone(), temp2_node.clone(), counter_node.clone()],
    )
    .await?;
    let temp1 = expect_double(&first[0], "temp1")?;
    let temp2 = expect_double(&first[1], "temp2")?;
    let counter_before = expect_int32(&first[2], "counter")?;

    if temp1 != TEMP1_EXPECTED {
        return Err(format!("temp1 = {temp1}, expected {TEMP1_EXPECTED}"));
    }
    if temp2 != TEMP2_EXPECTED {
        return Err(format!("temp2 = {temp2}, expected {TEMP2_EXPECTED}"));
    }

    // 等两个以上刷新周期，再读一次 counter，验证动态值链路。
    tokio::time::sleep(Duration::from_millis(700)).await;
    let second = read_values(&session, &[counter_node.clone()]).await?;
    let counter_after = expect_int32(&second[0], "counter")?;
    if counter_after <= counter_before {
        return Err(format!(
            "counter did not advance: before = {counter_before}, after = {counter_after}"
        ));
    }

    // ---- 收尾 ----
    handle.cancel();
    let _ = tokio::time::timeout(Duration::from_secs(5), server_task).await;

    Ok(format!(
        "{{\"namespace\":\"{NAMESPACE_URI}\",\"port\":{port},\"endpoint\":\"{endpoint_url}\",\
         \"securityPolicy\":\"None\",\"temp1\":{temp1},\"temp2\":{temp2},\
         \"counterBefore\":{counter_before},\"counterAfter\":{counter_after},\"reads\":4}}"
    ))
}

async fn read_values(
    session: &Arc<Session>,
    node_ids: &[NodeId],
) -> Result<Vec<DataValue>, String> {
    let reads: Vec<ReadValueId> = node_ids
        .iter()
        .map(|id| ReadValueId {
            node_id: id.clone(),
            attribute_id: AttributeId::Value as IntegerId,
            ..Default::default()
        })
        .collect();
    session
        .read(&reads, TimestampsToReturn::Both, 0.0)
        .await
        .map_err(|e| format!("read {:?}: {e}", node_ids))
}

fn expect_double(value: &DataValue, name: &str) -> Result<f64, String> {
    expect_good_status(value, name)?;
    match value.value.as_ref() {
        Some(Variant::Double(v)) => Ok(*v),
        other => Err(format!("{name}: unexpected variant {other:?}")),
    }
}

fn expect_int32(value: &DataValue, name: &str) -> Result<i32, String> {
    expect_good_status(value, name)?;
    match value.value.as_ref() {
        Some(Variant::Int32(v)) => Ok(*v),
        other => Err(format!("{name}: unexpected variant {other:?}")),
    }
}

fn expect_good_status(value: &DataValue, name: &str) -> Result<(), String> {
    match value.status.as_ref() {
        Some(status) if !status.is_good() => Err(format!("{name}: bad status {status}")),
        _ => Ok(()),
    }
}
