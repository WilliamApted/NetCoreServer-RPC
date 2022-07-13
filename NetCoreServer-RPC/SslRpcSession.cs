using MessagePack;
using Microsoft.Extensions.Logging;
using NetCoreServer;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetCoreServer_RPC
{
    public class SslRpcSession<T> : SslSession where T : IMessageBase, new()
    {
        private object _sendLock = new object();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<T>> _rpcResponses = new ConcurrentDictionary<Guid, TaskCompletionSource<T>>();
        private ILogger _logger;
        private int _defaultRpcTimeout = 5000;

        public SslRpcSession(SslServer server, ILogger logger) : base(server)
        {
            _logger = logger;
        }

        public async Task<T> SendRpc(T message)
        {
            message.CorrelationId = Guid.NewGuid();

            var commandResponse = new TaskCompletionSource<T>();
            _rpcResponses.TryAdd(message.CorrelationId.Value, commandResponse);

            Send(message);

            try
            {
                using (var timeoutCancellationTokenSource = new CancellationTokenSource())
                {
                    var rpcTask = commandResponse.Task;

                    var completedTask = await Task.WhenAny(rpcTask, Task.Delay(_defaultRpcTimeout, timeoutCancellationTokenSource.Token));

                    if (completedTask == rpcTask)
                    {
                        timeoutCancellationTokenSource.Cancel();
                        var result = await rpcTask;

                        return result;
                    }
                    else
                    {
                        return new T { Status = (int)StatusCodes.RequestTimeout };
                    }
                }
            }
            finally 
            {
                var removed = _rpcResponses.TryRemove(message.CorrelationId.Value, out var completionSource);
                completionSource.TrySetCanceled();
            }
        }

        public void Send(T message)
        {
            lock (_sendLock)
            {
                Send(MessagePackSerializer.Serialize<T>(message));
            }
        }

        protected override void OnConnected()
        {
            _logger.LogInformation($"Client SSL session with Id {base.Id} connected!");
        }

        protected override void OnHandshaked()
        {
            _logger.LogInformation($"Client SSL session with Id {base.Id} handshaked!");
        }

        protected override void OnDisconnected()
        {
            _logger.LogInformation($"Client SSL session with Id {base.Id} disconnected!");
        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            try
            {
                T receivedMessage = MessagePackSerializer.Deserialize<T>(buffer);

                if (BeforeReceiveActioned(receivedMessage) is false) { _logger.LogWarning($"BeforeReceiveAction determined not to countinue with processing message."); return; };

                if (receivedMessage.CorrelationId.HasValue && receivedMessage.Status == 0)
                {
                    // Process RPC Call
                    T result = (T)receivedMessage.ActionRpc(this);
                    result.CorrelationId = receivedMessage.CorrelationId;
                    Send(result);
                }
                else if (receivedMessage.Status != 0)
                {
                    // Process RPC Response
                    _rpcResponses.TryGetValue(receivedMessage.CorrelationId.Value, out var taskCompletionSource);
                    var resultSet = taskCompletionSource?.TrySetResult(receivedMessage);
                    if (!resultSet.HasValue || resultSet.Value == false)
                        _logger.LogWarning("Failed to set rpc result.");
                }
                else
                {
                    // Process Standard Message
                    receivedMessage.Action(this);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error when receiving data: {e.Message}");
            }
        }

        protected virtual bool BeforeReceiveActioned(T message)
        {
            return true;
        }

        protected override void OnError(SocketError error)
        {
            _logger.LogError($"Client SSL session caught an error with code {error}");
        }
    }
}
