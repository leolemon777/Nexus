using Nexus.Mqtt;
using Xunit;

namespace Nexus.Mqtt.Tests
{
    public class MqttTopicFilterTests
    {
        [Theory]
        [InlineData("sport/tennis/player1/#", "sport/tennis/player1", true)]
        [InlineData("sport/tennis/player1/#", "sport/tennis/player1/ranking", true)]
        [InlineData("sport/tennis/player1/#", "sport/tennis/player1/score/wimbledon", true)]
        [InlineData("sport/#", "sport", true)]
        [InlineData("#", "sport/tennis/player1", true)]
        [InlineData("sport/tennis/+", "sport/tennis/player1", true)]
        [InlineData("sport/tennis/+", "sport/tennis/player2", true)]
        [InlineData("sport/+", "sport/tennis", true)]
        [InlineData("+/+", "sport/tennis", true)]
        [InlineData("+", "sport", true)]
        [InlineData("+/tennis/player1", "sport/tennis/player1", true)]
        [InlineData("sport/tennis/player1", "sport/tennis/player1", true)]
        [InlineData("sport/tennis/+", "sport/tennis/player1/ranking", false)]
        [InlineData("sport/+", "sport", false)]
        [InlineData("sport/+/player1", "sport/tennis/player1", true)]
        [InlineData("sport/tennis/player1", "sport/tennis", false)]
        [InlineData("sport/tennis", "sport/tennis/player1", false)]
        public void IsMatch_ReturnsExpected(string filter, string topic, bool expected)
        {
            Assert.Equal(expected, MqttTopicFilter.IsMatch(topic, filter));
        }

        [Theory]
        [InlineData("sport/tennis/+", true)]
        [InlineData("sport/#", true)]
        [InlineData("+", true)]
        [InlineData("#", true)]
        [InlineData("+/+", true)]
        [InlineData("sport/+/player1/#", true)]
        [InlineData("sport/tennis", true)]
        [InlineData("", false)]
        public void IsValidTopicFilter_ReturnsExpected(string filter, bool expected)
        {
            Assert.Equal(expected, MqttTopicFilter.IsValidTopicFilter(filter));
        }

        [Theory]
        [InlineData("sport/tennis", true)]
        [InlineData("/", true)]
        [InlineData("a", true)]
        [InlineData("", false)]
        [InlineData("sport/+", false)]
        [InlineData("sport/#", false)]
        public void IsValidTopicName_ReturnsExpected(string topic, bool expected)
        {
            Assert.Equal(expected, MqttTopicFilter.IsValidTopicName(topic));
        }

        [Fact]
        public void IsMatch_MultiLevelWildcard_MatchesEmptyLevels()
        {
            Assert.True(MqttTopicFilter.IsMatch("sport", "sport/#"));
        }

        [Fact]
        public void IsMatch_SingleLevelWildcard_DoesNotMatchMultipleLevels()
        {
            Assert.False(MqttTopicFilter.IsMatch("a/b/c", "+/+"));
        }

        [Fact]
        public void IsMatch_ExactMatch()
        {
            Assert.True(MqttTopicFilter.IsMatch("a/b/c", "a/b/c"));
        }

        [Fact]
        public void IsMatch_DifferentTopics_ReturnsFalse()
        {
            Assert.False(MqttTopicFilter.IsMatch("a/b/c", "a/b/d"));
        }

        [Fact]
        public void IsMatch_Null_ThrowsArgumentNull()
        {
            Assert.Throws<System.ArgumentNullException>(() => MqttTopicFilter.IsMatch(null, "a"));
            Assert.Throws<System.ArgumentNullException>(() => MqttTopicFilter.IsMatch("a", null));
        }
    }
}
