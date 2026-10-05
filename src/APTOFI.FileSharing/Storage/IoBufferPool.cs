// Date: 2026-08-07
// Time: 09:34:29 Europe/London
// File version: 1.0.0
// Description: Reuses small transfer buffers so large files never occupy significant managed memory.
using System.Collections.Concurrent;

namespace APTOFI.FileSharing.Storage
{
    internal sealed class IoBufferPool
    {
        private readonly ConcurrentBag<byte[]> _buffers = new ConcurrentBag<byte[]>();
        private readonly int _size;

        public IoBufferPool(int size)
        {
            _size = size < 65536 ? 65536 : size;
        }

        public byte[] Rent()
        {
            return _buffers.TryTake(out var buffer) ? buffer : new byte[_size];
        }

        public void Return(byte[] buffer)
        {
            if (buffer != null && buffer.Length == _size)
                _buffers.Add(buffer);
        }
    }
}
