using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Redis
{
    public class RedisClient : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly int _timeoutMs;
        private readonly int _maxPoolSize;
        private readonly ConcurrentStack<RedisConnection> _pool;
        private readonly SemaphoreSlim _semaphore;
        private volatile bool _disposed;
        private string _password = string.Empty;
        private int _database;

        public RedisClient(string host, int port = 6379, int timeoutMs = 5000, int maxPoolSize = 10)
        {
            _host = host;
            _port = port;
            _timeoutMs = timeoutMs;
            _maxPoolSize = maxPoolSize;
            _pool = new ConcurrentStack<RedisConnection>();
            _semaphore = new SemaphoreSlim(maxPoolSize, maxPoolSize);
        }

        private RedisConnection AcquireConnection()
        {
            _semaphore.Wait();
            if (_pool.TryPop(out var conn) && conn.IsConnected)
                return conn;
            try
            {
                conn?.Dispose();
            }
            catch { }
            return CreateConnection();
        }

        private async Task<RedisConnection> AcquireConnectionAsync(CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            if (_pool.TryPop(out var conn) && conn.IsConnected)
                return conn;
            try
            {
                conn?.Dispose();
            }
            catch { }
            return await CreateConnectionAsync(ct).ConfigureAwait(false);
        }

        private void ReturnConnection(RedisConnection conn)
        {
            if (conn == null || !conn.IsConnected || _disposed)
            {
                try { conn?.Dispose(); } catch { }
                _semaphore.Release();
                return;
            }
            _pool.Push(conn);
            _semaphore.Release();
        }

        private RedisConnection CreateConnection()
        {
            var conn = new RedisConnection(_host, _port, _timeoutMs);
            conn.Connect();
            if (_password != null)
                conn.SendCommand("AUTH", _password);
            if (_database > 0)
                conn.SendCommand("SELECT", _database.ToString());
            return conn;
        }

        private async Task<RedisConnection> CreateConnectionAsync(CancellationToken ct)
        {
            var conn = new RedisConnection(_host, _port, _timeoutMs);
            await conn.ConnectAsync(ct).ConfigureAwait(false);
            if (_password != null)
                await conn.SendCommandAsync(new[] { "AUTH", _password }, ct).ConfigureAwait(false);
            if (_database > 0)
                await conn.SendCommandAsync(new[] { "SELECT", _database.ToString() }, ct).ConfigureAwait(false);
            return conn;
        }

        public void Auth(string password)
        {
            _password = password;
        }

        public void Select(int database)
        {
            _database = database;
        }

        private static void ThrowIfError(RespValue resp)
        {
            if (resp.Type == RespType.Error)
                throw new RedisException(resp.AsString());
        }

        private static RedisValue ExtractRedisValue(RespValue resp)
        {
            ThrowIfError(resp);
            return resp.ToRedisValue();
        }

        private static RedisValue[] ExtractRedisValues(RespValue resp)
        {
            ThrowIfError(resp);
            if (resp.ArrayValue == null) return Array.Empty<RedisValue>();
            var result = new RedisValue[resp.ArrayValue.Length];
            for (int i = 0; i < resp.ArrayValue.Length; i++)
                result[i] = resp.ArrayValue[i].ToRedisValue();
            return result;
        }

        // ═══════════════════════════════════════════════
        //  String Operations
        // ═══════════════════════════════════════════════

        public RedisValue Get(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("GET", key)); }
            finally { ReturnConnection(conn); }
        }

        public async Task<RedisValue> GetAsync(string key, CancellationToken ct = default)
        {
            var conn = await AcquireConnectionAsync(ct).ConfigureAwait(false);
            try { return ExtractRedisValue(await conn.SendCommandAsync(new[] { "GET", key }, ct).ConfigureAwait(false)); }
            finally { ReturnConnection(conn); }
        }

        public bool Set(string key, RedisValue value, int expireSeconds = 0)
        {
            var conn = AcquireConnection();
            try
            {
                var args = expireSeconds > 0
                    ? new[] { "SET", key, value.AsString(), "EX", expireSeconds.ToString() }
                    : new[] { "SET", key, value.AsString() };
                var resp = conn.SendCommand(args);
                ThrowIfError(resp);
                return resp.AsString() == "OK";
            }
            finally { ReturnConnection(conn); }
        }

        public async Task<bool> SetAsync(string key, RedisValue value, int expireSeconds = 0, CancellationToken ct = default)
        {
            var conn = await AcquireConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                var args = expireSeconds > 0
                    ? new[] { "SET", key, value.AsString(), "EX", expireSeconds.ToString() }
                    : new[] { "SET", key, value.AsString() };
                var resp = await conn.SendCommandAsync(args, ct).ConfigureAwait(false);
                ThrowIfError(resp);
                return resp.AsString() == "OK";
            }
            finally { ReturnConnection(conn); }
        }

        public RedisValue[] MGet(params string[] keys)
        {
            var args = new string[keys.Length + 1];
            args[0] = "MGET";
            Array.Copy(keys, 0, args, 1, keys.Length);
            var conn = AcquireConnection();
            try { return ExtractRedisValues(conn.SendCommand(args)); }
            finally { ReturnConnection(conn); }
        }

        public async Task<RedisValue[]> MGetAsync(string[] keys, CancellationToken ct = default)
        {
            var args = new string[keys.Length + 1];
            args[0] = "MGET";
            Array.Copy(keys, 0, args, 1, keys.Length);
            var conn = await AcquireConnectionAsync(ct).ConfigureAwait(false);
            try { return ExtractRedisValues(await conn.SendCommandAsync(args, ct).ConfigureAwait(false)); }
            finally { ReturnConnection(conn); }
        }

        public bool MSet(params (string key, RedisValue value)[] pairs)
        {
            var args = new string[pairs.Length * 2 + 1];
            args[0] = "MSET";
            for (int i = 0; i < pairs.Length; i++)
            {
                args[i * 2 + 1] = pairs[i].key;
                args[i * 2 + 2] = pairs[i].value.AsString();
            }
            var conn = AcquireConnection();
            try
            {
                var resp = conn.SendCommand(args);
                ThrowIfError(resp);
                return resp.AsString() == "OK";
            }
            finally { ReturnConnection(conn); }
        }

        public long Incr(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("INCR", key)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public async Task<long> IncrAsync(string key, CancellationToken ct = default)
        {
            var conn = await AcquireConnectionAsync(ct).ConfigureAwait(false);
            try { return ExtractRedisValue(await conn.SendCommandAsync(new[] { "INCR", key }, ct).ConfigureAwait(false)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public long IncrBy(string key, long increment)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("INCRBY", key, increment.ToString())).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public long Decr(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("DECR", key)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public long DecrBy(string key, long decrement)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("DECRBY", key, decrement.ToString())).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public long Append(string key, string value)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("APPEND", key, value)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public long StrLen(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("STRLEN", key)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        // ═══════════════════════════════════════════════
        //  Hash Operations
        // ═══════════════════════════════════════════════

        public RedisValue HGet(string key, string field)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("HGET", key, field)); }
            finally { ReturnConnection(conn); }
        }

        public async Task<RedisValue> HGetAsync(string key, string field, CancellationToken ct = default)
        {
            var conn = await AcquireConnectionAsync(ct).ConfigureAwait(false);
            try { return ExtractRedisValue(await conn.SendCommandAsync(new[] { "HGET", key, field }, ct).ConfigureAwait(false)); }
            finally { ReturnConnection(conn); }
        }

        public bool HSet(string key, string field, RedisValue value)
        {
            var conn = AcquireConnection();
            try
            {
                var resp = conn.SendCommand("HSET", key, field, value.AsString());
                ThrowIfError(resp);
                return resp.AsInt64() == 1;
            }
            finally { ReturnConnection(conn); }
        }

        public async Task<bool> HSetAsync(string key, string field, RedisValue value, CancellationToken ct = default)
        {
            var conn = await AcquireConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                var resp = await conn.SendCommandAsync(new[] { "HSET", key, field, value.AsString() }, ct).ConfigureAwait(false);
                ThrowIfError(resp);
                return resp.AsInt64() == 1;
            }
            finally { ReturnConnection(conn); }
        }

        public RedisValue[] HMGet(string key, params string[] fields)
        {
            var args = new string[fields.Length + 2];
            args[0] = "HMGET";
            args[1] = key;
            Array.Copy(fields, 0, args, 2, fields.Length);
            var conn = AcquireConnection();
            try { return ExtractRedisValues(conn.SendCommand(args)); }
            finally { ReturnConnection(conn); }
        }

        public bool HMSet(string key, params (string field, RedisValue value)[] pairs)
        {
            var args = new string[pairs.Length * 2 + 2];
            args[0] = "HMSET";
            args[1] = key;
            for (int i = 0; i < pairs.Length; i++)
            {
                args[i * 2 + 2] = pairs[i].field;
                args[i * 2 + 3] = pairs[i].value.AsString();
            }
            var conn = AcquireConnection();
            try
            {
                var resp = conn.SendCommand(args);
                ThrowIfError(resp);
                return resp.AsString() == "OK";
            }
            finally { ReturnConnection(conn); }
        }

        public long HDel(string key, params string[] fields)
        {
            var args = new string[fields.Length + 2];
            args[0] = "HDEL";
            args[1] = key;
            Array.Copy(fields, 0, args, 2, fields.Length);
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand(args)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public Dictionary<string, RedisValue> HGetAll(string key)
        {
            var conn = AcquireConnection();
            try
            {
                var resp = conn.SendCommand("HGETALL", key);
                ThrowIfError(resp);
                var result = new Dictionary<string, RedisValue>();
                if (resp.ArrayValue != null)
                {
                    for (int i = 0; i < resp.ArrayValue.Length - 1; i += 2)
                        result[resp.ArrayValue[i].AsString()] = resp.ArrayValue[i + 1].ToRedisValue();
                }
                return result;
            }
            finally { ReturnConnection(conn); }
        }

        public string[] HKeys(string key)
        {
            var conn = AcquireConnection();
            try
            {
                var resp = conn.SendCommand("HKEYS", key);
                ThrowIfError(resp);
                if (resp.ArrayValue == null) return Array.Empty<string>();
                var result = new string[resp.ArrayValue.Length];
                for (int i = 0; i < resp.ArrayValue.Length; i++)
                    result[i] = resp.ArrayValue[i].AsString();
                return result;
            }
            finally { ReturnConnection(conn); }
        }

        public RedisValue[] HVals(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValues(conn.SendCommand("HVALS", key)); }
            finally { ReturnConnection(conn); }
        }

        // ═══════════════════════════════════════════════
        //  List Operations
        // ═══════════════════════════════════════════════

        public long LPush(string key, params RedisValue[] values)
        {
            var args = new string[values.Length + 2];
            args[0] = "LPUSH";
            args[1] = key;
            for (int i = 0; i < values.Length; i++)
                args[i + 2] = values[i].AsString();
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand(args)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public long RPush(string key, params RedisValue[] values)
        {
            var args = new string[values.Length + 2];
            args[0] = "RPUSH";
            args[1] = key;
            for (int i = 0; i < values.Length; i++)
                args[i + 2] = values[i].AsString();
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand(args)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public RedisValue LPop(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("LPOP", key)); }
            finally { ReturnConnection(conn); }
        }

        public RedisValue RPop(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("RPOP", key)); }
            finally { ReturnConnection(conn); }
        }

        public RedisValue[] LRange(string key, long start, long stop)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValues(conn.SendCommand("LRANGE", key, start.ToString(), stop.ToString())); }
            finally { ReturnConnection(conn); }
        }

        public long LLen(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("LLEN", key)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        // ═══════════════════════════════════════════════
        //  Set Operations
        // ═══════════════════════════════════════════════

        public long SAdd(string key, params RedisValue[] members)
        {
            var args = new string[members.Length + 2];
            args[0] = "SADD";
            args[1] = key;
            for (int i = 0; i < members.Length; i++)
                args[i + 2] = members[i].AsString();
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand(args)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public long SRem(string key, params RedisValue[] members)
        {
            var args = new string[members.Length + 2];
            args[0] = "SREM";
            args[1] = key;
            for (int i = 0; i < members.Length; i++)
                args[i + 2] = members[i].AsString();
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand(args)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public RedisValue[] SMembers(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValues(conn.SendCommand("SMEMBERS", key)); }
            finally { ReturnConnection(conn); }
        }

        public bool SIsMember(string key, RedisValue member)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("SISMEMBER", key, member.AsString())).AsInt64() == 1; }
            finally { ReturnConnection(conn); }
        }

        public long SCard(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("SCARD", key)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        // ═══════════════════════════════════════════════
        //  Key Operations
        // ═══════════════════════════════════════════════

        public long Del(params string[] keys)
        {
            var args = new string[keys.Length + 1];
            args[0] = "DEL";
            Array.Copy(keys, 0, args, 1, keys.Length);
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand(args)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public bool Exists(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("EXISTS", key)).AsInt64() == 1; }
            finally { ReturnConnection(conn); }
        }

        public bool Expire(string key, int seconds)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("EXPIRE", key, seconds.ToString())).AsInt64() == 1; }
            finally { ReturnConnection(conn); }
        }

        public long Ttl(string key)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("TTL", key)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        public string[] Keys(string pattern)
        {
            var conn = AcquireConnection();
            try
            {
                var resp = conn.SendCommand("KEYS", pattern);
                ThrowIfError(resp);
                if (resp.ArrayValue == null) return Array.Empty<string>();
                var result = new string[resp.ArrayValue.Length];
                for (int i = 0; i < resp.ArrayValue.Length; i++)
                    result[i] = resp.ArrayValue[i].AsString();
                return result;
            }
            finally { ReturnConnection(conn); }
        }

        public string Type(string key)
        {
            var conn = AcquireConnection();
            try
            {
                var resp = conn.SendCommand("TYPE", key);
                ThrowIfError(resp);
                return resp.AsString();
            }
            finally { ReturnConnection(conn); }
        }

        // ═══════════════════════════════════════════════
        //  Connection Operations
        // ═══════════════════════════════════════════════

        public string Ping()
        {
            var conn = AcquireConnection();
            try
            {
                var resp = conn.SendCommand("PING");
                ThrowIfError(resp);
                return resp.AsString();
            }
            finally { ReturnConnection(conn); }
        }

        public bool SelectDb(int database)
        {
            _database = database;
            var conn = AcquireConnection();
            try
            {
                var resp = conn.SendCommand("SELECT", database.ToString());
                ThrowIfError(resp);
                return resp.AsString() == "OK";
            }
            finally { ReturnConnection(conn); }
        }

        // ═══════════════════════════════════════════════
        //  Pub/Sub
        // ═══════════════════════════════════════════════

        public long Publish(string channel, string message)
        {
            var conn = AcquireConnection();
            try { return ExtractRedisValue(conn.SendCommand("PUBLISH", channel, message)).AsInt64(); }
            finally { ReturnConnection(conn); }
        }

        // ═══════════════════════════════════════════════
        //  Transactions
        // ═══════════════════════════════════════════════

        public RedisTransaction CreateTransaction()
        {
            var conn = AcquireConnection();
            return new RedisTransaction(conn, () => ReturnConnection(conn));
        }

        // ═══════════════════════════════════════════════
        //  Pipeline
        // ═══════════════════════════════════════════════

        public RespValue[] Pipeline(params byte[][] commands)
        {
            var conn = AcquireConnection();
            try
            {
                var results = conn.SendPipeline(commands);
                return results.ToArray();
            }
            finally { ReturnConnection(conn); }
        }

        public async Task<RespValue[]> PipelineAsync(byte[][] commands, CancellationToken ct = default)
        {
            var conn = await AcquireConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                var results = await conn.SendPipelineAsync(commands, ct).ConfigureAwait(false);
                return results.ToArray();
            }
            finally { ReturnConnection(conn); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            while (_pool.TryPop(out var conn))
            {
                try { conn.Dispose(); } catch { }
            }
            _semaphore.Dispose();
        }
    }

    public class RedisTransaction : IDisposable
    {
        private readonly RedisConnection _conn;
        private readonly Action _returnConnection;
        private readonly List<byte[]> _commands;
        private bool _executed;
        private bool _discarded;

        internal RedisTransaction(RedisConnection conn, Action returnConnection)
        {
            _conn = conn;
            _returnConnection = returnConnection;
            _commands = new List<byte[]>();
            var resp = _conn.SendCommand("MULTI");
            if (resp.Type == RespType.Error)
                throw new RedisException(resp.AsString());
        }

        public RedisTransaction QueueCommand(params string[] args)
        {
            if (_executed || _discarded)
                throw new InvalidOperationException("Transaction already executed or discarded");
            _commands.Add(RespParser.EncodeCommand(args));
            return this;
        }

        public RespValue[] Exec()
        {
            if (_executed) throw new InvalidOperationException("Transaction already executed");
            _executed = true;

            foreach (var cmd in _commands)
                _conn.SendRaw(cmd);

            var execResp = _conn.SendCommand("EXEC");
            if (execResp.Type == RespType.Error)
                throw new RedisException(execResp.AsString());
            if (execResp.ArrayValue == null) return Array.Empty<RespValue>();
            return execResp.ArrayValue;
        }

        public bool Discard()
        {
            if (_executed || _discarded)
                throw new InvalidOperationException("Transaction already executed or discarded");
            _discarded = true;
            var resp = _conn.SendCommand("DISCARD");
            return resp.AsString() == "OK";
        }

        public void Dispose()
        {
            if (!_executed && !_discarded)
            {
                try { Discard(); } catch { }
            }
            _returnConnection?.Invoke();
        }
    }

    public class RedisException : Exception
    {
        public RedisException(string message) : base(message) { }
        public RedisException(string message, Exception inner) : base(message, inner) { }
    }
}
