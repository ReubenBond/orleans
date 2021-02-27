﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Serializable]
    [Hagar.GenerateSerializer]
    public class Initialized_State
    {
        [Hagar.Id(0)]
        public List<string> Names { get; set; }
        public Initialized_State()
        {
            Names = new List<string>();
        }
    }

    public class InitialStateGrain : Grain<Initialized_State>, IInitialStateGrain
    {
        public Task<List<string>> GetNames()
        {
            return Task.FromResult(State.Names);
        }

        public Task AddName(string name)
        {
            State.Names.Add(name);
            return WriteStateAsync();
        }
    }
}