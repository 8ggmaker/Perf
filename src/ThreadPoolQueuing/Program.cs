using System;
using System.Threading;
using System.Threading.Tasks;

namespace ThreadPoolQueuing
{
   class Program
   {
      static void Main( string[] args )
      {
         int coreCount = Environment.ProcessorCount;
         Console.WriteLine(coreCount);

         ThreadPool.SetMinThreads( coreCount, coreCount );

         int sleepTime = (int)(1000 * 1.6 / coreCount);

         Console.WriteLine( sleepTime );

         Task.Factory.StartNew(
             ()=> { Producer( sleepTime ); },
             TaskCreationOptions.None );
         Console.ReadLine();
      }

      static void Producer(int sleepTime)
      {
         while( true )
         {
            // Creating a new task instead of just calling Process
            // Needed to avoid blocking the loop since we removed the Task.Yield
            Process();

            Thread.Sleep( sleepTime );
         }
      }

      static async Task Process()
      {
         //await Task.Yield();

         var tcs = new TaskCompletionSource<bool>();

         Task.Run( () =>
         {
            Thread.Sleep( 1000 );
            tcs.SetResult( true );
         } );

         tcs.Task.Wait();

         Console.WriteLine( "Ended - " + DateTime.Now.ToLongTimeString() );
      }
   }
}