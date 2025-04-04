using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions;

internal sealed class TransactionCommitterAttributeMapper : IAttributeToFactoryMapper<TransactionCommitterAttribute>
{
    private static readonly MethodInfo CreateMethodInfo = typeof(ITransactionCommitterFactory).GetMethod("Create");

    public Factory<IGrainContext, object> GetFactory(ParameterInfo parameter, TransactionCommitterAttribute attribute)
    {
        // use generic type args to define collection type.
        var genericCreate = CreateMethodInfo.MakeGenericMethod(parameter.ParameterType.GetGenericArguments());
        return context => Create(context, genericCreate, [attribute]);
    }

    private static object Create(IGrainContext context, MethodInfo genericCreate, object[] args)
    {
        ITransactionCommitterFactory factory = context.ActivationServices.GetRequiredService<ITransactionCommitterFactory>();
        return genericCreate.Invoke(factory, args);
    }
}
