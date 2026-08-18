function recoverRustCoreClient(current, createClient) {
  if (!current || typeof createClient !== "function") {
    throw new TypeError("A current Rust core client and createClient function are required");
  }
  return current.state === "failed" || current.state === "stopped" ? createClient() : current;
}

module.exports = { recoverRustCoreClient };
