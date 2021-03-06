// Copyright 2007-2018 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the License for the
// specific language governing permissions and limitations under the License.
namespace MassTransit.AmazonSqsTransport.Pipeline
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Threading.Tasks;
    using Amazon.SQS.Model;
    using Contexts;
    using GreenPipes;
    using GreenPipes.Agents;
    using GreenPipes.Internals.Extensions;
    using Logging;
    using Topology;
    using Transports;
    using Transports.Metrics;


    public interface IBasicConsumer
    {
        Task HandleMessage(Message message);
    }

    /// <summary>
    /// Receives messages from AmazonSQS, pushing them to the InboundPipe of the service endpoint.
    /// </summary>
    public sealed class AmazonSqsBasicConsumer :
        Supervisor,
        IBasicConsumer,
        DeliveryMetrics
    {
        readonly IDeadLetterTransport _deadLetterTransport;
        readonly TaskCompletionSource<bool> _deliveryComplete;
        readonly IErrorTransport _errorTransport;
        readonly Uri _inputAddress;
        readonly ILog _log = Logger.Get<AmazonSqsBasicConsumer>();
        readonly ModelContext _model;
        readonly string _queueUrl;
        readonly ConcurrentDictionary<string, AmazonSqsReceiveContext> _pending;
        readonly IReceiveObserver _receiveObserver;
        readonly IPipe<ReceiveContext> _receivePipe;
        readonly ReceiveSettings _receiveSettings;
        readonly AmazonSqsReceiveEndpointContext _context;
        readonly IDeliveryTracker _tracker;

        /// <summary>
        /// The basic consumer receives messages pushed from the broker.
        /// </summary>
        /// <param name="model">The model context for the consumer</param>
        /// <param name="queueUrl"></param>
        /// <param name="inputAddress">The input address for messages received by the consumer</param>
        /// <param name="receivePipe">The receive pipe to dispatch messages</param>
        /// <param name="receiveObserver">The observer for receive events</param>
        /// <param name="context">The topology</param>
        /// <param name="deadLetterTransport"></param>
        /// <param name="errorTransport"></param>
        public AmazonSqsBasicConsumer(ModelContext model, string queueUrl, Uri inputAddress, IPipe<ReceiveContext> receivePipe,
            IReceiveObserver receiveObserver, AmazonSqsReceiveEndpointContext context,
            IDeadLetterTransport deadLetterTransport, IErrorTransport errorTransport)
        {
            _model = model;
            _queueUrl = queueUrl;
            _inputAddress = inputAddress;
            _receivePipe = receivePipe;
            _receiveObserver = receiveObserver;
            _context = context;
            _deadLetterTransport = deadLetterTransport;
            _errorTransport = errorTransport;

            _tracker = new DeliveryTracker(HandleDeliveryComplete);

            _receiveSettings = model.GetPayload<ReceiveSettings>();

            _pending = new ConcurrentDictionary<string, AmazonSqsReceiveContext>();

            _deliveryComplete = new TaskCompletionSource<bool>();

            SetReady();
        }

        public async Task HandleMessage(Message message)
        {
            if (IsStopping)
            {
                await WaitAndAbandonMessage(message).ConfigureAwait(false);
                return;
            }

            using (var delivery = _tracker.BeginDelivery())
            {
                var redelivered = message.Attributes.ContainsKey("ApproximateReceiveCount") && (int.TryParse(message.Attributes["ApproximateReceiveCount"], out var approximateReceiveCount) && approximateReceiveCount > 1);
                var context = new AmazonSqsReceiveContext(_inputAddress, message, redelivered, _receiveObserver, _context);

                context.GetOrAddPayload(() => _errorTransport);
                context.GetOrAddPayload(() => _deadLetterTransport);

                context.GetOrAddPayload(() => _receiveSettings);
                context.GetOrAddPayload(() => _model);
                context.GetOrAddPayload(() => _model.ConnectionContext);

                try
                {
                    if (!_pending.TryAdd(message.MessageId, context))
                        if (_log.IsErrorEnabled)
                            _log.ErrorFormat("Duplicate BasicDeliver: {0}", message.MessageId);

                    await _receiveObserver.PreReceive(context).ConfigureAwait(false);

                    await _receivePipe.Send(context).ConfigureAwait(false);

                    await context.CompleteTask.ConfigureAwait(false);

                    // Acknowledge
                    await _model.DeleteMessage(_queueUrl, message.ReceiptHandle);

                    await _receiveObserver.PostReceive(context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await _receiveObserver.ReceiveFault(context, ex).ConfigureAwait(false);
                    try
                    {
                        //_model.BasicNack(deliveryTag, false, true);
                    }
                    catch (Exception ackEx)
                    {
                        if (_log.IsErrorEnabled)
                            _log.ErrorFormat("An error occurred trying to NACK a message with delivery tag {0}: {1}", message.MessageId, ackEx.ToString());
                    }
                }
                finally
                {
                    _pending.TryRemove(message.MessageId, out _);

                    context.Dispose();
                }
            }
        }

        long DeliveryMetrics.DeliveryCount => _tracker.DeliveryCount;

        int DeliveryMetrics.ConcurrentDeliveryCount => _tracker.MaxConcurrentDeliveryCount;

        void HandleDeliveryComplete()
        {
            if (IsStopping)
            {
                if (_log.IsDebugEnabled)
                    _log.DebugFormat("Consumer shutdown completed: {0}", _context.InputAddress);

                _deliveryComplete.TrySetResult(true);
            }
        }

        async Task WaitAndAbandonMessage(Message message)
        {
            try
            {
                await _deliveryComplete.Task.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (_log.IsErrorEnabled)
                    _log.Debug("Shutting down, deliveryComplete Faulted: {_topology.InputAddress}", exception);
            }
        }

        protected override async Task StopSupervisor(StopSupervisorContext context)
        {
            if (_log.IsDebugEnabled)
                _log.DebugFormat("Stopping consumer: {0}", _context.InputAddress);

            SetCompleted(ActiveAndActualAgentsCompleted(context));

            await Completed.ConfigureAwait(false);
        }

        async Task ActiveAndActualAgentsCompleted(StopSupervisorContext context)
        {
            await Task.WhenAll(context.Agents.Select(x => Completed)).UntilCompletedOrCanceled(context.CancellationToken).ConfigureAwait(false);

            if (_tracker.ActiveDeliveryCount > 0)
            {
                try
                {
                    await _deliveryComplete.Task.UntilCompletedOrCanceled(context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (_log.IsWarnEnabled)
                        _log.WarnFormat("Stop canceled waiting for message consumers to complete: {0}", _context.InputAddress);
                }
            }

            //try
            //{
            //    _messageConsumer.Close();
            //    _messageConsumer.Dispose();
            //}
            //catch (OperationCanceledException)
            //{
            //    if (_log.IsWarnEnabled)
            //        _log.WarnFormat("Exception canceling the consumer: {0}", _context.InputAddress);
            //}
        }
    }
}
