﻿// CYPCore by Matthew Hellyer is licensed under CC BY-NC-ND 4.0.
// To view a copy of this license, visit https://creativecommons.org/licenses/by-nc-nd/4.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using MessagePack;
using Serilog;
using CYPCore.Cryptography;
using CYPCore.Extensions;
using CYPCore.Helper;
using CYPCore.Messages;
using CYPCore.Models;
using CYPCore.Serf.Message;
using static System.GC;

namespace CYPCore.Serf
{
    /// <summary>
    /// 
    /// </summary>
    public interface ISerfClient
    {
        ulong ClientId { get; }
        string ProcessError { get; set; }
        bool ProcessStarted { get; set; }
        int ProcessId { get; set; }
        string Name { get; set; }
        SerfConfigurationOptions SerfConfigurationOptions { get; }
        ApiConfigurationOptions ApiConfigurationOptions { get; }
        SerfSeedNodes SeedNodes { get; }
        Task<TaskResult<int>> MembersCount(Guid tcpSessionId);
        Task<SerfError> Authenticate(string secret, Guid tcpSessionId);
        void Dispose();
        Task<SerfError> Handshake(Guid tcpSessionId);
        Task<(KeyActionResponse response, SerfError error)> InstallKey(string key, Guid tcpSessionId);
        Task<TaskResult<JoinMessage>> Join(IEnumerable<string> members, Guid tcpSessionId, bool replay = false);
        Task<SerfError> Leave(Guid tcpSessionId);
        Task<(KeyListResponse response, SerfError error)> ListKeys(Guid tcpSessionId);
        Task<TaskResult<MemberMessage>> Members(Guid tcpSessionId);
        Task<(KeyActionResponse response, SerfError error)> RemoveKey(string key, Guid tcpSessionId);
        Task<TaskResult<SerfError>> Connect(Guid tcpSessionId);
        Task<(KeyActionResponse response, SerfError error)> UseKey(string key, Guid tcpSessionId);
        TcpSession TcpSessionsAddOrUpdate(TcpSession tcpSession);
        TcpSession GetTcpSession(Guid sessionId);
        bool RemoveTcpSession(Guid tcpSessionId);
        Task<TaskResult<ulong>> GetClientId();
        bool JoinedSeedNodes { get; }
    }

    /// <summary>
    /// 
    /// </summary>
    public class SerfClient : ISerfClient, IDisposable
    {
        public string ProcessError { get; set; }
        public bool ProcessStarted { get; set; }
        public int ProcessId { get; set; }
        public string Name { get; set; }

        public SerfConfigurationOptions SerfConfigurationOptions { get; }
        public ApiConfigurationOptions ApiConfigurationOptions { get; }

        public SerfSeedNodes SeedNodes { get; }

        private bool _disposed = false;
        private readonly SafeHandle _safeHandle = new SafeFileHandle(IntPtr.Zero, true);
        private CancellationTokenSource _cancellationTokenSource;
        private Task _responseReaderTask;

        private IDictionary<ulong, TransactionContext> _handlers = new ConcurrentDictionary<ulong, TransactionContext>();
        private long _internalSeqence;

        private ConcurrentDictionary<Guid, TcpSession> TcpSessions { get; }

        private readonly ISigning _signing;
        private readonly ILogger _logger;

        public SerfClient(ISigning signing, SerfConfigurationOptions serfConfigurationOptions,
            ApiConfigurationOptions apiConfigurationOptions, SerfSeedNodes seedNodes, ILogger logger)
        {
            _signing = signing;
            _logger = logger.ForContext("SourceContext", nameof(SerfClient));

            SerfConfigurationOptions = serfConfigurationOptions;
            ApiConfigurationOptions = apiConfigurationOptions;
            SeedNodes = seedNodes;

            TcpSessions = new ConcurrentDictionary<Guid, TcpSession>();

            ClientId = GetClientId().GetAwaiter().GetResult().Value;
        }

