using Xunit;
using Nexus.OpcUa;
using System;

namespace Nexus.OpcUa.Tests
{
    public class OpcUaNodeIdTests
    {
        [Fact]
        public void Parse_NumericId()
        {
            var nodeId = OpcUaNodeId.Parse("ns=0;i=2258");
            Assert.Equal(0, nodeId.NamespaceIndex);
            Assert.Equal(OpcUaNodeIdType.Numeric, nodeId.IdType);
            Assert.Equal(2258u, nodeId.Identifier);
        }

        [Fact]
        public void Parse_NumericId_NonZeroNamespace()
        {
            var nodeId = OpcUaNodeId.Parse("ns=2;i=100");
            Assert.Equal(2, nodeId.NamespaceIndex);
            Assert.Equal(100u, nodeId.Identifier);
            Assert.Equal(OpcUaNodeIdType.Numeric, nodeId.IdType);
        }

        [Fact]
        public void Parse_StringId()
        {
            var nodeId = OpcUaNodeId.Parse("ns=1;s=Temperature");
            Assert.Equal(1, nodeId.NamespaceIndex);
            Assert.Equal(OpcUaNodeIdType.String, nodeId.IdType);
            Assert.Equal("Temperature", nodeId.Identifier);
        }

        [Fact]
        public void Parse_GuidId()
        {
            var guid = Guid.NewGuid();
            var nodeId = OpcUaNodeId.Parse($"ns=0;g={guid}");
            Assert.Equal(0, nodeId.NamespaceIndex);
            Assert.Equal(OpcUaNodeIdType.Guid, nodeId.IdType);
            Assert.Equal(guid, nodeId.Identifier);
        }

        [Fact]
        public void Parse_BareNumeric_DefaultsToNs0()
        {
            var nodeId = OpcUaNodeId.Parse("i=2258");
            Assert.Equal(0, nodeId.NamespaceIndex);
            Assert.Equal(2258u, nodeId.Identifier);
            Assert.Equal(OpcUaNodeIdType.Numeric, nodeId.IdType);
        }

        [Fact]
        public void Parse_BareString_DefaultsToNs0()
        {
            var nodeId = OpcUaNodeId.Parse("s=Hello");
            Assert.Equal(0, nodeId.NamespaceIndex);
            Assert.Equal(OpcUaNodeIdType.String, nodeId.IdType);
            Assert.Equal("Hello", nodeId.Identifier);
        }

        [Fact]
        public void Parse_EmptyString_Throws()
        {
            Assert.Throws<ArgumentException>(() => OpcUaNodeId.Parse(""));
        }

        [Fact]
        public void Parse_InvalidFormat_Throws()
        {
            Assert.Throws<FormatException>(() => OpcUaNodeId.Parse("ns=0;i=1;extra"));
        }

        [Fact]
        public void Parse_MissingNs_Throws()
        {
            Assert.Throws<FormatException>(() => OpcUaNodeId.Parse("x=0;i=1"));
        }

        [Fact]
        public void ToString_Numeric()
        {
            var nodeId = new OpcUaNodeId(0, 100u);
            Assert.Equal("ns=0;i=100", nodeId.ToString());
        }

        [Fact]
        public void ToString_String()
        {
            var nodeId = new OpcUaNodeId(1, "MyVar");
            Assert.Equal("ns=1;s=MyVar", nodeId.ToString());
        }

        [Fact]
        public void ToString_Guid()
        {
            var guid = new Guid("12345678-1234-1234-1234-123456789abc");
            var nodeId = new OpcUaNodeId(0, guid);
            Assert.Equal($"ns=0;g={guid}", nodeId.ToString());
        }

