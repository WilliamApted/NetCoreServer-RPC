using MessagePack;
using Microsoft.Extensions.Logging;
using NetCoreServer;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetCoreServer_RPC
{
    public class SslRpcClient<T> : SslClient where T : IMessageBase, new()
    {
        public SslRpcClient(SslContext context, IPAddress address, int port, ILogger logger) : base(context, address, port)
        {
            _logger = logger;
        }

        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<T>> _rpcResponses = new ConcurrentDictionary<Guid, TaskCompletionSource<T>>();

        protected ILogger _logger;
        protected bool _stop;
        protected int _reconnectAttempts;
        private object _sendLock = new object();
        private int _defaultRpcTimeout = 5000;

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

        public void DisconnectAndStop()
        {
            _stop = true;
            DisconnectAsync();
            while (IsConnected)
                Thread.Yield();
        }

        protected override void OnDisconnected()
        {
            _logger.LogWarning($"SSL client disconnected a session with Id {Id}");

            //Try to auto reconnect - say if base server has dropped off - move to another
            Thread.Sleep(1000);

            if (!_stop && _reconnectAttempts > 0)
            {
                _reconnectAttempts--;
                ConnectAsync();
            }
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

        protected override void OnConnected()
        {
            _logger.LogInformation($"SSL client connected a new session with Id {Id}");
        }

        protected override void OnError(SocketError error)
        {
            _logger.LogError($"Base SSL client caught an error with code {error}");
        }
    }
}
