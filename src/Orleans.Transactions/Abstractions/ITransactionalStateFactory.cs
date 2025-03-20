// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Transactions.Abstractions
{
    public class TransactionalStateConfiguration : ITransactionalStateConfiguration
    {
        private readonly string name;
        private readonly string storage;
        public TransactionalStateConfiguration(ITransactionalStateConfiguration config, ParticipantId.Role supportedRoles = ParticipantId.Role.Resource | ParticipantId.Role.Manager)
        {
            this.name = config.StateName;
            this.storage = config.StorageName;
            this.SupportedRoles = supportedRoles;
        }
        public string StateName => this.name;

        public string StorageName => this.storage;

        public ParticipantId.Role SupportedRoles { get; }
    }

    public interface ITransactionalStateFactory
    {
        ITransactionalState<TState> Create<TState>(TransactionalStateConfiguration config) where TState : class, new();
    }
}
