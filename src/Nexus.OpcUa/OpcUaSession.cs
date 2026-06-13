#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.OpcUa
{
    public enum AuthenticationType
    {
        Anonymous,
        UserName,
        X509Certificate
    }

    public class OpcUaSession
    {
        private int _requestHandle;
        private int _sequenceNumber;

        public uint SessionId { get; internal set; }
        public ushort SessionNamespace { get; internal set; }
        public OpcUaNodeId AuthenticationToken { get; internal set; }
        public uint SecureChannelId { get; internal set; }
        public uint SecurityTokenId { get; internal set; }
        public DateTime SecurityTokenCreatedAt { get; internal set; }
        public uint SecurityTokenLifetime { get; internal set; }
        public byte[] ServerNonce { get; internal set; }
        public double RevisedTimeout { get; internal set; }
        public uint MaxRequestMessageSize { get; internal set; }

        public AuthenticationType AuthType { get; set; } = AuthenticationType.Anonymous;
        public string UserName { get; set; }
        public string Password { get; set; }

        public List<string> NamespaceTable { get; } = new List<string>();
        public Dictionary<uint, OpcUaNodeId> SubscriptionMap { get; } = new Dictionary<uint, OpcUaNodeId>();

        public uint LastAcknowledgedSequence { get; internal set; }
        public uint LastReceivedSequence { get; internal set; }
        public bool NeedsAcknowledgement { get; internal set; }

        public int NextRequestHandle() => Interlocked.Increment(ref _requestHandle);

        public uint NextSequenceNumber()
        {
            return (uint)Interlocked.Increment(ref _sequenceNumber);
        }

        public bool IsTokenExpired()
        {
            if (SecurityTokenLifetime == 0) return false;
            var elapsed = (DateTime.UtcNow - SecurityTokenCreatedAt).TotalMilliseconds;
            return elapsed > SecurityTokenLifetime * 0.75;
        }

        public bool IsTokenRenewalDue()
        {
            if (SecurityTokenLifetime == 0) return false;
            var elapsed = (DateTime.UtcNow - SecurityTokenCreatedAt).TotalMilliseconds;
            return elapsed > SecurityTokenLifetime * 0.5;
        }

        public void UpdateSecurityToken(uint tokenId, DateTime createdAt, uint lifetime)
        {
            SecurityTokenId = tokenId;
            SecurityTokenCreatedAt = createdAt;
            SecurityTokenLifetime = lifetime;
        }

        public void Reset()
        {
            _requestHandle = 0;
            _sequenceNumber = 0;
            SessionId = 0;
            SessionNamespace = 0;
            AuthenticationToken = null;
            SecureChannelId = 0;
            SecurityTokenId = 0;
            SecurityTokenCreatedAt = DateTime.MinValue;
            SecurityTokenLifetime = 0;
            ServerNonce = null;
            RevisedTimeout = 0;
            MaxRequestMessageSize = 0;
            LastAcknowledgedSequence = 0;
            LastReceivedSequence = 0;
            NeedsAcknowledgement = false;
            NamespaceTable.Clear();
            SubscriptionMap.Clear();
            AuthType = AuthenticationType.Anonymous;
            UserName = null;
            Password = null;
        }
    }
}
