using LevelDB;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Xml.Linq;


namespace staker
{
    public class BlockScanner
    {

        const uint epochLength = 40;
        const uint epochCooldown = 10;  //# of blocks after epoch to do payout calc  
        const uint claimExpire = 4;     //# of blocks that claims expire in
        const uint payoutCooldown = 5;  //# of blocks after calc to start submitting payouts
        const uint payoutExpire = 10;   //# of blocks after first block to submit payout that payouts expire
        const uint txidClaimMod = 16;

        static HttpWebRequest webRequest;

        bool Shutdown = false;
        int currentBlockHeight;
        uint lastBlockTimestamp;

        Object rpcExecLock = new Object();

        //all current staking balances
        //when a staking command is processed, add to the list
        //when an unstake command is processed, remove from the list
        //key is txid
        //value is json of amount, type, address, term
        public Database balances;

        //all claims submitted
        //when a claims is submitted, add to the list
        //when a claims is in expired epoch, remove from the list
        //key is txid
        //value is json of epoch and count
        public Database claims;

        //payouts calculated by staking system
        //at end of each epoch calculate payouts and add to list
        //when a payout is claimed, remove it from the list
        //if a payout expires, remove it from the list
        //key is txid
        //value is amount and expiration block
        public Database payouts;


        //list of TXID for which I should submit claims and request payouts
        List<string> myTXID = new List<string>();

        Dictionary<UInt32, string> blockHashHistory = new Dictionary<UInt32, string>();

        //staking power by term and type
        Dictionary<uint, decimal> flexPower = new Dictionary<uint, decimal>();
        Dictionary<uint, decimal> lockPower = new Dictionary<uint, decimal>();

        bool initialBlockDownload = true;

        public void run()
        {

            flexPower.Add(30, 1m);
            flexPower.Add(60, 1.5m);
            flexPower.Add(90, 2.25m);
            flexPower.Add(120, 3.37m);
            flexPower.Add(180, 5.06m);
            flexPower.Add(360, 7.59m);
            flexPower.Add(720, 11.39m);

            lockPower.Add(30, 2m);
            lockPower.Add(60, 3m);
            lockPower.Add(90, 4.5m);
            lockPower.Add(120, 6.74m);
            lockPower.Add(180, 10.12m);
            lockPower.Add(360, 15.18m);
            lockPower.Add(720, 22.78m);


            balances = new Database("_balances");
            claims = new Database("_claims");
            payouts = new Database("_payouts");

            if (File.Exists("addresses.txt"))
            {
                dynamic a = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText("addresses.txt"));
                for ( int i = 0; i < a.Count; i++) {
                    string addr = a[i].ToString();
                    ReadOptions o = new ReadOptions { };
                    Iterator i1 = balances.db.CreateIterator(o);
                    for (i1.SeekToFirst(); i1.IsValid(); i1.Next())
                    {
                        dynamic stake = JsonConvert.DeserializeObject<dynamic>(i1.ValueAsString());
                        if (stake["address"] == addr)
                            myTXID.Add(stake["txid"].ToString());
                    }

                    }
                }

