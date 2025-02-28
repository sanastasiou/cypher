﻿// CYPCore by Matthew Hellyer is licensed under CC BY-NC-ND 4.0.
// To view a copy of this license, visit https://creativecommons.org/licenses/by-nc-nd/4.0

namespace CYPCore.Network
{
    public class Peer
    {
        public string Host { get; set; }
        public string PublicKey { get; set; }
        public ulong ClientId { get; set; }
        public string NodeName { get; set; }
        public string NodeVersion { get; set; }
        public long BlockHeight { get; set; }
    }
}
