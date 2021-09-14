using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orleans.Networking.Shared
{
    internal sealed class SharedMemoryPool
    {
        public ReferenceCountingPinnedMemoryPool Pool { get; } = ReferenceCountingPinnedMemoryPool.Shared;
    }

    internal sealed class ReferenceCountingPinnedMemoryPool : MemoryPool<byte>
    {
        public const int BlockSize = 4096;
        private readonly ConcurrentQueue<MemoryPoolBlock> _ownerPool = new();
        private readonly ConcurrentQueue<SequenceSegment> _segmentPool = new();

        public static new ReferenceCountingPinnedMemoryPool Shared { get; } = new ReferenceCountingPinnedMemoryPool();

        public override int MaxBufferSize => int.MaxValue;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            if (!_ownerPool.TryDequeue(out var owner))
            {
                owner = new MemoryPoolBlock(this);
            }

            owner.RetainInternal();
            return owner;
        }

        public static void Return(ReadOnlySequence<byte> sequence)
        {
            var current = sequence.Start.GetObject();
            while (current is SequenceSegment buffSeg)
            {
                var next = buffSeg.Next;
                buffSeg.Return();
                current = next;
            }
        }

        internal void Return(MemoryPoolBlock memoryOwner) => _ownerPool.Enqueue(memoryOwner);

        internal void Return(SequenceSegment segment) => _segmentPool.Enqueue(segment);

        private SequenceSegment GetSegment()
        {
            if (!_segmentPool.TryDequeue(out var segment))
            {
                segment = new SequenceSegment(this);
            }

            return segment;
        }

        public ReadOnlySequence<byte> RetainOrCopy(ReadOnlySequence<byte> input)
        {
            SequenceSegment first = default;
            SequenceSegment previous = default;
            SequenceSegment current = default;

            var isRetain = false;
            foreach (var segment in input)
            {
                current = GetSegment();
                if (MemoryMarshal.TryGetMemoryManager(segment, out MemoryPoolBlock manager))
                {
                    manager.Retain();
                    current.SetMemory(manager, segment);

                    if (previous is null)
                    {
                        first = current;
                    }
                    else
                    {
                        previous.SetNext(current);
                    }

                    previous = current;
                    isRetain = true;
                }
                else if (isRetain)
                {
                    ThrowInconsistentRetainability();
                }
                else
                {
                    // TODO: Copy
                    ThrowInconsistentRetainability();
                }
            }

            return new ReadOnlySequence<byte>(first, 0, current, current.Length);
        }

        protected override void Dispose(bool disposing)
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInconsistentRetainability() => throw new InvalidOperationException("Inconsistent retainability of provided sequence");

        internal sealed class MemoryPoolBlock : MemoryManager<byte>
        {
            private readonly byte[] _array;
            private int _referenceCount;

            public MemoryPoolBlock(ReferenceCountingPinnedMemoryPool pool)
            {
                Pool = pool;
#if NET5_0_OR_GREATER
                _array = GC.AllocateUninitializedArray<byte>(BlockSize, pinned: true);
#else
                _array = new byte[BlockSize];
#endif
            }

            public ReferenceCountingPinnedMemoryPool Pool { get; }

            public bool IsDisposed => _referenceCount == 0;

            public override Span<byte> GetSpan()
            {
                if (IsDisposed)
                {
                    ThrowObjectDisposedException(nameof(MemoryPoolBlock));
                }

                return _array;
            }

            public override MemoryHandle Pin(int elementIndex = 0)
            {
                unsafe
                {
                    Retain();
                    if ((uint)elementIndex > (uint)Memory.Length)
                    {
                        throw new ArgumentOutOfRangeException(nameof(elementIndex));
                    }

                    var handle = GCHandle.Alloc(_array, GCHandleType.Pinned);
                    void* pointer = Unsafe.Add<byte>((void*)handle.AddrOfPinnedObject(), elementIndex);
                    return new MemoryHandle(pointer, handle, this);
                }
            }

            protected override bool TryGetArray(out ArraySegment<byte> arraySegment)
            {
                if (IsDisposed)
                {
                    ThrowObjectDisposedException(nameof(MemoryPoolBlock));
                }

                arraySegment = new ArraySegment<byte>(_array, 0, _array.Length);
                return true;
            }

            protected override void Dispose(bool disposing)
            {
                Return();
            }

            internal void RetainInternal()
            {
                Interlocked.Increment(ref _referenceCount);
            }

            public void Retain()
            {
                if (IsDisposed)
                {
                    ThrowObjectDisposedException(nameof(MemoryPoolBlock));
                }

                Interlocked.Increment(ref _referenceCount);
            }

            public void Return()
            {
                var refCount = Interlocked.Decrement(ref _referenceCount);
                if (refCount < 0)
                {
                    ThrowInvalidOperationException();
                }

                if (refCount == 0)
                {
                    Pool.Return(this);
                }
            }

            public override void Unpin()
            {
                Return();
            }

            /*
#pragma warning disable CA2015 // Do not define finalizers for types derived from MemoryManager<T>
            ~MemoryPoolBlock()
#pragma warning restore CA2015 // Do not define finalizers for types derived from MemoryManager<T>
            {
                if (!IsDisposed && !Environment.HasShutdownStarted)
                {
                    Debug.Fail($"{nameof(MemoryPoolBlock)} was leaked.");
                }
            }
            */

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void ThrowObjectDisposedException(string objectName) => throw new ObjectDisposedException(objectName);

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void ThrowInvalidOperationException() => throw new InvalidOperationException();

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void ThrowArgumentNullException(string argumentName) => throw new ArgumentNullException(argumentName);
        }

        internal sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
        {
            private readonly ReferenceCountingPinnedMemoryPool _pool;
            private MemoryPoolBlock _memoryOwner;

            public SequenceSegment(ReferenceCountingPinnedMemoryPool pool)
            {
                _pool = pool;
            }

            public void SetMemory(MemoryPoolBlock memoryOwner, ReadOnlyMemory<byte> memory)
            {
                _memoryOwner = memoryOwner;
                Memory = memory;
            }

            public void Return()
            {
                Next = null;
                RunningIndex = 0;
                Memory = default;

                var memoryOwner = _memoryOwner;
                if (memoryOwner != null)
                {
                    _memoryOwner = null;
                    memoryOwner.Return();
                }

                _pool.Return(this);
            }

            public int Length => Memory.Length;

            public void SetNext(SequenceSegment segment)
            {
                Debug.Assert(segment != null);
                Debug.Assert(Next == null);
                Next = segment;
                segment = this;
                while (segment.Next != null)
                {
                    var next = Unsafe.As<SequenceSegment>(segment.Next);
                    next.RunningIndex = segment.RunningIndex + segment.Length;
                    segment = next;
                }
            }
        }
    }
}
