﻿// Developed by Softeq Development Corporation
// http://www.softeq.com

using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softeq.NetKit.Components.EventBus.Abstract;
using Softeq.NetKit.Components.EventBus.Events;
using Softeq.NetKit.Components.EventBus.Managers;
using Softeq.NetKit.Components.EventBus.Service.Connection;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Softeq.NetKit.Components.EventBus.Service
{
    public class EventBusService : IEventBusPublisher, IEventBusSubscriber
    {
        private readonly MessageQueueConfiguration _messageQueueConfiguration;
        private readonly IEventBusSubscriptionsManager _subscriptionsManager;
        private readonly IServiceProvider _serviceProvider;
        private readonly ServiceBusTopicConnection _topicConnection;
        private readonly ServiceBusQueueConnection _queueConnection;
        private readonly ILogger _logger;

        private bool IsSubscriptionAvailable => _topicConnection?.SubscriptionClient != null;
        private bool IsQueueAvailable => _queueConnection != null;

        public EventBusService(IServiceBusPersisterConnection serviceBusPersisterConnection,
            IEventBusSubscriptionsManager subscriptionsManager,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            MessageQueueConfiguration messageQueueConfiguration)
        {
            _topicConnection = serviceBusPersisterConnection.TopicConnection;
            _queueConnection = serviceBusPersisterConnection.QueueConnection;
            _subscriptionsManager = subscriptionsManager;
            _serviceProvider = serviceProvider;
            _messageQueueConfiguration = messageQueueConfiguration;
            _logger = loggerFactory.CreateLogger(GetType());
        }

        public Task PublishToTopicAsync(IntegrationEvent @event, int? delayInSeconds = null)
        {
            ValidateTopic();
            return PublishMessageAsync(@event, _topicConnection.TopicClient, delayInSeconds);
        }

        public Task PublishToQueueAsync(IntegrationEvent @event, int? delayInSeconds = null)
        {
            ValidateQueue();
            return PublishMessageAsync(@event, _queueConnection.QueueClient, delayInSeconds);
        }

        private Task PublishMessageAsync(IntegrationEvent @event, ISenderClient client, int? delayInSeconds = null)
        {
            var message = GetPublishedMessage(@event);
            return delayInSeconds.HasValue
                ? client.ScheduleMessageAsync(message, DateTime.UtcNow.AddSeconds(delayInSeconds.Value))
                : client.SendAsync(message);
        }

        public async Task SubscribeAsync<TEvent, TEventHandler>() where TEvent : IntegrationEvent where TEventHandler : IEventHandler<TEvent>
        {
            var eventName = typeof(TEvent).Name;

            var containsKey = _subscriptionsManager.HasSubscriptionsForEvent<TEvent>();
            if (!containsKey)
            {
                if (IsSubscriptionAvailable)
                {
                    await AddSubscriptionRule(eventName);
                }

                _subscriptionsManager.AddSubscription<TEvent, TEventHandler>();
            }
        }

        public async Task UnsubscribeAsync<TEvent, TEventHandler>() where TEvent : IntegrationEvent where TEventHandler : IEventHandler<TEvent>
        {
            var eventName = typeof(TEvent).Name;

            if (IsSubscriptionAvailable)
            {
                await RemoveSubscriptionRule(eventName);
            }

            _subscriptionsManager.RemoveSubscription<TEvent, TEventHandler>();
        }

        public void SubscribeDynamic<TEventHandler>(string eventName) where TEventHandler : IDynamicEventHandler
        {
            _subscriptionsManager.AddDynamicSubscription<TEventHandler>(eventName);
        }

        public void UnsubscribeDynamic<TEventHandler>(string eventName) where TEventHandler : IDynamicEventHandler
        {
            _subscriptionsManager.RemoveDynamicSubscription<TEventHandler>(eventName);
        }

        private async Task RemoveDefaultRule()
        {
            try
            {
                await _topicConnection.SubscriptionClient.RemoveRuleAsync(RuleDescription.DefaultRuleName);
            }
            catch (MessagingEntityNotFoundException ex)
            {
                throw new Exceptions.ServiceBusException($"The messaging entity {RuleDescription.DefaultRuleName} could not be found.", ex.InnerException);
            }
        }

        public async Task RegisterSubscriptionListenerAsync()
        {
            ValidateSubscription();

            await RemoveDefaultRule();

            _topicConnection.SubscriptionClient.RegisterMessageHandler(async (message, token) => await HandleReceivedMessage(_topicConnection.SubscriptionClient, _topicConnection.TopicClient, message, token),
                new MessageHandlerOptions(ExceptionReceivedHandler) { MaxConcurrentCalls = 10, AutoComplete = false });
        }

        public void RegisterQueueListener()
        {
            ValidateQueue();

            _queueConnection.QueueClient.RegisterMessageHandler(async (message, token) => await HandleReceivedMessage(_queueConnection.QueueClient, _queueConnection.QueueClient, message, token),
                new MessageHandlerOptions(ExceptionReceivedHandler) { MaxConcurrentCalls = 1, AutoComplete = false });
        }

        private async Task AddSubscriptionRule(string eventName)
        {
            try
            {
                await _topicConnection.SubscriptionClient.AddRuleAsync(new RuleDescription
                {
                    Filter = new CorrelationFilter { Label = eventName },
                    Name = eventName
                });
            }
            catch (ServiceBusException ex)
            {
                throw new Exceptions.ServiceBusException($"The messaging entity {eventName} already exists.", ex.InnerException);
            }
        }

        private async Task RemoveSubscriptionRule(string eventName)
        {
            try
            {
                await _topicConnection.SubscriptionClient.RemoveRuleAsync(eventName);
            }
            catch (MessagingEntityNotFoundException ex)
            {
                throw new Exceptions.ServiceBusException($"The messaging entity {eventName} could not be found.", ex.InnerException);
            }
        }

        private async Task HandleReceivedMessage(IReceiverClient receiverClient, ISenderClient senderClient, Message message, CancellationToken token)
        {
            var eventName = message.Label;
            var messageData = Encoding.UTF8.GetString(message.Body);
            await ProcessEvent(eventName, messageData);

            // Complete the message so that it is not received again.
            await receiverClient.CompleteAsync(message.SystemProperties.LockToken);

            var eventType = _subscriptionsManager.GetEventTypeByName(eventName);
            if (eventType != null && eventType != typeof(CompletedEvent))
            {
                dynamic eventData = JObject.Parse(messageData);
                if (Guid.TryParse(eventData["Id"] as string, out var eventId))
                {
                    var completedEvent = new CompletedEvent(eventId);
                    await PublishMessageAsync(completedEvent, senderClient);
                }
            }
        }

        private async Task ProcessEvent(string eventName, string message)
        {
            if (!_subscriptionsManager.HasSubscriptionsForEvent(eventName))
            {
                return;
            }

            using (var scope = _serviceProvider.CreateScope())
            {
                var subscriptions = _subscriptionsManager.GetEventHandlers(eventName);
                foreach (var subscription in subscriptions)
                {
                    var handler = scope.ServiceProvider.GetService(subscription.HandlerType);

                    if (subscription.IsDynamic && handler is IDynamicEventHandler eventHandler)
                    {
                        dynamic eventData = JObject.Parse(message);
                        await eventHandler.Handle(eventData);
                    }
                    else if (handler != null)
                    {
                        var eventType = _subscriptionsManager.GetEventTypeByName(eventName);
                        var integrationEvent = JsonConvert.DeserializeObject(message, eventType);
                        var concreteType = typeof(IEventHandler<>).MakeGenericType(eventType);
                        await (Task)concreteType.GetMethod("Handle").Invoke(handler, new[] { integrationEvent });
                    }
                }
            }
        }

        private Task ExceptionReceivedHandler(ExceptionReceivedEventArgs exceptionReceivedEventArgs)
        {
            var context = exceptionReceivedEventArgs.ExceptionReceivedContext;
            _logger.LogInformation($"Message handler encountered an exception {exceptionReceivedEventArgs.Exception}.\n" +
                                   "Exception context for troubleshooting:\n" +
                                   $"Endpoint: {context.Endpoint}\n" +
                                   $"Entity Path: {context.EntityPath}\n" +
                                   $"Executing Action: {context.Action}");
            return Task.CompletedTask;
        }

        private Message GetPublishedMessage(IntegrationEvent @event)
        {
            var eventName = @event.GetType().Name;
            var jsonMessage = JsonConvert.SerializeObject(@event);
            var body = Encoding.UTF8.GetBytes(jsonMessage);

            var message = new Message
            {
                MessageId = Guid.NewGuid().ToString(),
                Body = body,
                Label = eventName,
                TimeToLive = TimeSpan.FromHours(_messageQueueConfiguration.TimeToLive),
            };

            return message;
        }

        private void ValidateTopic()
        {
            if (_topicConnection?.TopicClient == null)
            {
                throw new InvalidOperationException("Topic connection is not configured");
            }
        }

        private void ValidateSubscription()
        {
            if (!IsSubscriptionAvailable)
            {
                throw new InvalidOperationException("Topic Subscription connection is not configured");
            }
        }

        private void ValidateQueue()
        {
            if (!IsQueueAvailable)
            {
                throw new InvalidOperationException("Queue connection is not configured");
            }
        }
    }
}