            try
            {
                UInt32 lastBlock;
                if (File.Exists("last_checkpoint.txt"))
                {
                    lastBlock = Convert.ToUInt32(File.ReadAllText("last_checkpoint.txt"));
                }
                else
                {
                    lastBlock = 2138000;
                    File.WriteAllText("last_checkpoint.txt", lastBlock.ToString());
                }

                while (!Shutdown)
                {
                    int currentHeight = getCurrentHeight();
                    if (currentHeight != -1)
                    {
                        Console.WriteLine("currentHeight: " + currentHeight);
                        currentBlockHeight = (int)currentHeight;

                        if (currentHeight - lastBlock < 2)
                            initialBlockDownload = false;

                        if (lastBlock < currentHeight)
                        {
                            while ((lastBlock < currentHeight) && (!Shutdown))
                            {
                                lastBlock++;
                                Console.WriteLine("lastBock: " + lastBlock);
                                try
                                {
                                    parseBlock(lastBlock);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine("Error parsing block " + lastBlock + " " + ex.Message);
                                }
                                if (lastBlock % 100 == 0)
                                    Console.WriteLine("Parsing block: " + lastBlock);

                            }
                        }


                        if (currentHeight - lastBlock < Database.maxHistory)
                        {
                            bool mismatch = false;
                            UInt32 block = lastBlock;
                            while ((!mismatch) && (block > lastBlock - Database.maxHistory))
                            {
                                string blockHashRPC = rpcExec("{\"jsonrpc\": \"1.0\", \"id\":\"1\", \"method\": \"getblockhash\", \"params\": [" + block + "] }");
                                string blockHash = JsonConvert.DeserializeObject<dynamic>(blockHashRPC)["result"].ToString();
                                if (blockHashHistory.ContainsKey(block))
                                    if (blockHashHistory[block] != blockHash)
                                        mismatch = true;

                                if (mismatch)
                                {
                                    block--;

                                    Console.WriteLine("Rollback from " + lastBlock + " to " + block);

                                    lock (balances)
                                    {
                                        balances.Rollback(block);
                                        claims.Rollback(block);
                                        payouts.Rollback(block);
                                    }
                                    lastBlock = block;

                                    blockHashHistory.Clear();
                                }
                                else
                                    block--;
                            }

                        }

                    }
                    Thread.Sleep(5000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error in BlockScanner.run, exiting: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }

        }


        void parseBlock(UInt32 blockHeight)
        {

            dynamic dBlockResult = null;

            string filename = "blocks\\block" + blockHeight + ".dat";
            if (File.Exists(filename))
            {
                string data = File.ReadAllText(filename);
                dBlockResult = JsonConvert.DeserializeObject<dynamic>(data);
            }
            else
            {
                string blockHash = rpcExec("{\"jsonrpc\": \"1.0\", \"id\":\"1\", \"method\": \"getblockhash\", \"params\": [" + blockHeight + "] }");

                dynamic dHashResult = JsonConvert.DeserializeObject<dynamic>(blockHash)["result"];

                string block = rpcExec("{\"jsonrpc\": \"1.0\", \"id\":\"1\", \"method\": \"getblock\", \"params\": [\"" + dHashResult + "\", 2] }");

                dBlockResult = JsonConvert.DeserializeObject<dynamic>(block)["result"];

                File.WriteAllText(filename, JsonConvert.SerializeObject(dBlockResult));
            }

            Console.WriteLine(dBlockResult["hash"].ToString());

            blockHashHistory.Add(blockHeight, dBlockResult["hash"].ToString());
            if (blockHashHistory.Count > Database.maxHistory)
            {
                uint min = 999999999;
                foreach (uint height in blockHashHistory.Keys)
                    if (height < min)
                        min = height;

                blockHashHistory.Remove(min);
            }


            uint timestamp = dBlockResult["time"];
            lastBlockTimestamp = timestamp;

            bool isCoinbase = true;
            foreach (var tx in dBlockResult["tx"])
            {
                string from = "";
                foreach (var vin in tx["vin"])
                {

                    if (vin.ContainsKey("coinbase"))
                        from = "coinbase";
                    else
                    {
                        string vinTXID = vin["txid"];
                        int vout = vin["vout"];
                        string key = vinTXID + vout.ToString();
                        /*
                        if (Global.txList.ContainsKey(key))
                            from = Global.txList[key].address;
                        else
                            Log.log("ERROR TX vin not found " + key);
                        */
                    }

                    if (vin.ContainsKey("txid"))
                    {
//                            Global.spendTransaction(vin["txid"].ToString(), Convert.ToInt32(vin["vout"]));
                    }

                }
                foreach (var vout in tx["vout"])
                {
                    string command = vout["scriptPubKey"]["asm"].ToString();
                    if (command.StartsWith("OP_RETURN"))
                    {
                        string decode = FromHexString(command.Substring(10));
                        if (decode.StartsWith("st01"))
                        {
                            decode = decode.Substring(4);
                            dynamic dCommand = JsonConvert.DeserializeObject<dynamic>(decode);
                            if (dCommand != null)
                            {
                                if (dCommand["command"] == "stake")
                                {
                                    Balance b = new Balance();
                                    b.txid = tx["txid"].ToString();
                                    b.address = dCommand["wallet"];
                                    b.amount = Convert.ToDecimal(vout["value"]) * 100000000m;
                                    b.type = dCommand["type"];
                                    b.blockheight = blockHeight;
                                    if (b.type == "lock")
                                        b.term = Convert.ToInt32(dCommand["term"]);
                                    lock (balances)
                                        balances.db.Put(b.txid, JsonConvert.SerializeObject(b));

                                    //todo - maybe - if there is a rollback due to a fork, this will be processed a second time
                                    //dont think it matters because the txid is the same - maybe some timing or sequencing issues?
                                    dynamic a = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText("addresses.txt"));
                                    for (int i = 0; i < a.Count; i++)
                                    {
                                        string addr = a[i].ToString();
                                        if (addr == b.address)
                                            if (!myTXID.Contains(b.txid))
                                                myTXID.Add(b.txid);

                                    }
                                }

                                else if (dCommand["command"] == "unstake")
                                {
                                    lock (balances)
                                        balances.db.Delete(dCommand["txid"].ToString());

                                    if (myTXID.Contains(dCommand["txid"].ToString()))
                                        myTXID.Remove(dCommand["txid"].ToString());

                                }

                                else if (dCommand["command"] == "claim")
                                {
                                    Claim c = new Claim();
                                    c.txid = dCommand["txid"].ToString() + "-" + blockHeight;
                                    c.blockheight = blockHeight;
                                    lock (claims)
                                        claims.db.Put(c.txid, JsonConvert.SerializeObject(c));
                                }

                                else if (dCommand["command"] == "payout")
                                {
                                    lock (payouts)
                                        if (payouts.db.Get(dCommand["txid"].ToString()) != null)
                                            payouts.db.Delete(dCommand["txid"].ToString());
                                }

                            }
                        }
                    }
                    else
                    {
                        bool ok = true;

                        if (!vout["scriptPubKey"].ContainsKey("address"))
                            ok = false;

                        if (ok)
                        {
                            {
                                /*
                                Global.saveTx(tx["txid"].ToString(), Convert.ToInt32(vout["n"]), Convert.ToDecimal(vout["value"]), vout["scriptPubKey"]["address"].ToString(), isCoinbase, (int)blockHeight);
                                Global.updateWalletBalance(vout["scriptPubKey"]["address"].ToString(), Convert.ToDecimal(vout["value"]) * 100000000m);
                                Global.addWalletHistory(from, vout["scriptPubKey"]["address"].ToString(), timestamp, Convert.ToDecimal(vout["value"]) * 100000000m);
                                Swap.processWalletTX(from, vout["scriptPubKey"]["address"].ToString(), Convert.ToDecimal(vout["value"]) * 100000000m, blockHeight);
                                */
                            }
                        }
                    }
                }
                isCoinbase = false; //first transaction only 
            }

            //if new epoch - calc payouts after cooldown and save
            if ((blockHeight % epochLength) == epochCooldown)
                CalcPayouts(blockHeight);

            //expire unclaimed payouts
            if ((blockHeight % epochLength) == epochCooldown + payoutExpire)
                ExpirePayouts();

            //if after epoch + cooldown + payoutcooldown, submit payout transaction
            if ((blockHeight % epochLength) >= epochCooldown + payoutCooldown)
                SubmitPayouts();

            //if I need to claim, submit claim transaction
            CheckClaim(dBlockResult["hash"].ToString());

            lock (balances)
            {
                balances.NewBlock(blockHeight);
                claims.NewBlock(blockHeight);
                payouts.NewBlock(blockHeight);
            }

            File.WriteAllText("last_checkpoint.txt", blockHeight.ToString());

        }


