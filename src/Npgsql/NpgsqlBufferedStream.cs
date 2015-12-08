﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

/*============================================================
**
** 
** 
**
** Purpose: A composable Stream that buffers reads & writes to the underlying stream.
**
**
** Modified by Rikard Pavelic for Revenj/Npgsql
===========================================================*/
using System;
using System.Diagnostics.Contracts;
using System.IO;
using System.Runtime.InteropServices;

namespace Npgsql
{

	/// <summary>
	/// One of the design goals here is to prevent the buffer from getting in the way and slowing
	/// down underlying stream accesses when it is not needed. If you always read & write for sizes
	/// greater than the internal buffer size, then this class may not even allocate the internal buffer.
	/// See a large comment in Write for the details of the write buffer heuristic.
	/// 
	/// This class buffers reads & writes in a shared buffer.
	/// (If you maintained two buffers separately, one operation would always trash the other buffer
	/// anyways, so we might as well use one buffer.) 
	/// The assumption here is you will almost always be doing a series of reads or writes, but rarely
	/// alternate between the two of them on the same stream.
	///
	/// Class Invariants:
	/// The class has one buffer, shared for reading & writing.
	/// It can only be used for one or the other at any point in time - not both.
	/// The following should be true:
	/// <![CDATA[
	///   * 0 <= _readPos <= _readLen < _bufferSize
	///   * 0 <= _writePos < _bufferSize
	///   * _readPos == _readLen && _readPos > 0 implies the read buffer is valid, but we're at the end of the buffer.
	///   * _readPos == _readLen == 0 means the read buffer contains garbage.
	///   * Either _writePos can be greater than 0, or _readLen & _readPos can be greater than zero,
	///     but neither can be greater than zero at the same time.
	///  ]]>
	/// This class will never cache more bytes than the max specified buffer size.
	/// However, it may use a temporary buffer of up to twice the size in order to combine several IO operations on
	/// the underlying stream into a single operation. This is because we assume that memory copies are significantly
	/// faster than IO operations on the underlying stream (if this was not true, using buffering is never appropriate).
	/// The max size of this "shadow" buffer is limited as to not allocate it on the LOH.
	/// Shadowing is always transient. Even when using this technique, this class still guarantees that the number of
	/// bytes cached (not yet written to the target stream or not yet consumed by the user) is never larger than the 
	/// actual specified buffer size.
	/// </summary>
	internal sealed class NpgsqlBufferedStream : Stream
	{

		private const Int32 _DefaultBufferSize = 4096;

		private Stream _stream;                               // Underlying stream.  Close sets _stream to null.

		private Byte[] _buffer;                               // Shared read/write buffer.  Alloc on first use.

		private readonly Int32 _bufferSize;                   // Length of internal buffer (not counting the shadow buffer).

		private Int32 _readPos;                               // Read pointer within shared buffer.
		private Int32 _readLen;                               // Number of bytes read in buffer from _stream.
		private Int32 _writePos;                              // Write pointer within shared buffer.


		// Removing a private default constructor is a breaking change for the DataContractSerializer.
		// Because this ctor was here previously we need to keep it around.
		private NpgsqlBufferedStream() { }


		public NpgsqlBufferedStream(Stream stream)

			: this(stream, _DefaultBufferSize)
		{
		}


		public NpgsqlBufferedStream(Stream stream, Int32 bufferSize)
		{

			if (stream == null)
				throw new ArgumentNullException("stream");

			if (bufferSize <= 0)
				throw new ArgumentOutOfRangeException("bufferSize", string.Format("'{0}' must be greater than zero.", bufferSize));

			Contract.EndContractBlock();

			_stream = stream;
			_bufferSize = bufferSize;

			// Allocate _buffer on its first use - it will not be used if all reads
			// & writes are greater than or equal to buffer size.

			if (!_stream.CanRead && !_stream.CanWrite)
				StreamIsClosed();
		}

		void StreamIsClosed()
		{
			throw new ObjectDisposedException(null, "Cannot access a closed Stream.");
		}

		void SeekNotSupported()
		{
			throw new NotSupportedException("Stream does not support seeking.");
		}

