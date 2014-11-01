﻿using System;
using System.Linq;
using d60.Cirqus.Aggregates;
using d60.Cirqus.Events;
using d60.Cirqus.Logging;
using d60.Cirqus.Serialization;
using d60.Cirqus.Views;

namespace d60.Cirqus.Config.Configurers
{
    class CommandProcessorConfigurationBuilder :
        ILoggingAndEventStoreConfigurationBuilderApi,
        IAggregateRootRepositoryConfigurationBuilderApi,
        IEventDispatcherConfigurationBuilderApi,
        IFullConfiguration
    {
        static Logger _logger;

        static CommandProcessorConfigurationBuilder()
        {
            CirqusLoggerFactory.Changed += f => _logger = f.GetCurrentClassLogger();
        }

        readonly ConfigurationContainer _container = new ConfigurationContainer();

        public IAggregateRootRepositoryConfigurationBuilderApi EventStore(Action<EventStoreConfigurationBuilder> configure)
        {
            configure(new EventStoreConfigurationBuilder(_container));
            return this;
        }

        public IEventStoreConfigurationBuilderApi Logging(Action<LoggingConfigurationBuilder> configure)
        {
            configure(new LoggingConfigurationBuilder(_container));
            return this;
        }

        public IEventDispatcherConfigurationBuilderApi AggregateRootRepository(Action<AggregateRootRepositoryConfigurationBuilder> configure)
        {
            configure(new AggregateRootRepositoryConfigurationBuilder(_container));
            return this;
        }

        public IFullConfiguration EventDispatcher(Action<EventDispatcherConfigurationBuilder> configure)
        {
            configure(new EventDispatcherConfigurationBuilder(_container));
            return this;
        }

        public IFullConfiguration Options(Action<OptionsConfigurationBuilder> configure)
        {
            configure(new OptionsConfigurationBuilder(_container));
            return this;
        }

        public ICommandProcessor Create()
        {
            FillInDefaults();

            var resolutionContext = _container.CreateContext();

            var eventStore = resolutionContext.Get<IEventStore>();
            var aggregateRootRepository = resolutionContext.Get<IAggregateRootRepository>();
            var eventDispatcher = resolutionContext.Get<IEventDispatcher>();
            var serializer = resolutionContext.Get<IDomainEventSerializer>();

            var commandProcessor = new CommandProcessor(eventStore, aggregateRootRepository, eventDispatcher, serializer);

            commandProcessor.Disposed += () =>
            {
                var disposables = resolutionContext.GetDisposables();

                foreach (var disposable in disposables)
                {
                    _logger.Debug("Disposing {0}", disposable);

                    disposable.Dispose();
                }
            };

            resolutionContext.GetAll<Action<Options>>()
                .ToList()
                .ForEach(action => action(commandProcessor.Options));

            commandProcessor.Initialize();

            return commandProcessor;
        }

        void FillInDefaults()
        {
            if (!_container.HasService<IAggregateRootRepository>(checkForPrimary: true))
            {
                _container.Register<IAggregateRootRepository>(context => new DefaultAggregateRootRepository(context.Get<IEventStore>(), context.Get<IDomainEventSerializer>()));
            }

            if (!_container.HasService<IDomainEventSerializer>(checkForPrimary: true))
            {
                _container.Register<IDomainEventSerializer>(context => new JsonDomainEventSerializer());
            }
        }
    }
}