        void CheckClaim(string blockHash)
        {
            uint blockHashSum = 0;

            for (var i = 0; i < blockHash.Length / 2; i++)
                blockHashSum += Convert.ToByte(blockHash.Substring(i * 2, 2), 16);

            foreach (string txid in myTXID)
            {
                uint txidSum = 0;
                for (var i = 0; i < txid.Length / 2; i++)
                    txidSum += Convert.ToByte(txid.Substring(i * 2, 2), 16);

                if (((blockHashSum + txidSum) % txidClaimMod) == 0)
                {
                    Console.WriteLine("-----------------submit claim-----------------");
                }

            }
        }

        void CalcPayouts(uint blockHeight)
        {
            //count tickets by txid
            //for each txid calc (count * amt staked * power)
            //sum all amounts
            //for each txid calc proportion * epoch staking rewards

            uint epochEnd = (blockHeight / epochLength) * epochLength - 1;

            decimal totalRewards = epochLength * 30000000;

            Dictionary<string, uint> ticketCount = new Dictionary<string, uint>();

            lock(payouts) {
                lock (balances)
                {
                    lock (claims)
                    {
                        ReadOptions o = new ReadOptions { };

                        Iterator i1 = claims.db.CreateIterator(o);
                        for (i1.SeekToFirst(); i1.IsValid(); i1.Next())
                        {
                            Claim c = JsonConvert.DeserializeObject<Claim>(i1.ValueAsString());
                            if (c.blockheight <= epochEnd)
                            {
                                if (ticketCount.ContainsKey(c.txid))
                                    ticketCount[c.txid]++;
                                else
                                    ticketCount.Add(c.txid, 1);
                                claims.db.Delete(i1.KeyAsString());
                            }
                        }

                        decimal totalStakePower = 0;
                        Dictionary<string, decimal> stakingPower = new Dictionary<string, decimal>();

                        foreach (string txid in ticketCount.Keys)
                        {
                            if (balances.db.Get(txid) != null)
                            {
                                Balance b = JsonConvert.DeserializeObject<Balance>(balances.db.Get(txid));
                                uint term;
                                decimal power;
                                if (b.type == "lock")
                                {
                                    term = (uint)b.term;
                                    power = lockPower[term];
                                }
                                else
                                {
                                    term = (blockHeight - b.blockheight) / 144000;  //roughly 4800 blocks per day = 144,000 per 30 days
                                    if (term < 30)
                                        term = 30;
                                    else if (term < 60)
                                        term = 60;
                                    else if (term < 90)
                                        term = 90;
                                    else if (term < 120)
                                        term = 120;
                                    else if (term < 180)
                                        term = 180;
                                    else if (term < 360)
                                        term = 360;
                                    else
                                        term = 720;
                                    power = flexPower[term];
                                }
                                power = power * b.amount * (decimal)ticketCount[txid];
                                totalStakePower += power;
                                stakingPower.Add(txid, power);

                            }
                        }

                        foreach (string txid in stakingPower.Keys)
                        {
                            Payout p = new Payout();
                            p.amount = stakingPower[txid] * totalRewards / totalStakePower;
                            p.txid = txid;
                            payouts.db.Put(txid,JsonConvert.SerializeObject(p));
                        }

                    }
                }
            }

            DumpTables();
        }

