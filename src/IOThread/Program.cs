using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace IOThread
{
   class Program
   {
      private readonly static HttpClient client = new HttpClient();
      static void Main( string[] args )
      {
         ThreadPool.SetMaxThreads( 2, 2 );
         Console.WriteLine( "Before Request" );
         PrintInfo();

         Task.Run( async () =>
         {
            _ = await client.GetStringAsync( "https://www.microsoft.com" );
            PrintInfo();
            Task.Run( async () =>
            {
               _ = await client.GetStringAsync( "https://www.microsoft.com" );
               PrintInfo();
               Task.Run( async () =>
               {
                  _ = await client.GetStringAsync( "https://www.microsoft.com" );
                  PrintInfo();
               } ).Wait();
            } ).Wait();
            PrintInfo();

         } ).Wait();


         Console.ReadKey();
      }


      static void PrintInfo()
      {
         Console.WriteLine("-------------------------------------------------");
         SynchronizationContext context = SynchronizationContext.Current;
         Console.WriteLine("Current SynchronizationContext is {0}", context?.ToString());
         Console.WriteLine( "Current Thread Id {0}, is ThreadPool Thread {1}", Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.IsThreadPoolThread );
         ThreadPool.GetAvailableThreads( out int workerThreads, out int iocpThreads );
         Console.WriteLine( "Available Worker Thread {0}, IOCP Thread {1}", workerThreads, iocpThreads );
         Console.WriteLine( "-------------------------------------------------" );
      }
   }
}
