// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestGrainInterfaces;

namespace TestGrains
{
    [Serializable]
    [Orleans.GenerateSerializer]
    public class PersonState
    {
        [Orleans.Id(0)]
        public string FirstName { get; set; }
        [Orleans.Id(1)]
        public string LastName { get; set; }
        [Orleans.Id(2)]
        public GenderType Gender { get; set; }
        [Orleans.Id(3)]
        public bool IsMarried { get; set; }

        public void Apply(PersonRegistered @event)
        {
            this.FirstName = @event.FirstName;
            this.LastName = @event.LastName;
            this.Gender = @event.Gender;
        }

        public void Apply(PersonMarried @event)
        {
            this.IsMarried = true;
        }

        public void Apply(PersonLastNameChanged @event)
        {
            this.LastName = @event.LastName;
        }
    }
}
