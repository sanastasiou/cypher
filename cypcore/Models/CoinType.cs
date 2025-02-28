﻿// TGMNode by Matthew Hellyer is licensed under CC BY-NC-ND 4.0.
// To view a copy of this license, visit https://creativecommons.org/licenses/by-nc-nd/4.0

namespace CYPCore.Models
{
    public enum CoinType : sbyte
    {
        Empty,
        Coin,
        Coinbase,
        Coinstake,
        Fee,
        Genesis,
        Payment,
        Change
    }
}
