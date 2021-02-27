﻿using Orleans.Storage;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;

namespace Orleans.TestingHost
{
    [Serializable]
    [Hagar.GenerateSerializer]
    public class RandomlyInjectedStorageException : Exception
    {
        public RandomlyInjectedStorageException() : base("injected fault") { }

        protected RandomlyInjectedStorageException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    [Serializable]
    [Hagar.GenerateSerializer]
    public class RandomlyInjectedInconsistentStateException : InconsistentStateException
    {
        public RandomlyInjectedInconsistentStateException() : base("injected fault") { }

        protected RandomlyInjectedInconsistentStateException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