        void SubmitPayouts()
        {
            foreach (string txid in myTXID)
                Console.WriteLine("------------------------ submit payout ----------------");
        }

        void ExpirePayouts()
        {
            //just empty the database
            lock (payouts)
            {
                payouts.db.Close();
                payouts.db.Dispose();

                Options o1 = new Options { };
                DB.Destroy(o1, "_payouts");

                Options options = new Options { CreateIfMissing = true };
                payouts.db = new DB(options, "_payouts");
            }
        }


        void DumpTables()
        {
            ReadOptions o = new ReadOptions { };

            Iterator i1 = balances.db.CreateIterator(o);
            Console.WriteLine("=========balances==========");
            for (i1.SeekToFirst(); i1.IsValid(); i1.Next())
                Console.WriteLine(i1.KeyAsString() + "  " + i1.ValueAsString());
            Console.WriteLine();

            i1 = claims.db.CreateIterator(o);
            Console.WriteLine("=========claims==========");
            for (i1.SeekToFirst(); i1.IsValid(); i1.Next())
                Console.WriteLine(i1.KeyAsString() + "  " + i1.ValueAsString());
            Console.WriteLine();

            i1 = payouts.db.CreateIterator(o);
            Console.WriteLine("=========payouts==========");
            for (i1.SeekToFirst(); i1.IsValid(); i1.Next())
                Console.WriteLine(i1.KeyAsString() + "  " + i1.ValueAsString());
            Console.WriteLine();

        }


        int getCurrentHeight()
        {
            string result = rpcExec("{\"jsonrpc\": \"1.0\", \"id\":\"1\", \"method\": \"getblockcount\", \"params\": [] }");

            Console.WriteLine("get block count result: " + result);

            int iResult = -1;

            try
            {
                dynamic dResult = JsonConvert.DeserializeObject<dynamic>(result)["result"];
                iResult = dResult;
            }
            catch (Exception ex)
            {
            }


            return iResult;

        }

        public static bool HasProperty(dynamic obj, string name)
        {
            Type objType = obj.GetType();

            if (objType == typeof(ExpandoObject))
            {
                return ((IDictionary<string, object>)obj).ContainsKey(name);
            }

            return objType.GetProperty(name) != null;
        }

        public string rpcExec(string command)
        {
            string submitResponse = "";

            try
            {
                lock (rpcExecLock)
                {
                    webRequest = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:6432/");
                    webRequest.KeepAlive = false;

                    var data = Encoding.ASCII.GetBytes(command);

                    webRequest.Method = "POST";
                    webRequest.ContentType = "application/x-www-form-urlencoded";
                    webRequest.ContentLength = data.Length;

                    var username = "user";
                    var password = "123456";
                    string encoded = System.Convert.ToBase64String(Encoding.GetEncoding("ISO-8859-1").GetBytes(username + ":" + password));
                    webRequest.Headers.Add("Authorization", "Basic " + encoded);


                    using (var stream = webRequest.GetRequestStream())
                    {
                        stream.Write(data, 0, data.Length);
                    }


                    var webresponse = (HttpWebResponse)webRequest.GetResponse();

                    submitResponse = new StreamReader(webresponse.GetResponseStream()).ReadToEnd();

                    webresponse.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("BlockScanner.rpcExec error: " + ex.Message);
                submitResponse = "Error: " + ex.Message;
            }


            return submitResponse;
        }


        public string FromHexString(string hexString)
        {
            var bytes = new byte[hexString.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);
            }

            return Encoding.UTF8.GetString(bytes);
        }




    }
}
