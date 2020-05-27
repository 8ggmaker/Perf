// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Common.ZipStream
{
   // The disposable fields that this class owns get disposed when the ReadOnlyZipArchive it belongs to gets disposed
   [SuppressMessage( "Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable" )]
   public partial class ReadOnlyZipArchiveEntry
   {
      private const ushort DefaultVersionToExtract = 10;

      // The maximum index of our buffers, from the maximum index of a byte array
      private const int MaxSingleBufferSize = 0x7FFFFFC7;

      private ReadOnlyZipArchive _archive;
      private readonly bool _originallyInArchive;
      private readonly int _diskNumberStart;
      private readonly ZipVersionMadeByPlatform _versionMadeByPlatform;
      private ZipVersionNeededValues _versionMadeBySpecification;
      private ZipVersionNeededValues _versionToExtract;
      private BitFlagValues _generalPurposeBitFlag;
      private CompressionMethodValues _storedCompressionMethod;
      private DateTimeOffset _lastModified;
      private long _compressedSize;
      private long _uncompressedSize;
      private long _offsetOfLocalHeader;
      private long? _storedOffsetOfCompressedData;
      private uint _crc32;
      // An array of buffers, each a maximum of MaxSingleBufferSize in size
      private byte[][] _compressedBytes;
      private MemoryStream _storedUncompressedData;
      private bool _currentlyOpenForWrite;
      private bool _everOpenedForWrite;
      private Stream _outstandingWriteStream;
      private uint _externalFileAttr;
      private string _storedEntryName;
      private byte[] _storedEntryNameBytes;
      // only apply to update mode
      private List<ZipGenericExtraField> _cdUnknownExtraFields;
      private List<ZipGenericExtraField> _lhUnknownExtraFields;
      private byte[] _fileComment;
      private CompressionLevel? _compressionLevel;

      // Initializes, attaches it to archive
      internal ReadOnlyZipArchiveEntry( ReadOnlyZipArchive archive, ZipCentralDirectoryFileHeader cd )
      {
         _archive = archive;

         _originallyInArchive = true;

         _diskNumberStart = cd.DiskNumberStart;
         _versionMadeByPlatform = ( ZipVersionMadeByPlatform )cd.VersionMadeByCompatibility;
         _versionMadeBySpecification = ( ZipVersionNeededValues )cd.VersionMadeBySpecification;
         _versionToExtract = ( ZipVersionNeededValues )cd.VersionNeededToExtract;
         _generalPurposeBitFlag = ( BitFlagValues )cd.GeneralPurposeBitFlag;
         CompressionMethod = ( CompressionMethodValues )cd.CompressionMethod;
         _lastModified = new DateTimeOffset( ZipHelper.DosTimeToDateTime( cd.LastModified ) );
         _compressedSize = cd.CompressedSize;
         _uncompressedSize = cd.UncompressedSize;
         _externalFileAttr = cd.ExternalFileAttributes;
         _offsetOfLocalHeader = cd.RelativeOffsetOfLocalHeader;
         // we don't know this yet: should be _offsetOfLocalHeader + 30 + _storedEntryNameBytes.Length + extrafieldlength
         // but entryname/extra length could be different in LH
         _storedOffsetOfCompressedData = null;
         _crc32 = cd.Crc32;

         _compressedBytes = null;
         _storedUncompressedData = null;
         _currentlyOpenForWrite = false;
         _everOpenedForWrite = false;
         _outstandingWriteStream = null;

         FullName = DecodeEntryName( cd.Filename );

         _lhUnknownExtraFields = null;
         // the cd should have these as null if we aren't in Update mode
         _cdUnknownExtraFields = cd.ExtraFields;
         _fileComment = cd.FileComment;

         _compressionLevel = null;
      }

      // Initializes new entry
      internal ReadOnlyZipArchiveEntry( ReadOnlyZipArchive archive, string entryName, CompressionLevel compressionLevel )
          : this( archive, entryName )
      {
         _compressionLevel = compressionLevel;
      }

      // Initializes new entry
      internal ReadOnlyZipArchiveEntry( ReadOnlyZipArchive archive, string entryName )
      {
         _archive = archive;

         _originallyInArchive = false;

         _diskNumberStart = 0;
         _versionMadeByPlatform = CurrentZipPlatform;
         _versionMadeBySpecification = ZipVersionNeededValues.Default;
         _versionToExtract = ZipVersionNeededValues.Default; // this must happen before following two assignment
         _generalPurposeBitFlag = 0;
         CompressionMethod = CompressionMethodValues.Deflate;
         _lastModified = DateTimeOffset.Now;

         _compressedSize = 0; // we don't know these yet
         _uncompressedSize = 0;
         _externalFileAttr = 0;
         _offsetOfLocalHeader = 0;
         _storedOffsetOfCompressedData = null;
         _crc32 = 0;

         _compressedBytes = null;
         _storedUncompressedData = null;
         _currentlyOpenForWrite = false;
         _everOpenedForWrite = false;
         _outstandingWriteStream = null;

         FullName = entryName;

         _cdUnknownExtraFields = null;
         _lhUnknownExtraFields = null;
         _fileComment = null;

         _compressionLevel = null;

         if( _storedEntryNameBytes.Length > ushort.MaxValue )
            throw new ArgumentException( "entry names too long" );


      }

      /// <summary>
      /// The ReadOnlyZipArchive that this entry belongs to. If this entry has been deleted, this will return null.
      /// </summary>
      public ReadOnlyZipArchive Archive => _archive;

      public uint Crc32 => _crc32;

      /// <summary>
      /// The compressed size of the entry. If the archive that the entry belongs to is in Create mode, attempts to get this property will always throw an exception. If the archive that the entry belongs to is in update mode, this property will only be valid if the entry has not been opened.
      /// </summary>
      /// <exception cref="InvalidOperationException">This property is not available because the entry has been written to or modified.</exception>
      public long CompressedLength
      {
         get
         {
            if( _everOpenedForWrite )
               throw new InvalidOperationException( "length after write" );
            return _compressedSize;
         }
      }

      public int ExternalAttributes
      {
         get
         {
            return ( int )_externalFileAttr;
         }
         set
         {
            ThrowIfInvalidArchive();
            _externalFileAttr = ( uint )value;
         }
      }

      /// <summary>
      /// The relative path of the entry as stored in the Zip archive. Note that Zip archives allow any string to be the path of the entry, including invalid and absolute paths.
      /// </summary>
      public string FullName
      {
         get
         {
            return _storedEntryName;
         }

         private set
         {
            if( value == null )
               throw new ArgumentNullException( nameof( FullName ) );

            bool isUTF8;
            _storedEntryNameBytes = EncodeEntryName( value, out isUTF8 );
            _storedEntryName = value;

            if( isUTF8 )
               _generalPurposeBitFlag |= BitFlagValues.UnicodeFileName;
            else
               _generalPurposeBitFlag &= ~BitFlagValues.UnicodeFileName;

            if( ParseFileName( value, _versionMadeByPlatform ) == "" )
               VersionToExtractAtLeast( ZipVersionNeededValues.ExplicitDirectory );
         }
      }

      /// <summary>
      /// The last write time of the entry as stored in the Zip archive. When setting this property, the DateTime will be converted to the
      /// Zip timestamp format, which supports a resolution of two seconds. If the data in the last write time field is not a valid Zip timestamp,
      /// an indicator value of 1980 January 1 at midnight will be returned.
      /// </summary>
      /// <exception cref="NotSupportedException">An attempt to set this property was made, but the ReadOnlyZipArchive that this entry belongs to was
      /// opened in read-only mode.</exception>
      /// <exception cref="ArgumentOutOfRangeException">An attempt was made to set this property to a value that cannot be represented in the
      /// Zip timestamp format. The earliest date/time that can be represented is 1980 January 1 0:00:00 (midnight), and the last date/time
      /// that can be represented is 2107 December 31 23:59:58 (one second before midnight).</exception>

      /// <summary>
      /// The uncompressed size of the entry. This property is not valid in Create mode, and it is only valid in Update mode if the entry has not been opened.
      /// </summary>
      /// <exception cref="InvalidOperationException">This property is not available because the entry has been written to or modified.</exception>
      public long Length
      {
         get
         {
            if( _everOpenedForWrite )
               throw new InvalidOperationException( "length after write" );
            return _uncompressedSize;
         }
      }

      /// <summary>
      /// The filename of the entry. This is equivalent to the substring of Fullname that follows the final directory separator character.
      /// </summary>
      public string Name => ParseFileName( FullName, _versionMadeByPlatform );


      /// <summary>
      /// Opens the entry. If the archive that the entry belongs to was opened in Read mode, the returned stream will be readable, and it may or may not be seekable. If Create mode, the returned stream will be writable and not seekable. If Update mode, the returned stream will be readable, writable, seekable, and support SetLength.
      /// </summary>
      /// <returns>A Stream that represents the contents of the entry.</returns>
      /// <exception cref="IOException">The entry is already currently open for writing. -or- The entry has been deleted from the archive. -or- The archive that this entry belongs to was opened in ZipArchiveMode.Create, and this entry has already been written to once.</exception>
      /// <exception cref="InvalidDataException">The entry is missing from the archive or is corrupt and cannot be read. -or- The entry has been compressed using a compression method that is not supported.</exception>
      /// <exception cref="ObjectDisposedException">The ZipArchive that this entry belongs to has been disposed.</exception>
      public Stream Open()
      {
         ThrowIfInvalidArchive();

         switch( _archive.Mode )
         {
            case ZipArchiveMode.Read:
               return OpenInReadMode( checkOpenable: true );
            case ZipArchiveMode.Create:
            case ZipArchiveMode.Update:
            default:
               throw new InvalidOperationException( "do not support create or update operation" );
         }
      }

      /// <summary>
      /// Returns the FullName of the entry.
      /// </summary>
      /// <returns>FullName of the entry</returns>
      public override string ToString()
      {
         return FullName;
      }

      // Only allow opening ZipArchives with large ReadOnlyZipArchiveEntries in update mode when running in a 64-bit process.
      // This is for compatibility with old behavior that threw an exception for all process bitnesses, because this
      // will not work in a 32-bit process.
      private static readonly bool s_allowLargeReadOnlyZipArchiveEntriesInUpdateMode = IntPtr.Size > 4;

      internal bool EverOpenedForWrite => _everOpenedForWrite;

      private long OffsetOfCompressedData
      {
         get
         {
            if( _storedOffsetOfCompressedData == null )
            {
               _archive.ArchiveStream.Seek( _offsetOfLocalHeader, SeekOrigin.Begin );
               // by calling this, we are using local header _storedEntryNameBytes.Length and extraFieldLength
               // to find start of data, but still using central directory size information
               if( !ZipLocalFileHeader.TrySkipBlock( _archive.ArchiveReader ) )
                  throw new InvalidDataException( "local file header corrupt" );
               _storedOffsetOfCompressedData = _archive.ArchiveStream.Position;
            }
            return _storedOffsetOfCompressedData.Value;
         }
      }

      private CompressionMethodValues CompressionMethod
      {
         get { return _storedCompressionMethod; }
         set
         {
            if( value == CompressionMethodValues.Deflate )
               VersionToExtractAtLeast( ZipVersionNeededValues.Deflate );
            else if( value == CompressionMethodValues.Deflate64 )
               VersionToExtractAtLeast( ZipVersionNeededValues.Deflate64 );
            _storedCompressionMethod = value;
         }
      }

      private string DecodeEntryName( byte[] entryNameBytes )
      {
         Debug.Assert( entryNameBytes != null );

         Encoding readEntryNameEncoding;
         if( ( _generalPurposeBitFlag & BitFlagValues.UnicodeFileName ) == 0 )
         {
            readEntryNameEncoding = _archive == null ?
                Encoding.UTF8 :
                _archive.EntryNameEncoding ?? Encoding.UTF8;
         }
         else
         {
            readEntryNameEncoding = Encoding.UTF8;
         }

         return readEntryNameEncoding.GetString( entryNameBytes );
      }

      private byte[] EncodeEntryName( string entryName, out bool isUTF8 )
      {
         Debug.Assert( entryName != null );

         Encoding writeEntryNameEncoding;
         if( _archive != null && _archive.EntryNameEncoding != null )
            writeEntryNameEncoding = _archive.EntryNameEncoding;
         else
            writeEntryNameEncoding = ZipHelper.RequiresUnicode( entryName ) ? Encoding.UTF8 : Encoding.ASCII;

         isUTF8 = writeEntryNameEncoding.Equals( Encoding.UTF8 );
         return writeEntryNameEncoding.GetBytes( entryName );
      }


      // should only throw an exception in extremely exceptional cases because it is called from dispose
      internal void WriteCentralDirectoryFileHeader()
      {
         // This part is simple, because we should definitely know the sizes by this time
         BinaryWriter writer = new BinaryWriter( _archive.ArchiveStream );

         // _entryname only gets set when we read in or call moveTo. MoveTo does a check, and
         // reading in should not be able to produce an entryname longer than ushort.MaxValue
         Debug.Assert( _storedEntryNameBytes.Length <= ushort.MaxValue );

         // decide if we need the Zip64 extra field:
         Zip64ExtraField zip64ExtraField = new Zip64ExtraField();
         uint compressedSizeTruncated, uncompressedSizeTruncated, offsetOfLocalHeaderTruncated;

         bool zip64Needed = false;

         if( SizesTooLarge()
#if DEBUG_FORCE_ZIP64
                || _archive._forceZip64
#endif
                )
         {
            zip64Needed = true;
            compressedSizeTruncated = ZipHelper.Mask32Bit;
            uncompressedSizeTruncated = ZipHelper.Mask32Bit;

            // If we have one of the sizes, the other must go in there as speced for LH, but not necessarily for CH, but we do it anyways
            zip64ExtraField.CompressedSize = _compressedSize;
            zip64ExtraField.UncompressedSize = _uncompressedSize;
         }
         else
         {
            compressedSizeTruncated = ( uint )_compressedSize;
            uncompressedSizeTruncated = ( uint )_uncompressedSize;
         }


         if( _offsetOfLocalHeader > uint.MaxValue
#if DEBUG_FORCE_ZIP64
                || _archive._forceZip64
#endif
                )
         {
            zip64Needed = true;
            offsetOfLocalHeaderTruncated = ZipHelper.Mask32Bit;

            // If we have one of the sizes, the other must go in there as speced for LH, but not necessarily for CH, but we do it anyways
            zip64ExtraField.LocalHeaderOffset = _offsetOfLocalHeader;
         }
         else
         {
            offsetOfLocalHeaderTruncated = ( uint )_offsetOfLocalHeader;
         }

         if( zip64Needed )
            VersionToExtractAtLeast( ZipVersionNeededValues.Zip64 );

         // determine if we can fit zip64 extra field and original extra fields all in
         int bigExtraFieldLength = ( zip64Needed ? zip64ExtraField.TotalSize : 0 )
                                   + ( _cdUnknownExtraFields != null ? ZipGenericExtraField.TotalSize( _cdUnknownExtraFields ) : 0 );
         ushort extraFieldLength;
         if( bigExtraFieldLength > ushort.MaxValue )
         {
            extraFieldLength = ( ushort )( zip64Needed ? zip64ExtraField.TotalSize : 0 );
            _cdUnknownExtraFields = null;
         }
         else
         {
            extraFieldLength = ( ushort )bigExtraFieldLength;
         }

         writer.Write( ZipCentralDirectoryFileHeader.SignatureConstant );      // Central directory file header signature  (4 bytes)
         writer.Write( ( byte )_versionMadeBySpecification );                    // Version made by Specification (version)  (1 byte)
         writer.Write( ( byte )CurrentZipPlatform );                             // Version made by Compatibility (type)     (1 byte)
         writer.Write( ( ushort )_versionToExtract );                            // Minimum version needed to extract        (2 bytes)
         writer.Write( ( ushort )_generalPurposeBitFlag );                       // General Purpose bit flag                 (2 bytes)
         writer.Write( ( ushort )CompressionMethod );                            // The Compression method                   (2 bytes)
         writer.Write( ZipHelper.DateTimeToDosTime( _lastModified.DateTime ) );  // File last modification time and date     (4 bytes)
         writer.Write( _crc32 );                                               // CRC-32                                   (4 bytes)
         writer.Write( compressedSizeTruncated );                              // Compressed Size                          (4 bytes)
         writer.Write( uncompressedSizeTruncated );                            // Uncompressed Size                        (4 bytes)
         writer.Write( ( ushort )_storedEntryNameBytes.Length );                 // File Name Length                         (2 bytes)
         writer.Write( extraFieldLength );                                     // Extra Field Length                       (2 bytes)

         // This should hold because of how we read it originally in ZipCentralDirectoryFileHeader:
         Debug.Assert( ( _fileComment == null ) || ( _fileComment.Length <= ushort.MaxValue ) );

         writer.Write( _fileComment != null ? ( ushort )_fileComment.Length : ( ushort )0 ); // file comment length
         writer.Write( ( ushort )0 ); // disk number start
         writer.Write( ( ushort )0 ); // internal file attributes
         writer.Write( _externalFileAttr ); // external file attributes
         writer.Write( offsetOfLocalHeaderTruncated ); // offset of local header

         writer.Write( _storedEntryNameBytes );

         // write extra fields
         if( zip64Needed )
            zip64ExtraField.WriteBlock( _archive.ArchiveStream );
         if( _cdUnknownExtraFields != null )
            ZipGenericExtraField.WriteAllBlocks( _cdUnknownExtraFields, _archive.ArchiveStream );

         if( _fileComment != null )
            writer.Write( _fileComment );
      }

      // returns false if fails, will get called on every entry before closing in update mode
      // can throw InvalidDataException
      internal bool LoadLocalHeaderExtraFieldAndCompressedBytesIfNeeded()
      {
         string message;
         // we should have made this exact call in _archive.Init through ThrowIfOpenable
         Debug.Assert( IsOpenable( false, true, out message ) );

         // load local header's extra fields. it will be null if we couldn't read for some reason
         if( _originallyInArchive )
         {
            _archive.ArchiveStream.Seek( _offsetOfLocalHeader, SeekOrigin.Begin );

            _lhUnknownExtraFields = ZipLocalFileHeader.GetExtraFields( _archive.ArchiveReader );
         }

         if( !_everOpenedForWrite && _originallyInArchive )
         {
            // we know that it is openable at this point

            _compressedBytes = new byte[ ( _compressedSize / MaxSingleBufferSize ) + 1 ][];
            for( int i = 0; i < _compressedBytes.Length - 1; i++ )
            {
               _compressedBytes[ i ] = new byte[ MaxSingleBufferSize ];
            }
            _compressedBytes[ _compressedBytes.Length - 1 ] = new byte[ _compressedSize % MaxSingleBufferSize ];

            _archive.ArchiveStream.Seek( OffsetOfCompressedData, SeekOrigin.Begin );

            for( int i = 0; i < _compressedBytes.Length - 1; i++ )
            {
               ZipHelper.ReadBytes( _archive.ArchiveStream, _compressedBytes[ i ], MaxSingleBufferSize );
            }
            ZipHelper.ReadBytes( _archive.ArchiveStream, _compressedBytes[ _compressedBytes.Length - 1 ], ( int )( _compressedSize % MaxSingleBufferSize ) );
         }

         return true;
      }

      internal void ThrowIfNotOpenable( bool needToUncompress, bool needToLoadIntoMemory )
      {
         string message;
         if( !IsOpenable( needToUncompress, needToLoadIntoMemory, out message ) )
            throw new InvalidDataException( message );
      }


      private Stream GetDataDecompressor( Stream compressedStreamToRead )
      {
         Stream uncompressedStream = null;
         switch( CompressionMethod )
         {
            case CompressionMethodValues.Deflate:
               uncompressedStream = new DeflateStream( compressedStreamToRead, CompressionMode.Decompress );
               break;
            case CompressionMethodValues.Deflate64:
               throw new Exception( "do not support large deflate64" );
            case CompressionMethodValues.Stored:
            default:
               // we can assume that only deflate/deflate64/stored are allowed because we assume that
               // IsOpenable is checked before this function is called
               Debug.Assert( CompressionMethod == CompressionMethodValues.Stored );

               uncompressedStream = compressedStreamToRead;
               break;
         }

         return uncompressedStream;
      }

      private Stream OpenInReadMode( bool checkOpenable )
      {
         if( checkOpenable )
            ThrowIfNotOpenable( needToUncompress: true, needToLoadIntoMemory: false );

         Stream compressedStream = null;


         if(_archive.ArchiveStream is MemoryStream ms )
         {
            var buffer = ms.GetBuffer();
            var readOnlySequence = new ReadOnlySequence<byte>( buffer, (int)OffsetOfCompressedData, (int)_compressedSize );
            compressedStream = new ReadOnlySequenceStream( readOnlySequence );
         }
         else
         {
            //old implementation, not thread safe in readmode
            compressedStream = new SubReadStream( _archive.ArchiveStream, OffsetOfCompressedData, _compressedSize );
         }
         //_archive.AddActiveReference();
         return GetDataDecompressor( compressedStream );
      }


      private bool IsOpenable( bool needToUncompress, bool needToLoadIntoMemory, out string message )
      {
         message = null;

         if( _originallyInArchive )
         {
            if( needToUncompress )
            {
               if( CompressionMethod != CompressionMethodValues.Stored &&
                   CompressionMethod != CompressionMethodValues.Deflate &&
                   CompressionMethod != CompressionMethodValues.Deflate64 )
               {
                  switch( CompressionMethod )
                  {
                     case CompressionMethodValues.BZip2:
                     case CompressionMethodValues.LZMA:
                        message = $"Unsupport compression method:{CompressionMethod.ToString()}";
                        break;
                     default:
                        message = $"Unsupport compression method";
                        break;
                  }
                  return false;
               }
            }
            if( _diskNumberStart != _archive.NumberOfThisDisk )
            {
               message = "split spanned";
               return false;
            }
            if( _offsetOfLocalHeader > _archive.ArchiveStream.Length )
            {
               message = "local file header corrupt";
               return false;
            }
            _archive.ArchiveStream.Seek( _offsetOfLocalHeader, SeekOrigin.Begin );
            if( !ZipLocalFileHeader.TrySkipBlock( _archive.ArchiveReader ) )
            {
               message = "local file header corrupt";
               return false;
            }
            // when this property gets called, some duplicated work
            if( OffsetOfCompressedData + _compressedSize > _archive.ArchiveStream.Length )
            {
               message = "local file header corrupt";
               return false;
            }
            // This limitation originally existed because a) it is unreasonable to load > 4GB into memory
            // but also because the stream reading functions make it hard.  This has been updated to handle
            // this scenario in a 64-bit process using multiple buffers, delivered first as an OOB for
            // compatibility.
            if( needToLoadIntoMemory )
            {
               if( _compressedSize > int.MaxValue )
               {
                  if( !s_allowLargeReadOnlyZipArchiveEntriesInUpdateMode )
                  {
                     message = "entry too large";
                     return false;
                  }
               }
            }
         }

         return true;
      }

      private bool SizesTooLarge() => _compressedSize > uint.MaxValue || _uncompressedSize > uint.MaxValue;


      private void VersionToExtractAtLeast( ZipVersionNeededValues value )
      {
         if( _versionToExtract < value )
         {
            _versionToExtract = value;
         }
         if( _versionMadeBySpecification < value )
         {
            _versionMadeBySpecification = value;
         }
      }

      private void ThrowIfInvalidArchive()
      {
         if( _archive == null )
            throw new InvalidOperationException( "deleted entry" );
         _archive.ThrowIfDisposed();
      }

      /// <summary>
      /// Gets the file name of the path based on Windows path separator characters
      /// </summary>
      private static string GetFileName_Windows( string path )
      {
         int length = path.Length;
         for( int i = length; --i >= 0; )
         {
            char ch = path[ i ];
            if( ch == '\\' || ch == '/' || ch == ':' )
               return path.Substring( i + 1 );
         }
         return path;
      }

      /// <summary>
      /// Gets the file name of the path based on Unix path separator characters
      /// </summary>
      private static string GetFileName_Unix( string path )
      {
         int length = path.Length;
         for( int i = length; --i >= 0; )
            if( path[ i ] == '/' )
               return path.Substring( i + 1 );
         return path;
      }

      [Flags]
      private enum BitFlagValues : ushort { DataDescriptor = 0x8, UnicodeFileName = 0x800 }

      internal enum CompressionMethodValues : ushort { Stored = 0x0, Deflate = 0x8, Deflate64 = 0x9, BZip2 = 0xC, LZMA = 0xE }
   }
}
