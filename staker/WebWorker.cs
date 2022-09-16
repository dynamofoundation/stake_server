using Newtonsoft.Json;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Linq;
using LevelDB;

namespace staker
{

    public class WebWorker
    {

        public HttpListenerContext context;
        static readonly HttpClient client = new HttpClient();

        static HttpWebRequest webRequest;

        public BlockScanner scanner;


        public void run()
        {
            try
            {
                HttpListenerRequest request = context.Request;

                StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding);
                string text = reader.ReadToEnd();

                string[] path = request.RawUrl.Substring(1).Split("/");

                //Log.log(request.RawUrl);

                string result = "";


                if (path[0].StartsWith("get_balance"))
                {
                    ReadOptions o = new ReadOptions { };

                    lock (scanner.balances)
                    {
                        Iterator i1 = scanner.balances.db.CreateIterator(o);

                        result = "[";
                        for (i1.SeekToFirst(); i1.IsValid(); i1.Next())
                            result += i1.ValueAsString() + ",\r\n";

                        result = result.Substring(0, result.Length - 3);
                        result += "]";
                    }
                }

                HttpListenerResponse response = context.Response;


                System.IO.Stream output = response.OutputStream;
                byte[] binaryData = Encoding.ASCII.GetBytes(result);
                output.Write(binaryData, 0, binaryData.Length);
                output.Close();

                uint sum = 0;
                for (int i = 0; i < binaryData.Length; i++)
                    sum += binaryData[i];

                //Global.UpdateRand(sum);
            }
            catch (Exception e)
            {
                //Log.log("Error in worker thread:" + e.Message);
                //Log.log(e.StackTrace);

            }
        }
    }
}