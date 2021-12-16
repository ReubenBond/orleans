using System;

namespace Orleans.Legacy.Runtime
{
    [Serializable]
    internal class LegacyResponse
    {
        public bool ExceptionFlag { get; private set; }
        public Exception Exception { get; private set; }
        public object Data { get; private set; }

        public LegacyResponse(object data)
        {
            switch (data)
            {
                case Exception exception:
                    Exception = exception;
                    ExceptionFlag = true;
                    break;
                default:
                    Data = data;
                    ExceptionFlag = false;
                    break;
            }
        }

        private LegacyResponse()
        {
        }

        static public LegacyResponse ExceptionResponse(Exception exc)
        {
            return new LegacyResponse
            {
                ExceptionFlag = true,
                Exception = exc
            };
        }

        public override string ToString()
        {
            if (ExceptionFlag)
            {
                return $"Response Exception={Exception}";
            }

            return $"Response Data={Data}";
        }
    }
}
