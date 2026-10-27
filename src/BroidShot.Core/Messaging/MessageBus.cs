using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace BroidShot.Core;

/// <summary>
/// In-process typed publish/subscribe. A subscriber to type T receives messages of T and of types
/// derived from T. Message types shared between modules live in BroidShot.Core (see CommonMessages.cs).
/// </summary>
public interface IMessageBus
{
    /// <summary>Handlers run on the UI thread. Dispose the result to unsubscribe.</summary>
    IDisposable Subscribe<TMessage>(Action<TMessage> handler) where TMessage : class;

    /// <summary>Delivers to current subscribers. Safe from any thread; never throws because of a handler.</summary>
    void Publish<TMessage>(TMessage message) where TMessage : class;
}

public sealed class MessageBus : IMessageBus
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();
    private readonly Dispatcher? _dispatcher;
    private readonly Action<Exception>? _onHandlerError;

    /// <param name="dispatcher">Handlers are marshalled to this dispatcher; null delivers synchronously on the publisher thread.</param>
    /// <param name="onHandlerError">Receives handler exceptions (default: errors.log).</param>
    public MessageBus(Dispatcher? dispatcher = null, Action<Exception>? onHandlerError = null)
    {
        _dispatcher = dispatcher;
        _onHandlerError = onHandlerError;
    }

    public IDisposable Subscribe<TMessage>(Action<TMessage> handler) where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, typeof(TMessage), message => handler((TMessage)message));
        lock (_gate) _subscriptions.Add(subscription);
        return subscription;
    }

    public void Publish<TMessage>(TMessage message) where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);
        Type type = message.GetType();
        Subscription[] targets;
        lock (_gate) targets = _subscriptions.Where(s => s.MessageType.IsAssignableFrom(type)).ToArray();
        if (targets.Length == 0) return;
        if (_dispatcher is null || _dispatcher.CheckAccess()) Deliver(targets, message);
        else if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(new Action(() => Deliver(targets, message)));
    }

    public int SubscriberCount { get { lock (_gate) return _subscriptions.Count; } }

    private void Deliver(Subscription[] targets, object message)
    {
        foreach (Subscription subscription in targets)
        {
            if (subscription.IsDisposed) continue;
            try { subscription.Handler(message); }
            catch (Exception ex)
            {
                if (_onHandlerError is not null) _onHandlerError(ex);
                else ErrorLog.Write("messages", "Mesaj işleyicisi başarısız: " + message.GetType().Name, ex);
            }
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate) _subscriptions.Remove(subscription);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly MessageBus _owner;

        public Subscription(MessageBus owner, Type messageType, Action<object> handler)
        {
            _owner = owner;
            MessageType = messageType;
            Handler = handler;
        }

        public Type MessageType { get; }
        public Action<object> Handler { get; }
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            _owner.Remove(this);
        }
    }
}
