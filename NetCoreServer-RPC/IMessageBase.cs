using System;
using System.Collections.Generic;
using System.Text;

namespace NetCoreServer_RPC
{
    public interface IMessageBase
    {
        public Guid? CorrelationId { get; set; }
        public int Status { get; set; }
        public void Action<T>(T session);
        public object ActionRpc<T>(T session);

        public bool IsSuccess()
        {
            return Status >= 200 && Status < 300;
        }

        public bool IsRedirect()
        {
            return Status >= 300 && Status < 400;
        }

        public bool IsClientError()
        {
            return Status >= 400 && Status < 500;
        }

        public bool IsServerError()
        {
            return Status >= 500 && Status < 600;
        }
    }
}
