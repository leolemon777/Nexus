// Nexus OPC UA PoC —— docs/opcua-blocked.md 复活条件验证
//
// 目的：验证 FreeOpcUa async-opcua 0.19（纯 Rust 加密栈，无 openssl-sys）
// 能否在 Windows MSVC 上编译并完成最小的 server + client 自闭环：
//   1. 内嵌 server：SecurityPolicy::None 端点 + 模拟点表（2 个 f64 温度 + 1 个动态计数器）
//   2. 同进程 client：匿名连接，直接读取静态值，二次读取验证动态值在推进
//
// 判定：进程退出码 0 且 stdout 打印 "POC RESULT {...}"；任何一步失败退出码 1。
// 本 PoC 不涉及加密端点（SignAndEncrypt），加密栈仅以"能编译链接"的形式被验证。
//
// 本文件的代码形态不是正式接入的模板，硬约束见 docs/opcua-blocked.md
// 「正式接入的硬约束」一节（尤其是重试循环与手写 YAML 两条）。

use std::{path::Path, sync::Arc, time::Duration};

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

/// 连接重试预算。server 在同进程内，正常情况下第一次就能连上；
/// 预算只是为了容忍启动抖动，写成常量是为了不在两处各写一遍。
const MAX_CONNECT_ATTEMPTS: u32 = 40;
const CONNECT_RETRY_INTERVAL: Duration = Duration::from_millis(250);
const COUNTER_TICK: Duration = Duration::from_millis(300);
const SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(5);

#[tokio::main]
async fn main() {
    // 独立临时工作目录：server pki / client pki / server.conf 都不落在仓库里。
    // 清理放在这里而不是 run_poc 里：下面的 std::process::exit 不会跑析构，
    // 靠 Drop 清不掉，只能在这一层显式做。成功才删，失败保留现场（见下）。
    let work_dir = std::env::temp_dir().join(format!("nexus-opcua-poc-{}", std::process::id()));
    let result = run_poc(&work_dir).await;

    match result {
        Ok(summary) => {
            // 只在成功路径删：里面是自签私钥，跑通了就没有留存价值。
            if let Err(e) = std::fs::remove_dir_all(&work_dir) {
                if e.kind() != std::io::ErrorKind::NotFound {
                    eprintln!(
                        "warning: 临时目录未清理（内含自签私钥），请手工删除 {}: {e}",
                        work_dir.display()
                    );
                }
            }
            println!("POC RESULT {summary}");
            std::process::exit(0);
        }
        Err(error) => {
            // 失败时保留现场：生成的 server.conf 与 pki 往往就是排查起点。
            eprintln!("POC FAILED: {error}");
            eprintln!(
                "现场保留在 {}（内含自签私钥，排查完请自行删除）",
                work_dir.display()
            );
            std::process::exit(1);
        }
    }
}

