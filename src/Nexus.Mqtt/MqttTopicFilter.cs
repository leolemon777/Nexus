using System;

namespace Nexus.Mqtt
{
    public static class MqttTopicFilter
    {
        public static bool IsMatch(string topic, string filter)
        {
            if (topic == null) throw new ArgumentNullException(nameof(topic));
            if (filter == null) throw new ArgumentNullException(nameof(filter));

            if (filter.Length == 0 || topic.Length == 0)
                return false;

            string[] filterLevels = filter.Split('/');
            string[] topicLevels = topic.Split('/');

            for (int i = 0; i < filterLevels.Length; i++)
            {
                string fLevel = filterLevels[i];

                if (fLevel == "#")
                    return true;

                if (i >= topicLevels.Length)
                    return false;

                if (fLevel != "+" && fLevel != topicLevels[i])
                    return false;
            }

            return filterLevels.Length == topicLevels.Length;
        }

        public static bool IsValidTopicFilter(string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return false;

            int len = filter.Length;
            for (int i = 0; i < len; i++)
            {
                char c = filter[i];
                if (c == '#')
                {
                    if (i != len - 1)
                        return false;
                    if (i > 0 && filter[i - 1] != '/')
                        return false;
                }
                else if (c == '+')
                {
                    if (i > 0 && filter[i - 1] != '/')
                        return false;
                    if (i < len - 1 && filter[i + 1] != '/')
                        return false;
                }
            }
            return true;
        }

        public static bool IsValidTopicName(string topic)
        {
            if (string.IsNullOrEmpty(topic))
                return false;

            for (int i = 0; i < topic.Length; i++)
            {
                char c = topic[i];
                if (c == '#' || c == '+')
                    return false;
            }
            return true;
        }
    }
}
