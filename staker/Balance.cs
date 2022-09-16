using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace staker
{
    internal class Balance
    {
        public string txid;
        public string address;
        public decimal amount;
        public string type;
        public int term;
        public uint blockheight;

        public Balance()
        {
            txid = "";
            address = "";
            amount = 0;
            type = "";
            term = 0;
            blockheight = 0;
        }
    }
}