        /// <summary>
        /// 
        /// </summary>
        public ulong ClientId { get; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sessionId"></param>
        /// <returns></returns>
        public TcpSession GetTcpSession(Guid sessionId) => TcpSessions.GetValueOrDefault(sessionId);

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public async Task<TaskResult<ulong>> GetClientId()
        {
            ulong clientId;

            try
            {
                var pubKey = await _signing.GetPublicKey(_signing.DefaultSigningKeyName);
                clientId = Util.HashToId(pubKey.ByteToHex());
            }
            catch (Exception ex)
            {
                _logger.Here().Error(ex, "Cannot get client ID");
                return TaskResult<ulong>.CreateFailure(new SerfError { Error = ex.Message });
            }

            return TaskResult<ulong>.CreateSuccess(clientId);
        }

        /// <summary>
        /// 
        /// </summary>
        public bool JoinedSeedNodes { get; private set; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        public async Task<TaskResult<int>> MembersCount(Guid tcpSessionId)
        {
            int memberCount;

            try
            {
                var membersResult = await Members(tcpSessionId);
                if (membersResult.Success)
                {
                    var count = membersResult.Value.Members.Count();
                    count--;
                    memberCount = count == -1 ? 0 : count;
                }
                else
                {
                    return TaskResult<int>.CreateFailure(membersResult.NonSuccessMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.Here().Error(ex, "Cannot get members count");
                return TaskResult<int>.CreateFailure(new SerfError { Error = ex.Message });
            }

            return TaskResult<int>.CreateSuccess(memberCount);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        public async Task<TaskResult<SerfError>> Connect(Guid tcpSessionId)
        {
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _responseReaderTask = ReadResponses(_cancellationTokenSource.Token, tcpSessionId);

                var handshakeError = await Handshake(tcpSessionId);

                if (handshakeError != null)
                {
                    return TaskResult<SerfError>.CreateFailure(handshakeError);
                }
            }
            catch (ObjectDisposedException ex)
            {
                return TaskResult<SerfError>.CreateFailure(new SerfError { Error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.Here().Error(ex, "Cannot establish connection");
                return TaskResult<SerfError>.CreateFailure(new SerfError { Error = ex.Message });
            }

            return TaskResult<SerfError>.CreateSuccess(new SerfError());
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        public Task<SerfError> Handshake(Guid tcpSessionId)
        {
            return HandleRequest(SerfCommandLine.Handshake, new Handshake { Version = 1 }, tcpSessionId);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="secret"></param>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        public Task<SerfError> Authenticate(string secret, Guid tcpSessionId)
        {
            return HandleRequest("auth", new Authentication { AuthenticationKey = secret }, tcpSessionId);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="members"></param>
        /// <param name="tcpSessionId"></param>
        /// <param name="replay"></param>
        /// <returns></returns>
        public async Task<TaskResult<JoinMessage>> Join(IEnumerable<string> members, Guid tcpSessionId, bool replay = false)
        {
            uint nodesJoined;

            try
            {
                var join = new JoinRequest { Existing = members.ToArray(), Replay = replay };
                var (node, error) = await HandleRequestReponse<JoinRequest, JoinResponse>(SerfCommandLine.Join, join, tcpSessionId);

                if (error != null)
                {
                    return TaskResult<JoinMessage>.CreateFailure(error);
                }

                nodesJoined = node.Peers;
                JoinedSeedNodes = true;
            }
            catch (Exception ex)
            {
                return TaskResult<JoinMessage>.CreateFailure(new SerfError { Error = ex.Message });
            }

            return TaskResult<JoinMessage>.CreateSuccess(new JoinMessage(nodesJoined, null));
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        public async Task<TaskResult<MemberMessage>> Members(Guid tcpSessionId)
        {
            IEnumerable<Members> members;

            try
            {
                var (response, error) = await HandleRequestReponse<MembersRequest, MembersResponse>(SerfCommandLine.Members, null, tcpSessionId);
                if (error != null)
                {
                    return TaskResult<MemberMessage>.CreateFailure(error);
                }

                members = response.Members;
            }
            catch (ObjectDisposedException ex)
            {
                return TaskResult<MemberMessage>.CreateFailure(ex.GetType().Name);
            }
            catch (Exception ex)
            {

                return TaskResult<MemberMessage>.CreateFailure(new SerfError { Error = ex.Message });
            }

            return TaskResult<MemberMessage>.CreateSuccess(new MemberMessage(members, null));
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        public Task<SerfError> Leave(Guid tcpSessionId)
        {
            return HandleRequest<LeaveRequest>(SerfCommandLine.Leave, null, tcpSessionId);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        public Task<(KeyActionResponse response, SerfError error)> InstallKey(string key, Guid tcpSessionId)
        {
            return HandleRequestReponse<KeyRequest, KeyActionResponse>(SerfCommandLine.InstallKey, new KeyRequest { Key = key }, tcpSessionId);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        public Task<(KeyActionResponse response, SerfError error)> UseKey(string key, Guid tcpSessionId)
        {
            return HandleRequestReponse<KeyRequest, KeyActionResponse>(SerfCommandLine.UseKey, new KeyRequest { Key = key }, tcpSessionId);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        public Task<(KeyActionResponse response, SerfError error)> RemoveKey(string key, Guid tcpSessionId)
        {
            return HandleRequestReponse<KeyRequest, KeyActionResponse>(SerfCommandLine.RemoveKey, new KeyRequest { Key = key }, tcpSessionId);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        public Task<(KeyListResponse response, SerfError error)> ListKeys(Guid tcpSessionId)
        {
            return HandleRequestReponse<KeyRequest, KeyListResponse>(SerfCommandLine.ListKey, null, tcpSessionId);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="nameOrAddress"></param>
        /// <returns></returns>
        public static bool PingHost(string nameOrAddress)
        {
            bool pingable = false;
            Ping pinger = null;

            try
            {
                pinger = new Ping();
                PingReply reply = pinger.Send(nameOrAddress);
                pingable = reply.Status == IPStatus.Success;
            }
            catch (PingException)
            {
                // Discard PingExceptions and return false;
            }
            finally
            {
                if (pinger != null)
                {
                    pinger.Dispose();
                }
            }

            return pingable;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tcpSession"></param>
        /// <returns></returns>
        public TcpSession TcpSessionsAddOrUpdate(TcpSession tcpSession)
        {
            var mSession = TcpSessions.AddOrUpdate(tcpSession.SessionId, tcpSession,
                            (Key, existingVal) =>
                            {
                                if (tcpSession != existingVal)
                                    throw new ArgumentException("Duplicate session ids are not allowed: {0}.", tcpSession.SessionId.ToString());

                                existingVal.TcpClient = tcpSession.TcpClient;
                                existingVal.TransportStream = tcpSession.TransportStream;

                                return existingVal;
                            });

            return mSession;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        public bool RemoveTcpSession(Guid tcpSessionId)
        {
            return TcpSessions.TryRemove(tcpSessionId, out TcpSession tcpSession);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TRequest"></typeparam>
        /// <typeparam name="TResponse"></typeparam>
        /// <param name="command"></param>
        /// <param name="request"></param>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        private async Task<(TResponse, SerfError)> HandleRequestReponse<TRequest, TResponse>(string command, TRequest request, Guid tcpSessionId) where TRequest : class
        {
            var sequence = (ulong)Interlocked.Increment(ref _internalSeqence);
            var reset = new ManualResetEventSlim(false);
            var cancellationToken = new CancellationTokenSource();
            var transaction = new TransactionContext { CancellationTokenSource = cancellationToken };

            SerfError error = null;
            TResponse response = default;

            _handlers.Add(sequence, transaction);

            cancellationToken.Token.Register(() =>
            {
                if (!string.IsNullOrWhiteSpace(transaction.Header.Error))
                {
                    error = new SerfError { Error = transaction.Header.Error };
                }
                else
                {
                    if (transaction.ResponseBuffer.Length != 0)
                    {
                        try
                        {
                            response = MessagePackSerializer.Deserialize<TResponse>(transaction.ResponseBuffer);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                        }
                    }
                }

                _handlers.Remove(sequence);
                reset.Set();
            });

            var headerBytes = MessagePackSerializer.Serialize(new RequestHeader { Command = command, Sequence = sequence });
            byte[] instructionBytes = headerBytes;

            if (request != default)
            {
                instructionBytes = headerBytes.Concat(MessagePackSerializer.Serialize(request)).ToArray();
            }

            var tcpSession = GetTcpSession(tcpSessionId);

            await tcpSession.TransportStream.WriteAsync(instructionBytes, 0, instructionBytes.Length);

            reset.Wait(_cancellationTokenSource.Token);

            return (response, error);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TRequest"></typeparam>
        /// <param name="command"></param>
        /// <param name="request"></param>
        /// <param name="tcpSessionId"></param>
        /// <returns></returns>
        private async Task<SerfError> HandleRequest<TRequest>(string command, TRequest request, Guid tcpSessionId)
        {
            var sequence = (ulong)Interlocked.Increment(ref _internalSeqence);
            var headerBytes = MessagePackSerializer.Serialize(new RequestHeader { Command = command, Sequence = sequence });
            var commandBytes = MessagePackSerializer.Serialize(request);
            var instructionBytes = headerBytes.Concat(commandBytes).ToArray();
            var cancellationToken = new CancellationTokenSource();
            var transaction = new TransactionContext { CancellationTokenSource = cancellationToken };
            var reset = new ManualResetEventSlim(false);

            SerfError error = null;

            _handlers.Add(sequence, transaction);

            cancellationToken.Token.Register(() =>
            {
                if (!string.IsNullOrWhiteSpace(transaction.Header.Error))
                {
                    error = new SerfError { Error = transaction.Header.Error };
                }

                reset.Set();
            });

            var tcpSession = GetTcpSession(tcpSessionId);
            await tcpSession.TransportStream?.WriteAsync(instructionBytes, 0, instructionBytes.Length);

            reset.Wait(_cancellationTokenSource.Token);

            return error;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task ReadResponses(CancellationToken token, Guid tcpSessionId)
        {
            try
            {
                var tcpSession = GetTcpSession(tcpSessionId);
                await using (_cancellationTokenSource.Token.Register(() => tcpSession.TransportStream.Close()))
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        if (tcpSession.TransportStream == null)
                        {
                            return;
                        }

                        var readBuffer = new byte[8048];
                        var size = await tcpSession.TransportStream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length),
                            _cancellationTokenSource.Token);
                        if (size <= 0)
                        {
                            if (!PingHost(SerfConfigurationOptions.RPC))
                            {
                                _cancellationTokenSource.Cancel();
                            }

                            continue;
                        }

                        try
                        {
                            var responseHeader =
                                MessagePackSerializer.Deserialize<ResponseHeader>(readBuffer,
                                    MessagePackSerializerOptions.Standard);
                            if (_handlers.ContainsKey(responseHeader.Seq))
                            {
                                var readSize = MessagePackSerializer.Serialize(responseHeader).Length;
                                var transaction = _handlers[responseHeader.Seq];
                                transaction.Header = responseHeader;
                                transaction.ResponseBuffer = readBuffer.Skip(readSize).Take(size - readSize).ToArray();
                                transaction.CancellationTokenSource.Cancel();
                                if (transaction.Header.Error == "Handshake required")
                                {
                                    break;
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (NullReferenceException)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                var error = new ResponseHeader
                {
                    Error = $"failed to read from socket: {e.Message} Message processing will be terminated."
                };
                foreach (var (_, value) in _handlers)
                {
                    value.Header = error;
                    value.CancellationTokenSource.Cancel();
                }

                throw;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void Dispose() => Dispose(true);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _cancellationTokenSource?.Cancel();

                try
                {
                    _responseReaderTask?.Wait();

                    // Suppress finalization.
                    SuppressFinalize(this);
                }
                catch (TaskCanceledException)
                {
                }
                catch (AggregateException)
                {
                }
                finally
                {
                    _safeHandle?.Dispose();
                }
            }

            _disposed = true;
        }
    }
}
