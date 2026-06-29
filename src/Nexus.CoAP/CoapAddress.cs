using System;
using Nexus;

namespace Nexus.CoAP
{
    public sealed class CoapAddress : IDataAddress
    {
        public string Original { get; }
        public string UriPath { get; }
        public string UriQuery { get; }

        public CoapAddress(string original, string uriPath, string uriQuery = "")
        {
            Original = original;
            UriPath = uriPath;
            UriQuery = uriQuery;
        }
    }

    public sealed class CoapAddressParser : IAddressParser<CoapAddress>
    {
        public CoapAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim();

            // 格式: /path 或 /path?key=value
            int queryIdx = address.IndexOf('?');
            if (queryIdx >= 0)
            {
                string path = address.Substring(0, queryIdx);
                string query = address.Substring(queryIdx + 1);
                return new CoapAddress(original, path, query);
            }

            return new CoapAddress(original, address);
        }

        public bool TryParse(string address, out CoapAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
