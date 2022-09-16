using LevelDB;
using System;
using System.Net;
using System.Threading;

namespace staker
{


    public class WebServer
    {

        bool Shutdown = false;
        public BlockScanner scanner;

        public void run()
        {
            try
            {
                if (!HttpListener.IsSupported)
                {
                    //Log.log("HTTP Listener not supported");
                    return;
                }

                HttpListener listener = new HttpListener();

                listener.Prefixes.Add("http://*:6502/");

                listener.Start();
                //Log.log("HTTP Listening on " + Global.WebServerURL());

                while (!Shutdown)
                {
                    HttpListenerContext context = listener.GetContext();
                    WebWorker worker = new WebWorker();
                    worker.context = context;
                    worker.scanner = scanner;
                    Thread t1 = new Thread(new ThreadStart(worker.run));
                    t1.Start();
                }

                listener.Stop();
            }
            catch (Exception ex)
            {
                //Log.log("error in WebServer.run, exiting: " + ex.Message);
            }
        }

    }
}