		void ReadNotSupported()
		{
			throw new NotSupportedException("Stream does not support reading.");
		}

		void WriteNotSupported()
		{
			throw new NotSupportedException("Stream does not support writing.");
		}

		public bool IsBufferEmpty { get { return _readPos == _readLen; } }

		private void EnsureNotClosed()
		{

			if (_stream == null)
				StreamIsClosed();
		}


		private void EnsureCanSeek()
		{

			Contract.Requires(_stream != null);

			if (!_stream.CanSeek)
				SeekNotSupported();
		}


		private void EnsureCanRead()
		{

			Contract.Requires(_stream != null);

			if (!_stream.CanRead)
				ReadNotSupported();
		}


		private void EnsureCanWrite()
		{

			Contract.Requires(_stream != null);

			if (!_stream.CanWrite)
				WriteNotSupported();
		}



		/// <summary><code>MaxShadowBufferSize</code> is chosed such that shadow buffers are not allocated on the Large Object Heap.
		/// Currently, an object is allocated on the LOH if it is larger than 85000 bytes. See LARGE_OBJECT_SIZE in ndp\clr\src\vm\gc.h
		/// We will go with exactly 80 KBytes, although this is somewhat arbitrary.</summary>
		private const Int32 MaxShadowBufferSize = 81920;  // Make sure not to get to the Large Object Heap.
		private void EnsureShadowBufferAllocated()
		{

			Contract.Assert(_buffer != null);
			Contract.Assert(_bufferSize > 0);

			// Already have shadow buffer?
			if (_buffer.Length != _bufferSize || _bufferSize >= MaxShadowBufferSize)
				return;

			Byte[] shadowBuffer = new Byte[Math.Min(_bufferSize + _bufferSize, MaxShadowBufferSize)];
			Buffer.BlockCopy(_buffer, 0, shadowBuffer, 0, _writePos);
			_buffer = shadowBuffer;
		}


		private void EnsureBufferAllocated()
		{

			Contract.Assert(_bufferSize > 0);

			// BufferedStream is not intended for multi-threaded use, so no worries about the get/set ---- on _buffer.
			if (_buffer == null)
				_buffer = new Byte[_bufferSize];
		}


		internal Stream UnderlyingStream
		{
			[Pure]
			get { return _stream; }
		}


		internal Int32 BufferSize
		{
			[Pure]
			get { return _bufferSize; }
		}


		public override bool CanRead
		{
			[Pure]
			get { return _stream != null && _stream.CanRead; }
		}


		public override bool CanWrite
		{
			[Pure]
			get { return _stream != null && _stream.CanWrite; }
		}


		public override bool CanSeek
		{
			[Pure]
			get { return _stream != null && _stream.CanSeek; }
		}


		public override Int64 Length
		{
			get
			{
				EnsureNotClosed();

				if (_writePos > 0)
					FlushWrite();

				return _stream.Length;
			}
		}


		public override Int64 Position
		{
			get
			{
				EnsureNotClosed();
				EnsureCanSeek();

				Contract.Assert(!(_writePos > 0 && _readPos != _readLen), "Read and Write buffers cannot both have data in them at the same time.");
				return _stream.Position + (_readPos - _readLen + _writePos);
			}
			set
			{
				if (value < 0)
					throw new ArgumentOutOfRangeException("value", "Non-negative number required.");
				Contract.EndContractBlock();

				EnsureNotClosed();
				EnsureCanSeek();

				if (_writePos > 0)
					FlushWrite();

				_readPos = 0;
				_readLen = 0;
				_stream.Seek(value, SeekOrigin.Begin);
			}
		}


		protected override void Dispose(bool disposing)
		{

			try
			{
				if (disposing && _stream != null)
				{
					try
					{
						Flush();
					}
					finally
					{
						_stream.Close();
					}
				}
			}
			finally
			{
				_stream = null;
				_buffer = null;

				// Call base.Dispose(bool) to cleanup async IO resources
				base.Dispose(disposing);
			}
		}


