using LevelDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace staker
{
    public class Database
    {
        string dbName;
        public DB db;
        Dictionary<uint,SnapShot> history;
        public const int maxHistory = 20;

        public Database(string name)
        {
            Options options = new Options { CreateIfMissing = true };
            db = new DB(options, name);
            dbName = name;
            history = new Dictionary<uint, SnapShot>();
        }

        public void NewBlock(uint blockHeight)
        {
            history.Add(blockHeight, db.CreateSnapshot());
            if (history.Count > maxHistory)
            {
                uint min = 999999999;
                foreach (uint height in history.Keys)
                    if (height < min)
                        min = height;

                history[min].Dispose();
                history.Remove(min);
            }

        }

        public void Rollback ( uint blockHeight )
        {

            if (history.ContainsKey(blockHeight)) {
                ReadOptions o = new ReadOptions { Snapshot = history[blockHeight] };

                Iterator i1 = db.CreateIterator(o);

                Options options = new Options { CreateIfMissing = true };
                DB rollback = new DB(options, "_rollback");

                for (i1.SeekToFirst(); i1.IsValid(); i1.Next())
                    rollback.Put(i1.KeyAsString(), i1.ValueAsString());

                i1.Dispose();
                history[blockHeight].Dispose();
                db.Close();
                db.Dispose();
                rollback.Close();

                Options o1 = new Options { };
                DB.Destroy(o1, dbName);

                Directory.Move("_rollback", dbName);

                db = new DB(options, dbName);

                history.Clear();

            }
            else
            {
                //todo - exit application - fork deeper than history kept
            }


        }
    }
}
