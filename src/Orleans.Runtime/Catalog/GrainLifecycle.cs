// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class GrainLifecycle(ILogger logger) : LifecycleSubject(logger), IGrainLifecycle
    {
        private static readonly ImmutableDictionary<int, string> StageNames = GetStageNames(typeof(GrainLifecycleStage));
        private List<IGrainMigrationParticipant> _migrationParticipants;

        public IEnumerable<IGrainMigrationParticipant> GetMigrationParticipants() => _migrationParticipants ?? (IEnumerable<IGrainMigrationParticipant>)[];

        public void AddMigrationParticipant(IGrainMigrationParticipant participant)
        {
            lock (this)
            {
                _migrationParticipants ??= [];
                _migrationParticipants.Add(participant);
            }
        }

        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant)
        {
            lock (this)
            {
                if (_migrationParticipants is null) return;
                _migrationParticipants.Remove(participant);
            }
        }

        protected override string GetStageName(int stage)
        {
            if (StageNames.TryGetValue(stage, out var result)) return result;
            return base.GetStageName(stage);
        }
    }
}