		public override void Flush()
		{

			EnsureNotClosed();

			// Has WRITE data in the buffer:
			if (_writePos > 0)
			{

				FlushWrite();
				Contract.Assert(_writePos == 0 && _readPos == 0 && _readLen == 0);
				return;
			}

			// Has READ data in the buffer:
			if (_readPos < _readLen)
			{

				// If the underlying stream is not seekable AND we have something in the read buffer, then FlushRead would throw.
				// We can either throw away the buffer resulting in data loss (!) or ignore the Flush.
				// (We cannot throw becasue it would be a breaking change.) We opt into ignoring the Flush in that situation.
				if (!_stream.CanSeek)
					return;

				FlushRead();

				// User streams may have opted to throw from Flush if CanWrite is false (although the abstract Stream does not do so).
				// However, if we do not forward the Flush to the underlying stream, we may have problems when chaining several streams.
				// Let us make a best effort attempt:
				if (_stream.CanWrite || _stream is NpgsqlBufferedStream)
					_stream.Flush();

				Contract.Assert(_writePos == 0 && _readPos == 0 && _readLen == 0);
				return;
			}

			// We had no data in the buffer, but we still need to tell the underlying stream to flush.
			if (_stream.CanWrite || _stream is NpgsqlBufferedStream)
				_stream.Flush();

			_writePos = _readPos = _readLen = 0;
		}

		// Reading is done in blocks, but someone could read 1 byte from the buffer then write. 
		// At that point, the underlying stream's pointer is out of [....] with this stream's position. 
		// All write  functions should call this function to ensure that the buffered data is not lost.
		private void FlushRead()
		{

			Contract.Assert(_writePos == 0, "BufferedStream: Write buffer must be empty in FlushRead!");

			if (_readPos - _readLen != 0)
				_stream.Seek(_readPos - _readLen, SeekOrigin.Current);

			_readPos = 0;
			_readLen = 0;
		}


		private void ClearReadBufferBeforeWrite()
		{

			// This is called by write methods to clear the read buffer.            

			Contract.Assert(_readPos <= _readLen, "_readPos <= _readLen [" + _readPos + " <= " + _readLen + "]");

			// No READ data in the buffer:
			if (_readPos == _readLen)
			{

				_readPos = _readLen = 0;
				return;
			}

			// Must have READ data.
			Contract.Assert(_readPos < _readLen);

			// If the underlying stream cannot seek, FlushRead would end up throwing NotSupported.
			// However, since the user did not call a method that is intuitively expected to seek, a better message is in order.
			// Ideally, we would throw an InvalidOperation here, but for backward compat we have to stick with NotSupported.
			if (!_stream.CanSeek)
				throw new NotSupportedException("Cannot write to a BufferedStream while the read buffer is not empty if the underlying stream is not seekable. Ensure that the stream underlying this BufferedStream can seek or avoid interleaving read and write operations on this BufferedStream.");

			FlushRead();
		}


		private void FlushWrite()
		{

			Contract.Assert(_readPos == 0 && _readLen == 0,
							"BufferedStream: Read buffer must be empty in FlushWrite!");
			Contract.Assert(_buffer != null && _bufferSize >= _writePos,
							"BufferedStream: Write buffer must be allocated and write position must be in the bounds of the buffer in FlushWrite!");

			_stream.Write(_buffer, 0, _writePos);
			_writePos = 0;
			_stream.Flush();
		}

		private Int32 ReadFromBuffer(Byte[] array, Int32 offset, Int32 count)
		{

			Int32 readBytes = _readLen - _readPos;
			Contract.Assert(readBytes >= 0);

			if (readBytes == 0)
				return 0;

			Contract.Assert(readBytes > 0);

			if (readBytes > count)
				readBytes = count;

			Buffer.BlockCopy(_buffer, _readPos, array, offset, readBytes);
			_readPos += readBytes;

			return readBytes;
		}


		private Int32 ReadFromBuffer(Byte[] array, Int32 offset, Int32 count, out Exception error)
		{

			try
			{

				error = null;
				return ReadFromBuffer(array, offset, count);

			}
			catch (Exception ex)
			{
				error = ex;
				return 0;
			}
		}


