using Common.Utilities;
using Common.ZipStream;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZipStreamWeb.Controllers
{
   public class ZipStreamController : ControllerBase
   {
      private readonly static string containerName = "testzipstream";
      private readonly AzureBlobHelper _blobHelper;
      public ZipStreamController( AzureBlobHelper blobHelper )
      {
         this._blobHelper = blobHelper;
      }

      //for test 
      [HttpGet]
      [Route( "api/zip/upload" )]
      public async ValueTask<IActionResult> DoZipStreamUploadAsync( [FromQuery]bool readOnly = true )
      {
         using( Stream stream = await this.OpenStream() )
         {
            if( readOnly )
            {
               await this.DoZipStreamUploadByReadOnlyAsync( stream );
            }
            else
            {
               await this.DoZipStreamUploadAsync( stream );
            }
         }
         return this.Ok();
      }

      private async ValueTask DoZipStreamUploadByReadOnlyAsync( Stream stream )
      {
         using( ReadOnlyZipArchive readonlyZip = new ReadOnlyZipArchive( stream ) )
         {
            int count = readonlyZip.Entries.Count;
            Task[] tasks = new Task[ readonlyZip.Entries.Count ];
            for( int i = 0; i < count; i++ )
            {
               var entry = readonlyZip.Entries[ i ];
               tasks[ i ] = ( ( Func<Task> )( async () =>
               {
                  using( Stream s = entry.Open() )
                  {
                     string blobName = Guid.NewGuid().ToString();
                     await this._blobHelper.UploadBlobAsync( containerName, blobName, s );
                  }
               } ) )();
            }
            await Task.WhenAll( tasks );
         }
      }

      private async ValueTask DoZipStreamUploadAsync( Stream stream )
      {
         using( ZipArchive zip = new ZipArchive( stream, ZipArchiveMode.Read ) )
         {
            foreach( var entry in zip.Entries )
            {
               using( Stream s = entry.Open() )
               {
                  string blobName = Guid.NewGuid().ToString();
                  await this._blobHelper.UploadBlobAsync( containerName, blobName, s );
               }
            }
         }
      }

      private async ValueTask<Stream> OpenStream()
      {
         using( Stream fs = System.IO.File.Open( "test.zip", FileMode.Open, FileAccess.Read, FileShare.Read ) )
         {
            MemoryStream ms = new MemoryStream( ( int )fs.Length );
            await fs.CopyToAsync( ms );
            ms.Seek( 0, SeekOrigin.Begin );
            return ms;
         }
      }
   }
}
