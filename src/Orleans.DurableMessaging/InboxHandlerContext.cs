using System;

namespace Orleans.DurableMessaging;

internal sealed class InboxHandlerContext(DurableEnvelope envelope, Action complete, Action<string> deadLetter) : IInboxHandlerContext
{
    private readonly Action _complete = complete ?? throw new ArgumentNullException(nameof(complete));
    private readonly Action<string> _deadLetter = deadLetter ?? throw new ArgumentNullException(nameof(deadLetter));

    public DurableEnvelope Envelope { get; } = envelope;

    public void Complete() => _complete();

    public void DeadLetter(string reason) => _deadLetter(reason);
}
