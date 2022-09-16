// See https://aka.ms/new-console-template for more information
using LevelDB;
using staker;

Console.WriteLine("Hello, World!");

BlockScanner scanner = new BlockScanner();
Thread t1 = new Thread(new ThreadStart(scanner.run));
t1.Start();

WebServer server = new WebServer();
server.scanner = scanner;
Thread t2 = new Thread(new ThreadStart(server.run));
t2.Start();