		public override int Read([In, Out] Byte[] array, Int32 offset, Int32 count)
		{

			if (array == null)
				throw new ArgumentNullException("array", "Buffer cannot be null.");
			if (offset < 0)
				throw new ArgumentOutOfRangeException("offset", "Non-negative number required.");
			if (count < 0)
				throw new ArgumentOutOfRangeException("count", "Non-negative number required.");
			if (array.Length - offset < count)
				throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
			Contract.EndContractBlock();

			EnsureNotClosed();
			EnsureCanRead();

			Int32 bytesFromBuffer = ReadFromBuffer(array, offset, count);

			// We may have read less than the number of bytes the user asked for, but that is part of the Stream contract.

			// Reading again for more data may cause us to block if we're using a device with no clear end of file,
			// such as a serial port or pipe. If we blocked here and this code was used with redirected pipes for a
			// process's standard output, this can lead to deadlocks involving two processes.              
			// BUT - this is a breaking change. 
			// So: If we could not read all bytes the user asked for from the buffer, we will try once from the underlying
			// stream thus ensuring the same blocking behaviour as if the underlying stream was not wrapped in this BufferedStream.
			if (bytesFromBuffer == count)
				return bytesFromBuffer;

			Int32 alreadySatisfied = bytesFromBuffer;
			if (bytesFromBuffer > 0)
			{
				count -= bytesFromBuffer;
				offset += bytesFromBuffer;
			}

			// So the READ buffer is empty.
			Contract.Assert(_readLen == _readPos);
			_readPos = _readLen = 0;

			// If there was anything in the WRITE buffer, clear it.
			if (_writePos > 0)
				FlushWrite();

			// If the requested read is larger than buffer size, avoid the buffer and still use a single read:
			if (count >= _bufferSize)
			{

				return _stream.Read(array, offset, count) + alreadySatisfied;
			}

			// Ok. We can fill the buffer:
			EnsureBufferAllocated();
			_readLen = _stream.Read(_buffer, 0, _bufferSize);

			bytesFromBuffer = ReadFromBuffer(array, offset, count);

			// We may have read less than the number of bytes the user asked for, but that is part of the Stream contract.
			// Reading again for more data may cause us to block if we're using a device with no clear end of stream,
			// such as a serial port or pipe.  If we blocked here & this code was used with redirected pipes for a process's
			// standard output, this can lead to deadlocks involving two processes. Additionally, translating one read on the
			// BufferedStream to more than one read on the underlying Stream may defeat the whole purpose of buffering of the
			// underlying reads are significantly more expensive.

			return bytesFromBuffer + alreadySatisfied;
		}

		public override Int32 ReadByte()
		{

			EnsureNotClosed();
			EnsureCanRead();

			if (_readPos == _readLen)
			{

				if (_writePos > 0)
					FlushWrite();

				EnsureBufferAllocated();
				_readLen = _stream.Read(_buffer, 0, _bufferSize);
				_readPos = 0;
			}

			if (_readPos == _readLen)
				return -1;

			Int32 b = _buffer[_readPos++];
			return b;
		}


		private void WriteToBuffer(Byte[] array, ref Int32 offset, ref Int32 count)
		{

			Int32 bytesToWrite = Math.Min(_bufferSize - _writePos, count);

			if (bytesToWrite <= 0)
				return;

			EnsureBufferAllocated();
			Buffer.BlockCopy(array, offset, _buffer, _writePos, bytesToWrite);

			_writePos += bytesToWrite;
			count -= bytesToWrite;
			offset += bytesToWrite;
		}


		private void WriteToBuffer(Byte[] array, ref Int32 offset, ref Int32 count, out Exception error)
		{

			try
			{

				error = null;
				WriteToBuffer(array, ref offset, ref count);

			}
			catch (Exception ex)
			{
				error = ex;
			}
		}


