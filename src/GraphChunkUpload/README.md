# MS Graph Chunk Uploader
Optimize to zero-allcation upload:
[Pull Request](https://github.com/microsoftgraph/msgraph-sdk-dotnet/pull/529)

> ### Changes proposed in this pull request
> -  Avoid extra memory allocation when doing chunked upload
> ChunkedUploadProvider trys to help with large file uploading, it divides the large stream to chunk-size stream and upload each one by one. The current implementation allocate maxChunksize of byte[] buffer each time:
> ```c#
> var readBuffer = new byte[this.maxChunkSize]; // extra allocation
> ```
> and an extra stream copy:
>
> ```c#
>    //extra copy 
>    await this.uploadStream.ReadAsync(readBuffer, 0, request.RangeLength).ConfigureAwait(false);
>
>       while (true)
>       {
>            using (var requestBodyStream = new MemoryStream(request.RangeLength))
>             {
>                 await requestBodyStream.WriteAsync(readBuffer, 0, request.RangeLength).ConfigureAwait(false);
>                 requestBodyStream.Seek(0, SeekOrigin.Begin);
>                 ...
>             }
>       }
> ```
> If there is a total 500MiB size file, open a FileStream with a 10MiB max chunk size, it will allocate a 10 * 1024 * 1024 size MemoryStream 50 times (extra 500MiB memory allocation), and do 100 times buffer operations (50 read and 50 write).
>
>Because the upload stream supports seek and read operation, this pull request provide a ReadOnlySubStream to represent a chunk of a upload stream to save memory when dealing with large file and remove the extra copy operations