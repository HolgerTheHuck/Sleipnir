using System;

namespace TrameTests.Fixtures
{
    /// <summary>
    /// Minimal <see cref="IObservable{T}"/> für Tests ohne System.Reactive-Abhängigkeit.
    /// Die Subscribe-Funktion erhält den <see cref="IObserver{T}"/> und gibt einen
    /// Dispose-Callback zurück. Nutzt die BCL <c>System.IObserver&lt;T&gt;</c>.
    /// </summary>
    internal sealed class SimpleObservable<T> : IObservable<T>
    {
        private readonly Func<IObserver<T>, Action> _subscribe;

        public SimpleObservable(Func<IObserver<T>, Action> subscribe) => _subscribe = subscribe;

        public IDisposable Subscribe(IObserver<T> observer)
        {
            var dispose = _subscribe(observer);
            return new DisposableAction(dispose);
        }

        private sealed class DisposableAction : IDisposable
        {
            private readonly Action _dispose;
            public DisposableAction(Action dispose) => _dispose = dispose;
            public void Dispose() => _dispose();
        }
    }
}