		public override void Write(Byte[] array, Int32 offset, Int32 count)
		{

			if (array == null)
				throw new ArgumentNullException("array", "Buffer cannot be null.");
			if (offset < 0)
				throw new ArgumentOutOfRangeException("offset", "Non-negative number required.");
			if (count < 0)
				throw new ArgumentOutOfRangeException("count", "Non-negative number required.");
			if (array.Length - offset < count)
				throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
			Contract.EndContractBlock();

			EnsureNotClosed();
			EnsureCanWrite();

			if (_writePos == 0)
				ClearReadBufferBeforeWrite();

			#region Write algorithm comment
			// We need to use the buffer, while avoiding unnecessary buffer usage / memory copies.
			// We ASSUME that memory copies are much cheaper than writes to the underlying stream, so if an extra copy is
			// guaranteed to reduce the number of writes, we prefer it.
			// We pick a simple strategy that makes degenerate cases rare if our assumptions are right.
			//
			// For ever write, we use a simple heuristic (below) to decide whether to use the buffer.
			// The heuristic has the desirable property (*) that if the specified user data can fit into the currently available
			// buffer space without filling it up completely, the heuristic will always tell us to use the buffer. It will also
			// tell us to use the buffer in cases where the current write would fill the buffer, but the remaining data is small
			// enough such that subsequent operations can use the buffer again.
			// 
			// Algorithm:
			// Determine whether or not to buffer according to the heuristic (below).
			// If we decided to use the buffer:
			//     Copy as much user data as we can into the buffer.
			//     If we consumed all data: We are finished.
			//     Otherwise, write the buffer out.
			//     Copy the rest of user data into the now cleared buffer (no need to write out the buffer again as the heuristic
			//     will prevent it from being filled twice).
			// If we decided not to use the buffer:
			//     Can the data already in the buffer and current user data be combines to a single write
			//     by allocating a "shadow" buffer of up to twice the size of _bufferSize (up to a limit to avoid LOH)?
			//     Yes, it can:
			//         Allocate a larger "shadow" buffer and ensure the buffered  data is moved there.
			//         Copy user data to the shadow buffer.
			//         Write shadow buffer to the underlying stream in a single operation.
			//     No, it cannot (amount of data is still too large):
			//         Write out any data possibly in the buffer.
			//         Write out user data directly.
			//
			// Heuristic:
			// If the subsequent write operation that follows the current write operation will result in a write to the
			// underlying stream in case that we use the buffer in the current write, while it would not have if we avoided
			// using the buffer in the current write (by writing current user data to the underlying stream directly), then we
			// prefer to avoid using the buffer since the corresponding memory copy is wasted (it will not reduce the number
			// of writes to the underlying stream, which is what we are optimising for).
			// ASSUME that the next write will be for the same amount of bytes as the current write (most common case) and
			// determine if it will cause a write to the underlying stream. If the next write is actually larger, our heuristic
			// still yields the right behaviour, if the next write is actually smaller, we may making an unnecessary write to
			// the underlying stream. However, this can only occur if the current write is larger than half the buffer size and
			// we will recover after one iteration.
			// We have:
			//     useBuffer = (_writePos + count + count < _bufferSize + _bufferSize)
			//
			// Example with _bufferSize = 20, _writePos = 6, count = 10:
			//
			//     +---------------------------------------+---------------------------------------+
			//     |             current buffer            | next iteration's "future" buffer      |
			//     +---------------------------------------+---------------------------------------+ 
			//     |0| | | | | | | | | |1| | | | | | | | | |2| | | | | | | | | |3| | | | | | | | | |
			//     |0|1|2|3|4|5|6|7|8|9|0|1|2|3|4|5|6|7|8|9|0|1|2|3|4|5|6|7|8|9|0|1|2|3|4|5|6|7|8|9|
			//     +-----------+-------------------+-------------------+---------------------------+
			//     | _writePos |  current count    | assumed next count|avail buff after next write|
			//     +-----------+-------------------+-------------------+---------------------------+
			//
			// A nice property (*) of this heuristic is that it will always succeed if the user data completely fits into the
			// available buffer, i.e. if count < (_bufferSize - _writePos).
			#endregion Write algorithm comment

			Contract.Assert(_writePos < _bufferSize);

			Int32 totalUserBytes;
			bool useBuffer;
			checked
			{  // We do not expect buffer sizes big enough for an overflow, but if it happens, lets fail early:
				totalUserBytes = _writePos + count;
				useBuffer = (totalUserBytes + count < (_bufferSize + _bufferSize));
			}

			if (useBuffer)
			{

				WriteToBuffer(array, ref offset, ref count);

				if (_writePos < _bufferSize)
				{

					Contract.Assert(count == 0);
					return;
				}

				Contract.Assert(count >= 0);
				Contract.Assert(_writePos == _bufferSize);
				Contract.Assert(_buffer != null);

				_stream.Write(_buffer, 0, _writePos);
				_writePos = 0;

				WriteToBuffer(array, ref offset, ref count);

				Contract.Assert(count == 0);
				Contract.Assert(_writePos < _bufferSize);

			}
			else
			{  // if (!useBuffer)

				// Write out the buffer if necessary.
				if (_writePos > 0)
				{

					Contract.Assert(_buffer != null);
					Contract.Assert(totalUserBytes >= _bufferSize);

					// Try avoiding extra write to underlying stream by combining previously buffered data with current user data:
					if (totalUserBytes <= (_bufferSize + _bufferSize) && totalUserBytes <= MaxShadowBufferSize)
					{

						EnsureShadowBufferAllocated();
						Buffer.BlockCopy(array, offset, _buffer, _writePos, count);
						_stream.Write(_buffer, 0, totalUserBytes);
						_writePos = 0;
						return;
					}

					_stream.Write(_buffer, 0, _writePos);
					_writePos = 0;
				}

				// Write out user data.
				_stream.Write(array, offset, count);
			}
		}

