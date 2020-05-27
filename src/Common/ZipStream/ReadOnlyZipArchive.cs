// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.


// Zip Spec here: http://www.pkware.com/documents/casestudies/APPNOTE.TXT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace Common.ZipStream
{
    public class ReadOnlyZipArchive : IDisposable
    {
        private Stream _archiveStream;
        private ReadOnlyZipArchiveEntry _archiveStreamOwner;
        private BinaryReader _archiveReader;
        private ZipArchiveMode _mode;
        private List<ReadOnlyZipArchiveEntry> _entries;
        private ReadOnlyCollection<ReadOnlyZipArchiveEntry> _entriesCollection;
        private Dictionary<string, ReadOnlyZipArchiveEntry> _entriesDictionary;
        private bool _readEntries;
        private bool _leaveOpen;
        private long _centralDirectoryStart; //only valid after ReadCentralDirectory
        private volatile bool _isDisposed;
        private uint _numberOfThisDisk; //only valid after ReadCentralDirectory
        private long _expectedNumberOfEntries;
        private Stream _backingStream;
        private byte[] _archiveComment;
        private Encoding _entryNameEncoding;

        // reference by opened ziparchiveentry
        private long _activeReferenceCount = 0;

#if DEBUG_FORCE_ZIP64
        public bool _forceZip64;
#endif

        /// <summary>
        /// Initializes a new instance of ZipArchive on the given stream for reading.
        /// </summary>
        /// <exception cref="ArgumentException">The stream is already closed or does not support reading.</exception>
        /// <exception cref="ArgumentNullException">The stream is null.</exception>
        /// <exception cref="InvalidDataException">The contents of the stream could not be interpreted as a Zip archive.</exception>
        /// <param name="stream">The stream containing the archive to be read.</param>
        public ReadOnlyZipArchive(Stream stream) : this(stream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: null) { }


        public ReadOnlyZipArchive(Stream stream, bool leaveOpen) : this(stream, ZipArchiveMode.Read, leaveOpen: leaveOpen, entryNameEncoding: null) { }

        internal ReadOnlyZipArchive(Stream stream, ZipArchiveMode mode, bool leaveOpen, Encoding entryNameEncoding)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            EntryNameEncoding = entryNameEncoding;
            Init(stream, mode, leaveOpen);
        }

        //internal void AddActiveReference()
        //{
        //    Interlocked.Increment(ref _activeReferenceCount);
        //}

        public bool IsDisposed => _isDisposed;
        /// <summary>
        /// The collection of entries that are currently in the ZipArchive. This may not accurately represent the actual entries that are present in the underlying file or stream.
        /// </summary>
        /// <exception cref="NotSupportedException">The ZipArchive does not support reading.</exception>
        /// <exception cref="ObjectDisposedException">The ZipArchive has already been closed.</exception>
        /// <exception cref="InvalidDataException">The Zip archive is corrupt and the entries cannot be retrieved.</exception>
        public ReadOnlyCollection<ReadOnlyZipArchiveEntry> Entries
        {
            get
            {
                ThrowIfDisposed();

                EnsureCentralDirectoryRead();
                return _entriesCollection;
            }
        }

        /// <summary>
        /// The ZipArchiveMode that the ZipArchive was initialized with.
        /// </summary>
        public ZipArchiveMode Mode
        {
            get
            {
                return _mode;
            }
        }



        /// <summary>
        /// Releases the unmanaged resources used by ZipArchive and optionally finishes writing the archive and releases the managed resources.
        /// </summary>
        /// <param name="disposing">true to finish writing the archive and release unmanaged and managed resources, false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {

            //if (Interlocked.Decrement(ref _activeReferenceCount) <= 0)
            //{
                if (disposing && !_isDisposed)
                {

                    CloseStreams();
                    _isDisposed = true;
                }
            //}
        }

        /// <summary>
        /// Finishes writing the archive and releases all resources used by the ZipArchive object, unless the object was constructed with leaveOpen as true. Any streams from opened entries in the ZipArchive still open will throw exceptions on subsequent writes, as the underlying streams will have been closed.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Retrieves a wrapper for the file entry in the archive with the specified name. Names are compared using ordinal comparison. If there are multiple entries in the archive with the specified name, the first one found will be returned.
        /// </summary>
        /// <exception cref="ArgumentException">entryName is a zero-length string.</exception>
        /// <exception cref="ArgumentNullException">entryName is null.</exception>
        /// <exception cref="NotSupportedException">The ZipArchive does not support reading.</exception>
        /// <exception cref="ObjectDisposedException">The ZipArchive has already been closed.</exception>
        /// <exception cref="InvalidDataException">The Zip archive is corrupt and the entries cannot be retrieved.</exception>
        /// <param name="entryName">A path relative to the root of the archive, identifying the desired entry.</param>
        /// <returns>A wrapper for the file entry in the archive. If no entry in the archive exists with the specified name, null will be returned.</returns>
        public ReadOnlyZipArchiveEntry GetEntry(string entryName)
        {
            if (entryName == null)
                throw new ArgumentNullException(nameof(entryName));

            EnsureCentralDirectoryRead();
            ReadOnlyZipArchiveEntry result;
            _entriesDictionary.TryGetValue(entryName, out result);
            return result;
        }

        internal BinaryReader ArchiveReader => _archiveReader;

        internal Stream ArchiveStream => _archiveStream;

        //internal MultiBufferMemoryStream MultiBufferArchiveStream => (MultiBufferMemoryStream)_archiveStream;

        internal uint NumberOfThisDisk => _numberOfThisDisk;

        internal Encoding EntryNameEncoding
        {
            get { return _entryNameEncoding; }

            private set
            {
                // value == null is fine. This means the user does not want to overwrite default encoding picking logic.

                // The Zip file spec [http://www.pkware.com/documents/casestudies/APPNOTE.TXT] specifies a bit in the entry header
                // (specifically: the language encoding flag (EFS) in the general purpose bit flag of the local file header) that
                // basically says: UTF8 (1) or CP437 (0). But in reality, tools replace CP437 with "something else that is not UTF8".
                // For instance, the Windows Shell Zip tool takes "something else" to mean "the local system codepage".
                // We default to the same behaviour, but we let the user explicitly specify the encoding to use for cases where they
                // understand their use case well enough.
                // Since the definition of acceptable encodings for the "something else" case is in reality by convention, it is not
                // immediately clear, whether non-UTF8 Unicode encodings are acceptable. To determine that we would need to survey
                // what is currently being done in the field, but we do not have the time for it right now.
                // So, we artificially disallow non-UTF8 Unicode encodings for now to make sure we are not creating a compat burden
                // for something other tools do not support. If we realise in future that "something else" should include non-UTF8
                // Unicode encodings, we can remove this restriction.

                if (value != null &&
                        (value.Equals(Encoding.BigEndianUnicode)
                        || value.Equals(Encoding.Unicode)
#if FEATURE_UTF32
                        || value.Equals(Encoding.UTF32)
#endif // FEATURE_UTF32
#if FEATURE_UTF7
                        || value.Equals(Encoding.UTF7)
#endif // FEATURE_UTF7
                        ))
                {
                    throw new ArgumentException("EntryNameEncodingNotSupported", nameof(EntryNameEncoding));
                }

                _entryNameEncoding = value;
            }
        }


        private void AddEntry(ReadOnlyZipArchiveEntry entry)
        {
            _entries.Add(entry);

            string entryName = entry.FullName;
            if (!_entriesDictionary.ContainsKey(entryName))
            {
                _entriesDictionary.Add(entryName, entry);
            }
        }


        internal void ThrowIfDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(GetType().ToString());
        }

        private void CloseStreams()
        {
            if (!_leaveOpen)
            {
                _archiveStream.Dispose();
                _backingStream?.Dispose();
                _archiveReader?.Dispose();
            }
            else
            {
                // if _backingStream isn't null, that means we assigned the original stream they passed
                // us to _backingStream (which they requested we leave open), and _archiveStream was
                // the temporary copy that we needed
                if (_backingStream != null)
                    _archiveStream.Dispose();
            }
        }

        private void EnsureCentralDirectoryRead()
        {
            if (!_readEntries)
            {
                ReadCentralDirectory();
                _readEntries = true;
            }
        }

        private void Init(Stream stream, ZipArchiveMode mode, bool leaveOpen)
        {
            if (mode == ZipArchiveMode.Update || mode == ZipArchiveMode.Create)
            {
                throw new InvalidOperationException();
            }
            Stream extraTempStream = null;

            try
            {
                _backingStream = null;

                // check stream against mode
                switch (mode)
                {
                    case ZipArchiveMode.Read:
                        if (!stream.CanRead)
                            throw new ArgumentException("ReadModeCapabilities");
                        if (!stream.CanSeek)
                        {
                            _backingStream = stream;
                            extraTempStream = stream = new MemoryStream();
                            _backingStream.CopyTo(stream);
                            stream.Seek(0, SeekOrigin.Begin);
                        }
                        break;

                    default:
                        // still have to throw this, because stream constructor doesn't do mode argument checks
                        throw new ArgumentOutOfRangeException(nameof(mode));
                }

                _mode = mode;

                _archiveStream = stream;
                _archiveStreamOwner = null;

                _archiveReader = new BinaryReader(_archiveStream);
                _entries = new List<ReadOnlyZipArchiveEntry>();
                _entriesCollection = new ReadOnlyCollection<ReadOnlyZipArchiveEntry>(_entries);
                _entriesDictionary = new Dictionary<string, ReadOnlyZipArchiveEntry>();
                _readEntries = false;
                _leaveOpen = leaveOpen;
                _centralDirectoryStart = 0; // invalid until ReadCentralDirectory
                _isDisposed = false;
                _numberOfThisDisk = 0; // invalid until ReadCentralDirectory
                _archiveComment = null;

                switch (mode)
                {

                    case ZipArchiveMode.Read:
                        ReadEndOfCentralDirectory();
                        break;

                    default:

                        break;
                }
            }
            catch
            {
                if (extraTempStream != null)
                    extraTempStream.Dispose();

                throw;
            }
        }

        private void ReadCentralDirectory()
        {
            try
            {
                // assume ReadEndOfCentralDirectory has been called and has populated _centralDirectoryStart

                _archiveStream.Seek(_centralDirectoryStart, SeekOrigin.Begin);

                long numberOfEntries = 0;

                //read the central directory
                ZipCentralDirectoryFileHeader currentHeader;
                bool saveExtraFieldsAndComments = Mode == ZipArchiveMode.Update;
                while (ZipCentralDirectoryFileHeader.TryReadBlock(_archiveReader,
                                                        saveExtraFieldsAndComments, out currentHeader))
                {
                    AddEntry(new ReadOnlyZipArchiveEntry(this, currentHeader));
                    numberOfEntries++;
                }

                if (numberOfEntries != _expectedNumberOfEntries)
                    throw new InvalidDataException("NumEntriesWrong");
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("CentralDirectoryInvalid", ex);
            }
        }

        // This function reads all the EOCD stuff it needs to find the offset to the start of the central directory
        // This offset gets put in _centralDirectoryStart and the number of this disk gets put in _numberOfThisDisk
        // Also does some verification that this isn't a split/spanned archive
        // Also checks that offset to CD isn't out of bounds
        private void ReadEndOfCentralDirectory()
        {
            try
            {
                // this seeks to the start of the end of central directory record
                _archiveStream.Seek(-ZipEndOfCentralDirectoryBlock.SizeOfBlockWithoutSignature, SeekOrigin.End);
                if (!ZipHelper.SeekBackwardsToSignature(_archiveStream, ZipEndOfCentralDirectoryBlock.SignatureConstant))
                    throw new InvalidDataException("EOCDNotFound");

                long eocdStart = _archiveStream.Position;

                // read the EOCD
                ZipEndOfCentralDirectoryBlock eocd;
                bool eocdProper = ZipEndOfCentralDirectoryBlock.TryReadBlock(_archiveReader, out eocd);
                Debug.Assert(eocdProper); // we just found this using the signature finder, so it should be okay

                if (eocd.NumberOfThisDisk != eocd.NumberOfTheDiskWithTheStartOfTheCentralDirectory)
                    throw new InvalidDataException("SplitSpanned");

                _numberOfThisDisk = eocd.NumberOfThisDisk;
                _centralDirectoryStart = eocd.OffsetOfStartOfCentralDirectoryWithRespectToTheStartingDiskNumber;
                if (eocd.NumberOfEntriesInTheCentralDirectory != eocd.NumberOfEntriesInTheCentralDirectoryOnThisDisk)
                    throw new InvalidDataException("SplitSpanned");
                _expectedNumberOfEntries = eocd.NumberOfEntriesInTheCentralDirectory;

                // only bother saving the comment if we are in update mode
                if (_mode == ZipArchiveMode.Update)
                    _archiveComment = eocd.ArchiveComment;

                // only bother looking for zip64 EOCD stuff if we suspect it is needed because some value is FFFFFFFFF
                // because these are the only two values we need, we only worry about these
                // if we don't find the zip64 EOCD, we just give up and try to use the original values
                if (eocd.NumberOfThisDisk == ZipHelper.Mask16Bit ||
                    eocd.OffsetOfStartOfCentralDirectoryWithRespectToTheStartingDiskNumber == ZipHelper.Mask32Bit ||
                    eocd.NumberOfEntriesInTheCentralDirectory == ZipHelper.Mask16Bit)
                {
                    // we need to look for zip 64 EOCD stuff
                    // seek to the zip 64 EOCD locator
                    _archiveStream.Seek(eocdStart - Zip64EndOfCentralDirectoryLocator.SizeOfBlockWithoutSignature, SeekOrigin.Begin);
                    // if we don't find it, assume it doesn't exist and use data from normal eocd
                    if (ZipHelper.SeekBackwardsToSignature(_archiveStream, Zip64EndOfCentralDirectoryLocator.SignatureConstant))
                    {
                        // use locator to get to Zip64EOCD
                        Zip64EndOfCentralDirectoryLocator locator;
                        bool zip64eocdLocatorProper = Zip64EndOfCentralDirectoryLocator.TryReadBlock(_archiveReader, out locator);
                        Debug.Assert(zip64eocdLocatorProper); // we just found this using the signature finder, so it should be okay

                        if (locator.OffsetOfZip64EOCD > long.MaxValue)
                            throw new InvalidDataException("FieldTooBigOffsetToZip64EOCD");
                        long zip64EOCDOffset = (long)locator.OffsetOfZip64EOCD;

                        _archiveStream.Seek(zip64EOCDOffset, SeekOrigin.Begin);

                        // read Zip64EOCD
                        Zip64EndOfCentralDirectoryRecord record;
                        if (!Zip64EndOfCentralDirectoryRecord.TryReadBlock(_archiveReader, out record))
                            throw new InvalidDataException("Zip64EOCDNotWhereExpected");

                        _numberOfThisDisk = record.NumberOfThisDisk;

                        if (record.NumberOfEntriesTotal > long.MaxValue)
                            throw new InvalidDataException("FieldTooBigNumEntries");
                        if (record.OffsetOfCentralDirectory > long.MaxValue)
                            throw new InvalidDataException("FieldTooBigOffsetToCD");
                        if (record.NumberOfEntriesTotal != record.NumberOfEntriesOnThisDisk)
                            throw new InvalidDataException("SplitSpanned");

                        _expectedNumberOfEntries = (long)record.NumberOfEntriesTotal;
                        _centralDirectoryStart = (long)record.OffsetOfCentralDirectory;
                    }
                }

                if (_centralDirectoryStart > _archiveStream.Length)
                {
                    throw new InvalidDataException("FieldTooBigOffsetToCD");
                }
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("CDCorrupt", ex);
            }
            catch (IOException ex)
            {
                throw new InvalidDataException("CDCorrupt", ex);
            }
        }

    }
}
