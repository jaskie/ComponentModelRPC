﻿//#undef DEBUG

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace jNet.RPC.Server
{
    internal class ServerSession : SocketConnection
    {
        private readonly Dictionary<DelegateKey, Delegate> _delegates = new Dictionary<DelegateKey, Delegate>();
        private readonly IPrincipal _sessionUser;
        private readonly IDto _initialObject;
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        protected readonly ConcurrentQueue<SocketMessage> _receiveQueue = new ConcurrentQueue<SocketMessage>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();               

        public ServerSession(TcpClient client, IDto initialObject, IPrincipalProvider principalProvider): base(client, new ServerReferenceResolver())
        {
            _initialObject = initialObject;
           
            if (!(client.Client.RemoteEndPoint is IPEndPoint))
                throw new UnauthorizedAccessException("Client RemoteEndpoint is invalid");
            _sessionUser = principalProvider.GetPrincipal(client);
            if (_sessionUser == null)
                throw new UnauthorizedAccessException($"Client {Client.Client.RemoteEndPoint} not allowed");
            ((ServerReferenceResolver)ReferenceResolver).ReferencePropertyChanged += ReferenceResolver_ReferencePropertyChanged;
            StartThreads();           
        }

#if DEBUG
        ~ServerSession()
        {
            Debug.WriteLine("Finalized: {0} for {1}", this, _initialObject);
        }
#endif

        protected override void ReadThreadProc()
        {
            Thread.CurrentPrincipal = _sessionUser;
            base.ReadThreadProc();
        }

        protected override void WriteThreadProc()
        {
            Thread.CurrentPrincipal = _sessionUser;
            base.WriteThreadProc();
        }

        protected override void EnqueueMessage(SocketMessage message)
        {
            _receiveQueue.Enqueue(message);
            if (_messageReceivedSempahore.CurrentCount == 0)
                _messageReceivedSempahore.Release();
        }        

        protected override async Task MessageHandlerProc()
        {
            Thread.CurrentPrincipal = _sessionUser;

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                if (!_receiveQueue.TryDequeue(out var message))
                {
                    try
                    {
                        await _messageReceivedSempahore.WaitAsync(_cancellationTokenSource.Token);
                    }
                    catch (Exception ex)
                    {
                        if (ex.GetType() == typeof(OperationCanceledException))
                            break;

                        Logger.Error(ex, "Unexpected error in MessageHandler.");
                    }
                    continue;
                }

                if (_cancellationTokenSource.IsCancellationRequested)
                    break;

                if (message.MessageType != SocketMessage.SocketMessageType.EventNotification)
                    Logger.Debug("Processing message: {0}:{1}", message.MessageGuid, message.DtoGuid);
                
                try
                {
                    if (message.MessageType == SocketMessage.SocketMessageType.RootQuery)
                    {
                        SendResponse(message, _initialObject);
                    }                    
                    else // method of particular object
                    {
                        var objectToInvoke = ((ServerReferenceResolver)ReferenceResolver).ResolveReference(message.DtoGuid);
                        if (objectToInvoke != null)
                        {
                            Debug.WriteLine($"{objectToInvoke}:{message.MemberName}");
                            switch (message.MessageType)
                            {
                                case SocketMessage.SocketMessageType.Query:
                                    var objectToInvokeType = objectToInvoke.GetType();
                                    var methodToInvoke = objectToInvokeType.GetMethods()
                                        .FirstOrDefault(m => m.Name == message.MemberName &&
                                                             m.GetParameters().Length == message.ParametersCount);
                                    if (methodToInvoke != null)
                                    {
                                        var parameters = DeserializeDto<SocketMessageArrayValue>(message.ValueStream);
                                        var methodParameters = methodToInvoke.GetParameters();
                                        for (var i = 0; i < methodParameters.Length; i++)
                                            MethodParametersAlignment.AlignType(ref parameters.Value[i],
                                                methodParameters[i].ParameterType);
                                        object response = null;
                                        try
                                        {
                                            response = methodToInvoke.Invoke(objectToInvoke, parameters.Value);
                                        }
                                        catch (Exception e)
                                        {
                                            SendException(message, e);
                                            throw;
                                        }
                                        SendResponse(message, response);
                                    }
                                    else
                                        throw new ApplicationException(
                                            $"Server: unknown method: {objectToInvoke}:{message.MemberName}");
                                    break;
                                case SocketMessage.SocketMessageType.Get:
                                    var getProperty = objectToInvoke.GetType().GetProperty(message.MemberName);
                                    if (getProperty != null)
                                    {
                                        object response;
                                        try
                                        {
                                            response = getProperty.GetValue(objectToInvoke, null);
                                        }
                                        catch (Exception e)
                                        {
                                            SendException(message, e);
                                            throw;
                                        }
                                        SendResponse(message, response);
                                    }
                                    else
                                        throw new ApplicationException(
                                            $"Server: unknown property: {objectToInvoke}:{message.MemberName}");
                                    break;
                                case SocketMessage.SocketMessageType.Set:
                                    var setProperty = objectToInvoke.GetType().GetProperty(message.MemberName);
                                    if (setProperty != null)
                                    {
                                        var parameter = DeserializeDto<object>(message.ValueStream);
                                        MethodParametersAlignment.AlignType(ref parameter, setProperty.PropertyType);
                                        try
                                        {
                                            setProperty.SetValue(objectToInvoke, parameter, null);
                                            SendResponse(message, null);
                                        }
                                        catch (Exception e)
                                        {
                                            SendException(message, e);
                                            throw;
                                        }
                                    }
                                    else
                                        throw new ApplicationException(
                                            $"Server: unknown property: {objectToInvoke}:{message.MemberName}");
                                    break;
                                case SocketMessage.SocketMessageType.EventAdd:
                                case SocketMessage.SocketMessageType.EventRemove:
                                    var ei = objectToInvoke.GetType().GetEvent(message.MemberName);
                                    if (ei != null)
                                    {
                                        if (message.MessageType == SocketMessage.SocketMessageType.EventAdd)
                                            AddDelegate(objectToInvoke, ei);
                                        else if (message.MessageType == SocketMessage.SocketMessageType.EventRemove)
                                            RemoveDelegate(objectToInvoke, ei);
                                    }
                                    else
                                        throw new ApplicationException(
                                            $"Server: unknown event: {objectToInvoke}:{message.MemberName}");
                                    SendResponse(message, null);
                                    break;
                                case SocketMessage.SocketMessageType.ProxyFinalized:
                                    RemoveReference(objectToInvoke);
                                    SendResponse(message, null);
                                    break;
                            }
                        }                        
                        else
                        {
                            Logger.Debug("Dto send by client not found! {0}", message.DtoGuid);
                            SendResponse(message, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Exception while handling message. {ex.Message}");
                    Logger.Error(ex);
                }                                
            }
        }

        
        private void SendException(SocketMessage message, Exception exception)
        {
            message.MessageType = SocketMessage.SocketMessageType.Exception;
            SendResponse(message, new Exception(exception.Message, exception.InnerException == null ? null : new Exception(exception.InnerException.Message)));
        }

        protected override void OnDispose()
        {
            base.OnDispose();
            _cancellationTokenSource.Cancel();           
            ((ServerReferenceResolver)ReferenceResolver).ReferencePropertyChanged -= ReferenceResolver_ReferencePropertyChanged;
            lock (((IDictionary) _delegates).SyncRoot)
            {
                foreach (var d in _delegates.Keys.ToArray())
                {
                    var havingDelegate = ((ServerReferenceResolver)ReferenceResolver).ResolveReference(d.DtoGuid);
                    if (havingDelegate == null)
                        throw new ApplicationException("Referenced object not found");
                    var ei = havingDelegate.GetType().GetEvent(d.EventName);
                    RemoveDelegate(havingDelegate, ei);
                }
            }
            ((ServerReferenceResolver)ReferenceResolver).Dispose();
        }

        private void SendResponse(SocketMessage message, object response)
        {            
            Send(new SocketMessage(message, response));
        }


        private void AddDelegate(IDto objectToInvoke, EventInfo ei)
        {
            var signature = new DelegateKey(objectToInvoke.DtoGuid, ei.Name);
            lock (((IDictionary) _delegates).SyncRoot)
            {
                if (_delegates.ContainsKey(signature))
                    return;
                var delegateToInvoke = ConvertDelegate((Action<object, EventArgs>) delegate(object o, EventArgs ea) { NotifyClient(objectToInvoke, ea, ei.Name); }, ei.EventHandlerType);
                Debug.WriteLine($"Server: added delegate {ei.Name} on {objectToInvoke}");
                _delegates[signature] = delegateToInvoke;
                ei.AddEventHandler(objectToInvoke, delegateToInvoke);
            }
        }

        private void RemoveDelegate(IDto objectToInvoke, EventInfo ei)
        {
            var signature = new DelegateKey(objectToInvoke.DtoGuid, ei.Name);
            lock (((IDictionary) _delegates).SyncRoot)
            {
                var delegateToRemove = _delegates[signature];
                if (!_delegates.Remove(signature))
                    return;
                ei.RemoveEventHandler(objectToInvoke, delegateToRemove);
                Debug.WriteLine($"Server: removed delegate {ei.Name} on {objectToInvoke}");
            }
        }

        private static Delegate ConvertDelegate(Delegate originalDelegate, Type targetDelegateType)
        {
            return Delegate.CreateDelegate(
                targetDelegateType,
                originalDelegate.Target,
                originalDelegate.Method);
        }

        private void NotifyClient(IDto dto, EventArgs e, string eventName)
        {
            //Debug.Assert(_referenceResolver.ResolveReference(dto.DtoGuid) != null, "Null reference notified");
            try
            {
                if (e is WrappedEventArgs ea
                    && ea.Args is PropertyChangedEventArgs propertyChangedEventArgs
                    && eventName == nameof(INotifyPropertyChanged.PropertyChanged))
                {
                    var p = dto.GetType().GetProperty(propertyChangedEventArgs.PropertyName);
                    PropertyChangedValueReader valueReader;
                    if (p?.CanRead == true)
                        valueReader = new PropertyChangedValueReader(propertyChangedEventArgs.PropertyName, () => p.GetValue(dto, null));
                    else
                    {
                        valueReader = new PropertyChangedValueReader(propertyChangedEventArgs.PropertyName, () => null);
                        Debug.WriteLine(dto,
                            $"{GetType()}: Couldn't get value of {propertyChangedEventArgs.PropertyName}");
                    }
                    Debug.WriteLine($"Server: PropertyChanged {propertyChangedEventArgs.PropertyName} on {dto} sent");
                    Send(new SocketMessage(valueReader)
                    {
                        MessageType = SocketMessage.SocketMessageType.EventNotification,
                        DtoGuid = dto.DtoGuid,
                        MemberName = eventName,
                    });
                }
                else
                    Send(new SocketMessage(e)
                    {
                        MessageType = SocketMessage.SocketMessageType.EventNotification,
                        DtoGuid = dto.DtoGuid,
                        MemberName = eventName,

                    });
            }
            catch (Exception exception)
            {
                Logger.Error(exception);
            }
        }

        private void RemoveReference(IDto dto)
        {
            lock(((IDictionary)_delegates).SyncRoot)
            {
                var delegatesToRemove = _delegates.Keys.Where(k => k.DtoGuid == dto.DtoGuid).ToArray();
                foreach (var dk in delegatesToRemove)
                {
                    var ei = dto.GetType().GetEvent(dk.EventName);
                    RemoveDelegate(dto, ei);
                }
            }
            ((ServerReferenceResolver)ReferenceResolver).RemoveReference(dto);
            Debug.WriteLine($"Server: Reference removed: {dto}");
        }


        private void ReferenceResolver_ReferencePropertyChanged(object sender, WrappedEventArgs e)
        {
            NotifyClient(e.Dto, e, nameof(INotifyPropertyChanged.PropertyChanged));
        }

        private class DelegateKey
        {
            public DelegateKey(Guid dtoGuid, string eventName)
            {
                DtoGuid = dtoGuid;
                EventName = eventName;
            }
            public Guid DtoGuid { get; }
            public string EventName { get; }

            public override bool Equals(object obj)
            {
                if (obj == null) return false;
                if (ReferenceEquals(this, obj)) return true;
                return obj.GetType() == typeof(DelegateKey) && Equals((DelegateKey)obj);
            }

            private bool Equals(DelegateKey other)
            {
                return DtoGuid.Equals(other.DtoGuid) && string.Equals(EventName, other.EventName);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (DtoGuid.GetHashCode() * 397) ^ (EventName != null ? EventName.GetHashCode() : 0);
                }
            }
        }

    }
}
