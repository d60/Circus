﻿using System;
using System.Collections.Generic;
using System.Linq;
using d60.Cirqus.Aggregates;
using d60.Cirqus.Commands;
using d60.Cirqus.Config;
using d60.Cirqus.Config.Configurers;
using d60.Cirqus.Events;
using d60.Cirqus.Exceptions;
using d60.Cirqus.Extensions;
using d60.Cirqus.Logging;
using d60.Cirqus.Views;

namespace d60.Cirqus
{
    /// <summary>
    /// Main command processor event emitter thing - if you can successfully create this bad boy, you have a fully functioning event sourcing thing going for you
    /// </summary>
    public class CommandProcessor : ICommandProcessor
    {
        static Logger _logger;

        static CommandProcessor()
        {
            CirqusLoggerFactory.Changed += f => _logger = f.GetCurrentClassLogger();
        }

        /// <summary>
        /// Creates a configuration builder that can help you build a fully functional command processor
        /// </summary>
        public static ILoggingAndEventStoreConfigurationBuilderApi With()
        {
            return new CommandProcessorConfigurationBuilder();
        }

        readonly Options _options = new Options();
        readonly Retryer _retryer = new Retryer();
        readonly IEventStore _eventStore;
        readonly IAggregateRootRepository _aggregateRootRepository;
        readonly IEventDispatcher _eventDispatcher;
        bool _initialized;

        public CommandProcessor(IEventStore eventStore, IAggregateRootRepository aggregateRootRepository, IEventDispatcher eventDispatcher)
        {
            if (eventStore == null) throw new ArgumentNullException("eventStore");
            if (aggregateRootRepository == null) throw new ArgumentNullException("aggregateRootRepository");
            if (eventDispatcher == null) throw new ArgumentNullException("eventDispatcher");

            _eventStore = eventStore;
            _aggregateRootRepository = aggregateRootRepository;
            _eventDispatcher = eventDispatcher;
        }

        /// <summary>
        /// Initializes the event dispatcher, giving e.g. views a chance to catch up to the current state
        /// </summary>
        public CommandProcessor Initialize()
        {
            _logger.Info("Initializing event dispatcher");
            _eventDispatcher.Initialize(_eventStore, Options.PurgeExistingViews);
            _initialized = true;
            return this;
        }

        /// <summary>
        /// Accesses the options for this command processor. Mutating the options might/might not have any
        /// effect if the command processor has been initialized
        /// </summary>
        public Options Options
        {
            get { return _options; }
        }

        /// <summary>
        /// Processes the specified command by invoking the generic eventDispatcher method
        /// </summary>
        public CommandProcessingResult ProcessCommand(Command command)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("The command processor is not initialized! Make sure you initialized the command processor before using it.");
            }

            _logger.Debug("Processing command: {0}", command);

            var emittedDomainEvents = new List<DomainEvent>();

            try
            {
                var batchId = Guid.NewGuid();

                _retryer.RetryOn<ConcurrencyException>(() =>
                {
                    var eventsFromThisUnitOfWork = InnerProcessCommand(command).ToList();

                    // if command processing yielded no events, there's no more work to do
                    if (!eventsFromThisUnitOfWork.Any()) return;

                    // first: save the events
                    _logger.Debug("Saving batch {0} with {1} events", batchId, eventsFromThisUnitOfWork.Count);
                    _eventStore.Save(batchId, eventsFromThisUnitOfWork);

                    emittedDomainEvents.AddRange(eventsFromThisUnitOfWork);
                }, maxRetries: Options.MaxRetries);
            }
            catch (Exception exception)
            {
                // ordinary re-throw if exception is a domain exception
                if (Options.DomainExceptionTypes.Contains(exception.GetType()))
                {
                    throw;
                }

                throw CommandProcessingException.Create(command, exception);
            }

            try
            {
                _logger.Debug("Delivering {0} events to the dispatcher", emittedDomainEvents.Count);

                // when we come to this place, we deliver the events to the view manager
                _eventDispatcher.Dispatch(_eventStore, emittedDomainEvents);
            }
            catch (Exception exception)
            {
                var message =
                    string.Format(
                        "An error ocurred while dispatching events with global sequence numbers {0} to event dispatcher." +
                        " The events were properly saved in the event store, but you might need to re-initialize the" +
                        " event dispatcher",
                        string.Join(", ", emittedDomainEvents.Select(e => e.GetGlobalSequenceNumber())));

                throw new ApplicationException(message, exception);
            }

            return emittedDomainEvents.Any() 
                ? CommandProcessingResult.WithNewPosition(emittedDomainEvents.Max(e => e.GetGlobalSequenceNumber())) 
                : CommandProcessingResult.NoEvents();
        }

        IEnumerable<DomainEvent> InnerProcessCommand(Command command)
        {
            var unitOfWork = new RealUnitOfWork();

            command.Execute(new DefaultCommandContext(unitOfWork, _aggregateRootRepository));

            var emittedEvents = unitOfWork.EmittedEvents.ToList();

            if (!emittedEvents.Any()) return emittedEvents;

            foreach (var e in emittedEvents)
            {
                e.Meta.Merge(command.Meta);
            }

            return emittedEvents;
        }

        internal event Action Disposed = delegate { };

        bool _disposed;

        ~CommandProcessor()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _logger.Info("Disposing command processor");

                Disposed();
            }

            _disposed = true;
        }
    }
}