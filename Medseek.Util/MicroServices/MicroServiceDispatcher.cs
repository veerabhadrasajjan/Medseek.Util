﻿namespace Medseek.Util.MicroServices
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using Medseek.Util.Interactive;
    using Medseek.Util.Ioc;
    using Medseek.Util.Logging;
    using Medseek.Util.Messaging;
    using Medseek.Util.Threading;

    /// <summary>
    /// Provides a micro-service dispatcher using a messaging system connection.
    /// </summary>
    [SuppressMessage("StyleCop.CSharp.NamingRules", "SA1305:FieldNamesMustNotUseHungarianNotation", Justification = "MQ->MessageQueue is not hungarian notation.")]
    [Register(Lifestyle = Lifestyle.Singleton, Start = true)]
    public class MicroServiceDispatcher : IStartable, IDisposable
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly Dictionary<IMqConsumer, List<MicroServiceBinding>> bindingMap = new Dictionary<IMqConsumer, List<MicroServiceBinding>>(); 
        private readonly List<MicroServiceBinding> bindings = new List<MicroServiceBinding>(); 
        private readonly List<IMqConsumer> consumers = new List<IMqConsumer>();
        private readonly IMqChannel channel;
        private readonly IMessageContextAccess messageContextAccess;
        private readonly IMicroServiceLocator microServiceLocator;
        private readonly IMicroServiceSerializer microServiceSerializer;
        private readonly IMqPlugin mqPlugin;
        private readonly IDispatchedThread thread;
        private long dispatcherDepth;
        private bool disposed;
        private bool started;
        private bool threadStarted;

        /// <summary>
        /// Initializes a new instance of the <see cref="MicroServiceDispatcher" /> 
        /// class.
        /// </summary>
        public MicroServiceDispatcher(
            IMqConnection connection, 
            IMessageContextAccess messageContextAccess,
            IMicroServiceLocator microServiceLocator,
            IMicroServiceSerializer microServiceSerializer,
            IMqPlugin mqPlugin,
            IDispatchedThread thread)
        {
            if (connection == null)
                throw new ArgumentNullException("connection");
            if (messageContextAccess == null)
                throw new ArgumentNullException("messageContextAccess");
            if (microServiceLocator == null)
                throw new ArgumentNullException("microServiceLocator");
            if (microServiceSerializer == null)
                throw new ArgumentNullException("microServiceSerializer");
            if (mqPlugin == null)
                throw new ArgumentNullException("mqPlugin");
            if (thread == null)
                throw new ArgumentNullException("thread");

            channel = connection.CreateChannnel();
            this.messageContextAccess = messageContextAccess;
            this.microServiceLocator = microServiceLocator;
            this.microServiceSerializer = microServiceSerializer;
            this.mqPlugin = mqPlugin;
            this.thread = thread;
            thread.Name = "MicroServices.Dispatch";
        }

        /// <summary>
        /// Starts this instance.
        /// </summary>
        public void Start()
        {
            if (disposed)
                throw new ObjectDisposedException(GetType().Name);
            if (started)
                throw new InvalidOperationException();

            Log.DebugFormat("{0}", MethodBase.GetCurrentMethod().Name);
            started = true;
            if (!threadStarted)
            {
                threadStarted = true;
                thread.Start();
            }

            microServiceLocator.Initialize();
            var consumerGroups = microServiceLocator.Bindings
                .Do(bindings.Add)
                .Select(binding => new { binding, address = mqPlugin.ToConsumerAddress(binding.Address) })
                .GroupBy(x => x.address.SourceKey)
                .ToArray();

            thread.Invoke(() =>
            {
                foreach (var consumerGroup in consumerGroups)
                {
                    Log.InfoFormat("Starting message consumer; SourceKey = {0}, Bindings = {1}.", consumerGroup.Key, string.Join(", ", consumerGroup.Select(x => x.binding).Select(x => string.Format("(Addresses = {0}, Service = {1}, Method = {2})", x.Address, x.Service, x.Method))));
                    var addresses = consumerGroup.Select(x => x.address).ToArray();
                    var createdConsumers = channel.CreateConsumers(addresses, true);
                    foreach (var consumer in createdConsumers)
                    {
                        bindingMap[consumer] = consumerGroup.Select(x => x.binding).ToList();
                        consumer.Received += OnReceived;
                        consumers.Add(consumer);
                    }
                }
            });
        }

        /// <summary>
        /// Stops this instance.
        /// </summary>
        public void Stop()
        {
            if (disposed)
                throw new ObjectDisposedException(GetType().Name);
            if (!started)
                throw new InvalidOperationException();

            Log.DebugFormat("{0}", MethodBase.GetCurrentMethod().Name);
            started = false;

            thread.Invoke(() =>
            {
                bindingMap.Clear();
                bindings.Clear();
                foreach (var consumer in consumers.ToArray())
                {
                    consumer.Dispose();
                    consumers.Remove(consumer);
                }
            });
        }

        /// <summary>
        /// Disposes the dispatcher.
        /// </summary>
        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                thread.Dispose();
                channel.Dispose();
            }
        }

        private void OnReceived(object sender, ReceivedEventArgs e)
        {
            Log.DebugFormat("Read {0} bytes from queue; CorrelationId = {1}.", e.Body.Length, e.Properties.CorrelationId);

            Interlocked.Increment(ref dispatcherDepth);
            thread.InvokeAsync(() =>
            {
                Log.DebugFormat("Identifying micro-service invocation details; CorrelationId = {0}.", e.Properties.CorrelationId);
                if (channel.CanPause)
                    channel.IsPaused = Interlocked.Read(ref dispatcherDepth) > 10;
                try
                {
                    var consumer = (IMqConsumer)sender;
                    var bindingsFromMap = bindingMap[consumer];
                    var binding = bindingsFromMap.First(x => mqPlugin.IsMatch(e.Properties, x.Address));
                    var instance = microServiceLocator.Get(binding.Service);
                    try
                    {
                        var messageContext = new MessageContext(e.Properties);

                        object serializerState = null;
                        var parameterTypes = binding.Method.GetParameters().Select(x => x.ParameterType).ToArray();
                        var parameterValues = microServiceSerializer.Deserialize(messageContext, parameterTypes, e.Body, ref serializerState);

                        messageContextAccess.Push(messageContext);
                        try
                        {
                            Log.DebugFormat("Invoking micro-service; Instance = {0}, Method = {1}, Parameters = {2}.", instance.Instance, binding.Method, string.Join(", ", parameterValues));
                            var returnValue = instance.Invoke(binding.Method, parameterValues);
                            if (e.Properties.ReplyTo != null && !binding.IsOneWay)
                            {
                                Log.DebugFormat("Sending reply message; CorrelationId = {0}, ReplyTo = {1}, Response = {2}.", e.Properties.CorrelationId, e.Properties.ReplyTo, returnValue);
                                using (var ms = new MemoryStream())
                                {
                                    var returnType = binding.Method.ReturnType;
                                    microServiceSerializer.Serialize(messageContext, returnType, returnValue, ms, ref serializerState);
                                    var body = ms.ToArray();
                                    using (var publisher = channel.CreatePublisher(e.Properties.ReplyTo))
                                        publisher.Publish(body, e.Properties.CorrelationId);
                                }
                            }

                            //channel.BasicAck(e.DeliveryTag, false);
                        }
                        finally
                        {
                            messageContextAccess.Pop();
                        }
                    }
                    finally
                    {
                        microServiceLocator.Release(instance);
                    }
                }
                catch (Exception ex)
                {
                    var message = string.Format("Unexpected failure while dispatching message; Cause = {0}: {1}.", ex.GetType().Name, ex.Message.TrimEnd('.'));
                    Log.Warn(message, ex);
                }
                finally
                {
                    var depth = Interlocked.Decrement(ref dispatcherDepth);
                    if (channel.CanPause)
                        channel.IsPaused = 10 < depth;
                }
            });
        }
    }
}