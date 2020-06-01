using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IOThreadNET45
{
   class Program
   {
      private readonly static HttpClient client = new HttpClient();
      static void Main( string[] args )
      {
         Console.WriteLine( "Before Request" );
         PrintInfo();

         Task.Run( async () =>
         {
            _ = await client.GetStringAsync( "https://www.microsoft.com" );
            PrintInfo();
         } ).Wait();

         Console.ReadKey();
      }


      static void PrintInfo()
      {
         Console.WriteLine( "-------------------------------------------------" );
         SynchronizationContext context = SynchronizationContext.Current;
         Console.WriteLine( "Current SynchronizationContext is {0}", context?.ToString() );
         Console.WriteLine( "Current Thread Id {0}, is ThreadPool Thread {1}", Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.IsThreadPoolThread );
         ThreadPool.GetAvailableThreads( out int workerThreads, out int iocpThreads );
         Console.WriteLine( "Available Worker Thread {0}, IOCP Thread {1}", workerThreads, iocpThreads );
         Console.WriteLine( "-------------------------------------------------" );
      }
   }
}
