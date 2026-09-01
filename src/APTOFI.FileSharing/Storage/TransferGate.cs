using System;
using System.Threading;
using System.Threading.Tasks;

namespace APTOFI.FileSharing.Storage
{
    internal sealed class TransferGate
    {
        private readonly SemaphoreSlim _semaphore;

        public TransferGate(int maximum)
        {
            _semaphore = new SemaphoreSlim(Math.Max(1, maximum), Math.Max(1, maximum));
        }

        public async Task<IDisposable> EnterAsync()
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            return new Releaser(_semaphore);
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim _owner;

            public Releaser(SemaphoreSlim owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.Release();
            }
        }
    }
}