async fn run_poc(work_dir: &Path) -> Result<String, String> {
    std::fs::create_dir_all(work_dir).map_err(|e| format!("create work dir: {e}"))?;

    // 端口：绑 0 拿到空闲端口后**不释放**，listener 原样交给 server.run_with()。
    // 早先的写法是绑完就 drop、只留端口号，等 server 自己再绑一次——那中间隔着
    // 写配置文件和整条 ServerBuilder 链，够别的进程把端口抢走。
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
        .await
        .map_err(|e| format!("bind: {e}"))?;
    let port = listener
        .local_addr()
        .map_err(|e| format!("local_addr: {e}"))?
        .port();

    let server_pki = work_dir.join("server-pki");
    let client_pki = work_dir.join("client-pki");
    // 路径按 YAML 双引号标量写入：先把反斜杠换成正斜杠（Windows 临时路径），
    // 再转义引号。裸写的话，路径里一个 '#' 或 ': ' 就会把这一行截断或拆成两个键。
    let server_pki_yaml = format!(
        "\"{}\"",
        server_pki
            .to_string_lossy()
            .replace('\\', "/")
            .replace('"', "\\\"")
    );

    // 最小 server.conf：仅 None 端点 + 匿名令牌。字段与官方 samples/server.conf 同构，
    // 其余字段依赖 async-opcua-server 的 serde 默认值。
    let conf = format!(
        "application_name: Nexus OPC UA PoC Server\n\
         application_uri: urn:NexusOpcuaPocServer\n\
         product_uri: urn:NexusOpcuaPocServer\n\
         create_sample_keypair: true\n\
         certificate_path: own/cert.der\n\
         private_key_path: private/private.pem\n\
         pki_dir: {server_pki_yaml}\n\
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
        // add_folder 返回 bool 且不是 #[must_use]，丢掉不会有编译警告；
        // 父节点没建起来的话，下面三个变量就挂在一个不存在的父节点上。
        if !address_space.add_folder(
            &folder_node,
            "NexusPoC",
            "NexusPoC",
            &NodeId::objects_folder_id(),
        ) {
            return Err("add folder: NexusPoC was rejected".to_owned());
        }
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
    // 留住 JoinHandle：任务自己死掉时要能拿到原因，否则「counter 不推进」会被
    // 误报成数据链路问题，而真正的原因（panic 或 set_values 失败）被 tokio 吞掉。
    let counter_task = {
        let manager = node_manager.clone();
        let subscriptions: Arc<SubscriptionCache> = handle.subscriptions().clone();
        let counter_node = counter_node.clone();
        tokio::task::spawn(async move {
            let mut counter = 0_i32;
            let mut interval = tokio::time::interval(COUNTER_TICK);
            loop {
                interval.tick().await;
                counter = counter.wrapping_add(1);
                if let Err(e) = manager.set_values(
                    &subscriptions,
                    [(&counter_node, None, DataValue::new_now(counter))].into_iter(),
                ) {
                    return format!("counter update failed at {counter}: {e}");
                }
            }
        })
    };

    // server 的错误顺着 JoinHandle 回到这里，不在任务里直接 process::exit——
    // 那样会绕过 POC FAILED 的输出，连临时目录都留在盘上。
    let server_task = tokio::task::spawn(async move { server.run_with(listener).await });

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
    let mut session_opt = None;
    let mut event_loop_handle = None;
    let mut connect_failure = None;
    for attempt in 1..=MAX_CONNECT_ATTEMPTS {
        // server 已经退出的话，再重试也只是把同一个永久错误重复 40 遍；
        // 真正的原因在 server_task 里，早点跳出去取。
        if server_task.is_finished() {
            connect_failure = Some("server stopped before the client connected".to_owned());
            break;
        }
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
                // 留住事件循环的 handle：丢掉只是 detach（连接照跑），但它死了
                // 就没人知道，后面的读取失败会指向完全不相干的地方。
                event_loop_handle = Some(event_loop.spawn());
                connected.wait_for_connection().await;
                session_opt = Some(connected);
                break;
            }
            Err(e) => {
                if attempt == MAX_CONNECT_ATTEMPTS {
                    connect_failure = Some(format!("connect after {attempt} attempts: {e}"));
                    break;
                }
                tokio::time::sleep(CONNECT_RETRY_INTERVAL).await;
            }
        }
    }

    let session: Arc<Session> = match session_opt {
        Some(session) => session,
        None => {
            let reason = connect_failure
                .unwrap_or_else(|| "connect loop ended without a session".to_owned());
            return Err(format!("{reason}; {}", describe_server_exit(server_task).await));
        }
    };

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
    // 计数器任务：已经结束说明它自己出过事，那个原因比「跑通了」更值得报出来。
    if counter_task.is_finished() {
        return match counter_task.await {
            Ok(reason) => Err(reason),
            Err(join) => Err(format!("counter task panicked: {join}")),
        };
    }
    counter_task.abort();

    // 事件循环这时候还应该活着；它先死了的话，上面的读取结果就不可信。
    if let Some(event_loop_handle) = &event_loop_handle {
        if event_loop_handle.is_finished() {
            return Err("client event loop stopped before shutdown".to_owned());
        }
    }

    handle.cancel();
    match tokio::time::timeout(SHUTDOWN_TIMEOUT, server_task).await {
        Ok(Ok(Ok(()))) => {}
        Ok(Ok(Err(e))) => return Err(format!("server run error: {e}")),
        Ok(Err(join)) => return Err(format!("server task panicked: {join}")),
        Err(_) => {
            return Err(format!(
                "server did not shut down within {}s",
                SHUTDOWN_TIMEOUT.as_secs()
            ))
        }
    }

    Ok(format!(
        "{{\"namespace\":\"{NAMESPACE_URI}\",\"port\":{port},\"endpoint\":\"{endpoint_url}\",\
         \"securityPolicy\":\"None\",\"temp1\":{temp1},\"temp2\":{temp2},\
         \"counterBefore\":{counter_before},\"counterAfter\":{counter_after},\"reads\":4}}"
    ))
}

/// server 任务的结束原因翻译成一句话；还在跑就直说还在跑。
/// 单独抽出来是因为拿这个原因要消费 JoinHandle，在循环里做会牵扯所有权。
async fn describe_server_exit(
    server_task: tokio::task::JoinHandle<Result<(), String>>,
) -> String {
    match tokio::time::timeout(Duration::from_secs(1), server_task).await {
        Ok(Ok(Ok(()))) => "server exited cleanly".to_owned(),
        Ok(Ok(Err(e))) => format!("server run error: {e}"),
        Ok(Err(join)) => format!("server task panicked: {join}"),
        Err(_) => "server still running".to_owned(),
    }
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
    let values = session
        .read(&reads, TimestampsToReturn::Both, 0.0)
        .await
        .map_err(|e| format!("read {node_ids:?}: {e}"))?;
    // Read 服务规定按请求顺序一一对应返回，但返回长度没有类型层面的保证，
    // 而调用方是按下标取值的：这里挡一道，免得越界 panic 绕过 POC FAILED 判定。
    if values.len() != node_ids.len() {
        return Err(format!(
            "read {node_ids:?}: expected {} values, got {}",
            node_ids.len(),
            values.len()
        ));
    }
    Ok(values)
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
