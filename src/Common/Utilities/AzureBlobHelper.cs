using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Common.Utilities
{
   public class AzureBlobHelper
   {
      private readonly CloudStorageAccount _storageAccount;
      private readonly CloudBlobClient _blobClient;
      public AzureBlobHelper(string connectionString )
      {
         this._storageAccount = CloudStorageAccount.Parse( connectionString );
         this._blobClient = this._storageAccount.CreateCloudBlobClient();
      }

      public async ValueTask UploadBlobAsync( string containerName, string blobName, Stream stream)
      {
         if( string.IsNullOrWhiteSpace( containerName ) )
         {
            throw new ArgumentNullException(nameof(containerName));
         }
         if( string.IsNullOrWhiteSpace( blobName ) )
         {
            throw new ArgumentNullException(nameof(blobName));
         }

         var container = this._blobClient.GetContainerReference(containerName);
         await container.CreateIfNotExistsAsync();
         var blob = container.GetBlockBlobReference( blobName );
         await blob.UploadFromStreamAsync( stream );
      }
   }
}