        [Fact]
        public void Equals_SameNumericId()
        {
            var a = new OpcUaNodeId(0, 100u);
            var b = new OpcUaNodeId(0, 100u);
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void Equals_DifferentNamespace_NotEqual()
        {
            var a = new OpcUaNodeId(0, 100u);
            var b = new OpcUaNodeId(1, 100u);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Equals_StringId()
        {
            var a = new OpcUaNodeId(0, "test");
            var b = new OpcUaNodeId(0, "test");
            Assert.Equal(a, b);
        }

        [Fact]
        public void Encode_NumericTwoByte()
        {
            var nodeId = new OpcUaNodeId(0, 42u);
            var encoded = nodeId.Encode();
            Assert.Equal(2, encoded.Length);
            Assert.Equal(0x00, encoded[0]);
            Assert.Equal(42, encoded[1]);
        }

        [Fact]
        public void Encode_NumericFourByte()
        {
            var nodeId = new OpcUaNodeId(1, 300u);
            var encoded = nodeId.Encode();
            Assert.Equal(4, encoded.Length);
            Assert.Equal(0x01, encoded[0]);
        }

        [Fact]
        public void RoundTrip_Parse_ToString()
        {
            var original = "ns=3;s=MyVariable";
            var nodeId = OpcUaNodeId.Parse(original);
            Assert.Equal(original, nodeId.ToString());
        }

        [Fact]
        public void RoundTrip_Encode_NumericTwoByte()
        {
            var nodeId = new OpcUaNodeId(0, 200u);
            var encoded = nodeId.Encode();
            Assert.Equal(0x00, encoded[0]);
            Assert.Equal(200, encoded[1]);
        }
    }

    public class OpcUaSessionTests
    {
        [Fact]
        public void DefaultValues()
        {
            var session = new OpcUaSession();
            Assert.Equal(0u, session.SessionId);
            Assert.Equal(0, session.SessionNamespace);
            Assert.Equal(AuthenticationType.Anonymous, session.AuthType);
            Assert.NotNull(session.NamespaceTable);
            Assert.Empty(session.NamespaceTable);
            Assert.NotNull(session.SubscriptionMap);
            Assert.Empty(session.SubscriptionMap);
        }

        [Fact]
        public void NextRequestHandle_Increments()
        {
            var session = new OpcUaSession();
            int h1 = session.NextRequestHandle();
            int h2 = session.NextRequestHandle();
            Assert.Equal(h1 + 1, h2);
        }

        [Fact]
        public void NextSequenceNumber_Increments()
        {
            var session = new OpcUaSession();
            uint s1 = session.NextSequenceNumber();
            uint s2 = session.NextSequenceNumber();
            Assert.Equal(s1 + 1, s2);
        }

        [Fact]
        public void Reset_ClearsAll()
        {
            var session = new OpcUaSession();
            session.NextRequestHandle();
            session.NextSequenceNumber();
            session.NamespaceTable.Add("http://test");
            session.UpdateSecurityToken(10, DateTime.UtcNow, 600000);
            session.UserName = "admin";
            session.Password = "pass";

            session.Reset();

            Assert.Equal(0u, session.SessionId);
            Assert.Equal(0u, session.SecureChannelId);
            Assert.Equal(0u, session.SecurityTokenId);
            Assert.Null(session.AuthenticationToken);
            Assert.Empty(session.NamespaceTable);
            Assert.Empty(session.SubscriptionMap);
            Assert.Equal(AuthenticationType.Anonymous, session.AuthType);
            Assert.Null(session.UserName);
            Assert.Null(session.Password);
        }

        [Fact]
        public void IsTokenExpired_ZeroLifetime_ReturnsFalse()
        {
            var session = new OpcUaSession();
            Assert.False(session.IsTokenExpired());
        }

        [Fact]
        public void IsTokenRenewalDue_ZeroLifetime_ReturnsFalse()
        {
            var session = new OpcUaSession();
            Assert.False(session.IsTokenRenewalDue());
        }

        [Fact]
        public void UpdateSecurityToken_SetsValues()
        {
            var session = new OpcUaSession();
            var now = DateTime.UtcNow;
            session.UpdateSecurityToken(42, now, 600000);
            Assert.Equal(42u, session.SecurityTokenId);
            Assert.Equal(now, session.SecurityTokenCreatedAt);
            Assert.Equal(600000u, session.SecurityTokenLifetime);
        }
    }

    public class OpcUaEnumTests
    {
        [Theory]
        [InlineData(OpcUaNodeIdType.TwoByte)]
        [InlineData(OpcUaNodeIdType.FourByte)]
        [InlineData(OpcUaNodeIdType.Numeric)]
        [InlineData(OpcUaNodeIdType.String)]
        [InlineData(OpcUaNodeIdType.Guid)]
        [InlineData(OpcUaNodeIdType.Opaque)]
        public void NodeIdType_AllDefined(OpcUaNodeIdType type)
        {
            Assert.True(Enum.IsDefined(typeof(OpcUaNodeIdType), type));
        }

        [Theory]
        [InlineData(AuthenticationType.Anonymous)]
        [InlineData(AuthenticationType.UserName)]
        [InlineData(AuthenticationType.X509Certificate)]
        public void AuthenticationType_AllDefined(AuthenticationType type)
        {
            Assert.True(Enum.IsDefined(typeof(AuthenticationType), type));
        }
    }
}
