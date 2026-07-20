namespace Orleans.Runtime;

internal static class MessageDestinationCache
{
    public static void Update(
        IMessageDestinationCache? cache,
        SiloAddress? expectedSilo,
        Message request,
        Message response)
    {
        if (cache is null)
        {
            return;
        }

        if (TryGetCacheUpdate(request, response, expectedSilo, out var updatedSilo))
        {
            if (cache.CompareExchangeTargetSilo(updatedSilo, expectedSilo))
            {
                ClearMessageReceiver(cache);
            }

            return;
        }

        if (response.Result is Message.ResponseTypes.None
            or Message.ResponseTypes.Success
            or Message.ResponseTypes.Error
            or Message.ResponseTypes.Status)
        {
            if (response.SendingSilo is { } respondingSilo)
            {
                cache.CompareExchangeTargetSilo(respondingSilo, expectedSilo);
            }
        }
        else if (response.Result is Message.ResponseTypes.Rejection
            && cache.CompareExchangeTargetSilo(value: null, expectedSilo))
        {
            ClearMessageReceiver(cache);
        }
    }

    private static bool TryGetCacheUpdate(
        Message request,
        Message response,
        SiloAddress? expectedSilo,
        out SiloAddress? targetSilo)
    {
        if (response.CacheInvalidationHeader is { } updates)
        {
            lock (updates)
            {
                foreach (var update in updates)
                {
                    if (update.GrainId.Equals(request.TargetGrain)
                        && (expectedSilo is null || expectedSilo.Matches(update.InvalidGrainAddress.SiloAddress)))
                    {
                        targetSilo = update.ValidGrainAddress?.SiloAddress;
                        return true;
                    }
                }
            }
        }

        targetSilo = null;
        return false;
    }

    private static void ClearMessageReceiver(IMessageReceiverCache cache)
    {
        while (cache.MessageReceiver is { } receiver
            && !cache.CompareExchangeMessageReceiver(value: null, comparand: receiver))
        {
        }
    }
}
