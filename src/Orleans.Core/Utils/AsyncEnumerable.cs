using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Internal;

namespace Orleans.Runtime.Utilities
{
    internal static class AsyncEnumerable
    {
        internal static readonly object InitialValue = new ();
        internal static readonly object DisposedValue = new ();
    }

    internal sealed class AsyncEnumerable<T> : IAsyncEnumerable<T>
    {
        private enum PublishResult
        {
            Success,
            InvalidUpdate,
            Disposed
        }

        private readonly object _updateLock = new ();
        private readonly Func<T, T, bool> _updateValidator;
        private readonly Action<T> _onPublished;
        private Element _current;
        
        public AsyncEnumerable(Func<T, T, bool> updateValidator, T initial, Action<T> onPublished)
        {
            _updateValidator = updateValidator;
            _current = new Element(initial);
            _onPublished = onPublished;
        }

        public bool TryPublish(T value) => TryPublish(new Element(value)) == PublishResult.Success;
        
        public void Publish(T value)
        {
            switch (TryPublish(new Element(value)))
            {
                case PublishResult.Success:
                    return;
                case PublishResult.InvalidUpdate:
                    ThrowInvalidUpdate();
                    break;
                case PublishResult.Disposed:
                    ThrowDisposed();
                    break;
            }
        }

        private PublishResult TryPublish(Element newItem)
        {
            if (_current.IsDisposed) return PublishResult.Disposed;

            lock (_updateLock)
            {
                if (_current.IsDisposed) return PublishResult.Disposed;

                if (_current.IsValid && newItem.IsValid && !_updateValidator(_current.Value, newItem.Value))
                {
                    return PublishResult.InvalidUpdate;
                }

                var curr = _current;
                Interlocked.Exchange(ref _current, newItem);
                if (newItem.IsValid) _onPublished?.Invoke(newItem.Value);
                curr.SetNext(newItem);

                return PublishResult.Success;
            }
        }

        public void Dispose()
        {
            if (_current.IsDisposed) return;

            lock (_updateLock)
            {
                if (_current.IsDisposed) return;

                TryPublish(Element.CreateDisposed());
            }
        }

        private static void ThrowInvalidUpdate() => throw new ArgumentException("The value was not valid");

        private static void ThrowDisposed() => throw new ObjectDisposedException("This instance has been disposed");

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new AsyncEnumerator(_current, cancellationToken);

        private sealed class AsyncEnumerator : IAsyncEnumerator<T>
        {
            private readonly Task _cancellation;
            private Element _current;

            public AsyncEnumerator(Element initial, CancellationToken cancellation)
            {
                if (!initial.IsValid) _current = initial;
                else
                {
                    var result = Element.CreateInitial();
                    result.SetNext(initial);
                    _current = result;
                }

                if (cancellation != default)
                {
                    _cancellation = cancellation.WhenCancelled();
                }
            }

            T IAsyncEnumerator<T>.Current => _current.Value;

            async ValueTask<bool> IAsyncEnumerator<T>.MoveNextAsync()
            {
                Task<Element> next;
                if (_cancellation != default)
                {
                    next = _current.NextAsync();
                    var result = await Task.WhenAny(_cancellation, next);
                    if (ReferenceEquals(result, _cancellation)) return false;
                }
                else
                {
                    next = _current.NextAsync();
                }

                _current = await next;
                return _current.IsValid;
            }

            ValueTask IAsyncDisposable.DisposeAsync() => default;
        }

        private sealed class Element
        {
            private readonly TaskCompletionSource<Element> _next;
            private readonly object _value;

            public Element(T value)
            {
                _value = value;
                _next = new TaskCompletionSource<Element>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public static Element CreateInitial() => new (
                AsyncEnumerable.InitialValue,
                new TaskCompletionSource<Element>(TaskCreationOptions.RunContinuationsAsynchronously));

            public static Element CreateDisposed()
            {
                var tcs = new TaskCompletionSource<Element>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs.SetException(new ObjectDisposedException("This instance has been disposed"));
                return new Element(AsyncEnumerable.DisposedValue, tcs);
            }

            private Element(object value, TaskCompletionSource<Element> next)
            {
                _value = value;
                _next = next;
            }

            public bool IsValid => !IsInitial && !IsDisposed;

            public T Value
            {
                get
                {
                    if (IsInitial) ThrowInvalidInstance();
                    if (IsDisposed) ThrowDisposed();
                    if (_value is T typedValue) return typedValue;
                    return default;
                }
            }

            public bool IsInitial => ReferenceEquals(_value, AsyncEnumerable.InitialValue);
            public bool IsDisposed => ReferenceEquals(_value, AsyncEnumerable.DisposedValue);

            public Task<Element> NextAsync() => _next.Task;

            public void SetNext(Element next) => _next.SetResult(next);

            private static T ThrowInvalidInstance() => throw new InvalidOperationException("This instance does not have a value set.");
        }
    }
}
