namespace CrossplaySteamDoor
{
    internal static class CrossplayAddress
    {
        // Vanilla formats the listed address as "ip:port" or "[ipv6]:port", and as "" while the public IP is unknown.
        internal static string NextPort(string address)
        {
            if (string.IsNullOrEmpty(address)) return address;

            var colon = address.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(address.Substring(colon + 1), out var port) || port >= ushort.MaxValue)
            {
                return address;
            }

            return address.Substring(0, colon + 1) + (port + 1);
        }
    }
}