		public override void WriteByte(Byte value)
		{

			EnsureNotClosed();

			if (_writePos == 0)
			{

				EnsureCanWrite();
				ClearReadBufferBeforeWrite();
				EnsureBufferAllocated();
			}

			// We should not be flushing here, but only writing to the underlying stream, but previous version flushed, so we keep this.
			if (_writePos >= _bufferSize - 1)
				FlushWrite();

			_buffer[_writePos++] = value;

			Contract.Assert(_writePos < _bufferSize);
		}


		public override Int64 Seek(Int64 offset, SeekOrigin origin)
		{

			EnsureNotClosed();
			EnsureCanSeek();

			// If we have bytes in the WRITE buffer, flush them out, seek and be done.
			if (_writePos > 0)
			{

				// We should be only writing the buffer and not flushing,
				// but the previous version did flush and we stick to it for back-compat reasons.
				FlushWrite();
				return _stream.Seek(offset, origin);
			}

			// The buffer is either empty or we have a buffered READ.

			if (_readLen - _readPos > 0 && origin == SeekOrigin.Current)
			{

				// If we have bytes in the READ buffer, adjust the seek offset to account for the resulting difference
				// between this stream's position and the underlying stream's position.            
				offset -= (_readLen - _readPos);
			}

			Int64 oldPos = Position;
			Contract.Assert(oldPos == _stream.Position + (_readPos - _readLen));

			Int64 newPos = _stream.Seek(offset, origin);

			// If the seek destination is still within the data currently in the buffer, we want to keep the buffer data and continue using it.
			// Otherwise we will throw away the buffer. This can only happen on READ, as we flushed WRITE data above.

			// The offset of the new/updated seek pointer within _buffer:
			_readPos = (Int32)(newPos - (oldPos - _readPos));

			// If the offset of the updated seek pointer in the buffer is still legal, then we can keep using the buffer:
			if (0 <= _readPos && _readPos < _readLen)
			{

				// Adjust the seek pointer of the underlying stream to reflect the amount of useful bytes in the read buffer:
				_stream.Seek(_readLen - _readPos, SeekOrigin.Current);

			}
			else
			{  // The offset of the updated seek pointer is not a legal offset. Loose the buffer.

				_readPos = _readLen = 0;
			}

			Contract.Assert(newPos == Position, "newPos (=" + newPos + ") == Position (=" + Position + ")");
			return newPos;
		}


		public override void SetLength(Int64 value)
		{

			if (value < 0)
				throw new ArgumentOutOfRangeException("value", "Length must be non-negative.");
			Contract.EndContractBlock();

			EnsureNotClosed();
			EnsureCanSeek();
			EnsureCanWrite();

			Flush();
			_stream.SetLength(value);
		}
	}
}
