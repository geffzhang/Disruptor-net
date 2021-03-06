﻿using System;
using System.Collections.Generic;
using System.Threading;
using Disruptor.Dsl;
using Disruptor.Tests.Dsl.Stubs;
using Disruptor.Tests.Support;
using NUnit.Framework;

#pragma warning disable 618

namespace Disruptor.Tests.Dsl
{
    [TestFixture]
    public class DisruptorTests
    {
        private const int _timeoutInSeconds = 2;
        private Disruptor<TestEvent> _disruptor;
        private StubExecutor _executor;
        private List<DelayedEventHandler> _delayedEventHandlers;
        private List<TestWorkHandler> _testWorkHandlers;
        private RingBuffer<TestEvent> _ringBuffer;
        private TestEvent _lastPublishedEvent;

        [SetUp]
        public void SetUp()
        {
            _lastPublishedEvent = null;
            _ringBuffer = null;
            _delayedEventHandlers = new List<DelayedEventHandler>();
            _testWorkHandlers = new List<TestWorkHandler>();
            _executor = new StubExecutor();
            _disruptor = new Disruptor<TestEvent>(() => new TestEvent(), 4, _executor);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var delayedEventHandler in _delayedEventHandlers)
            {
                delayedEventHandler.StopWaiting();
            }
            foreach (var testWorkHandler in _testWorkHandlers)
            {
                testWorkHandler.StopWaiting();
            }

            _disruptor.Halt();
            _executor.JoinAllThreads();
        }

        [Test]
        public void ShouldProcessMessagesPublishedBeforeStartIsCalled()
        {
            var eventCounter = new CountdownEvent(2);
            _disruptor.HandleEventsWith(new ActionEventHandler<TestEvent>(e => eventCounter.Signal()));

            _disruptor.PublishEvent(new ActionEventTranslator<TestEvent>(e => _lastPublishedEvent = e));

            _disruptor.Start();

            _disruptor.PublishEvent(new ActionEventTranslator<TestEvent>(e => _lastPublishedEvent = e));

            if (!eventCounter.Wait(TimeSpan.FromSeconds(5)))
                Assert.Fail("Did not process event published before start was called. Missed events: " + eventCounter.CurrentCount);
        }

        [Test]
        public void ShouldAddEventProcessorsAfterPublishing()
        {
            var rb = _disruptor.RingBuffer;
            var b1 = BatchEventProcessorFactory.Create(rb, rb.NewBarrier(), new SleepingEventHandler());
            var b2 = BatchEventProcessorFactory.Create(rb, rb.NewBarrier(b1.Sequence), new SleepingEventHandler());
            var b3 = BatchEventProcessorFactory.Create(rb, rb.NewBarrier(b2.Sequence), new SleepingEventHandler());

            Assert.That(b1.Sequence.Value, Is.EqualTo(-1L));
            Assert.That(b2.Sequence.Value, Is.EqualTo(-1L));
            Assert.That(b3.Sequence.Value, Is.EqualTo(-1L));

            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());

            _disruptor.HandleEventsWith(b1, b2, b3);

            Assert.That(b1.Sequence.Value, Is.EqualTo(5L));
            Assert.That(b2.Sequence.Value, Is.EqualTo(5L));
            Assert.That(b3.Sequence.Value, Is.EqualTo(5L));
        }

        [Test]
        public void ShouldSetSequenceForHandlerIfAddedAfterPublish()
        {
            var rb = _disruptor.RingBuffer;
            var b1 = new SleepingEventHandler();
            var b2 = new SleepingEventHandler();
            var b3 = new SleepingEventHandler();

            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());

            _disruptor.HandleEventsWith(b1, b2, b3);

            Assert.That(_disruptor.GetSequenceValueFor(b1), Is.EqualTo(5L));
            Assert.That(_disruptor.GetSequenceValueFor(b2), Is.EqualTo(5L));
            Assert.That(_disruptor.GetSequenceValueFor(b3), Is.EqualTo(5L));
        }

        [Test]
        public void ShouldSetSequenceForWorkProcessorIfAddedAfterPublish()
        {
            var rb = _disruptor.RingBuffer;
            var wh1 = CreateTestWorkHandler();
            var wh2 = CreateTestWorkHandler();
            var wh3 = CreateTestWorkHandler();

            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());
            rb.Publish(rb.Next());

            _disruptor.HandleEventsWithWorkerPool(wh1, wh2, wh3);

            Assert.That(_disruptor.RingBuffer.GetMinimumGatingSequence(), Is.EqualTo(5L));
        }

        [Test]
        public void ShouldCreateEventProcessorGroupForFirstEventProcessors()
        {
            _executor.IgnoreExecutions();
            IEventHandler<TestEvent> eventHandler1 = new SleepingEventHandler();
            IEventHandler<TestEvent> eventHandler2 = new SleepingEventHandler();

            var eventHandlerGroup =
                _disruptor.HandleEventsWith(eventHandler1, eventHandler2);
            _disruptor.Start();

            Assert.IsNotNull(eventHandlerGroup);
            Assert.That(_executor.GetExecutionCount(), Is.EqualTo(2));
        }

        [Test]
        public void ShouldMakeEntriesAvailableToFirstHandlersImmediately()
        {
            var countDownLatch = new CountdownEvent(2);
            IEventHandler<TestEvent> eventHandler = new EventHandlerStub<TestEvent>(countDownLatch);

            _disruptor.HandleEventsWith(CreateDelayedEventHandler(), eventHandler);

            EnsureTwoEventsProcessedAccordingToDependencies(countDownLatch);
        }

        [Test]
        public void ShouldWaitUntilAllFirstEventProcessorsProcessEventBeforeMakingItAvailableToDependentEventProcessors()
        {
            var eventHandler1 = CreateDelayedEventHandler();

            var countDownLatch = new CountdownEvent(2);
            IEventHandler<TestEvent> eventHandler2 = new EventHandlerStub<TestEvent>(countDownLatch);

            _disruptor.HandleEventsWith(eventHandler1).Then(eventHandler2);

            EnsureTwoEventsProcessedAccordingToDependencies(countDownLatch, eventHandler1);
        }

        [Test]
        public void ShouldAllowSpecifyingSpecificEventProcessorsToWaitFor()
        {
            var handler1 = CreateDelayedEventHandler();
            var handler2 = CreateDelayedEventHandler();

            var countDownLatch = new CountdownEvent(2);
            IEventHandler<TestEvent> handlerWithBarrier = new EventHandlerStub<TestEvent>(countDownLatch);

            _disruptor.HandleEventsWith(handler1, handler2);
            _disruptor.After(handler1, handler2).HandleEventsWith(handlerWithBarrier);

            EnsureTwoEventsProcessedAccordingToDependencies(countDownLatch, handler1, handler2);
        }

        [Test]
        public void ShouldWaitOnAllProducersJoinedByAnd()

        {
            var handler1 = CreateDelayedEventHandler();
            var handler2 = CreateDelayedEventHandler();

            var countDownLatch = new CountdownEvent(2);
            IEventHandler<TestEvent> handlerWithBarrier = new EventHandlerStub<TestEvent>(countDownLatch);

            _disruptor.HandleEventsWith(handler1);
            var handler2Group = _disruptor.HandleEventsWith(handler2);
            _disruptor.After(handler1).And(handler2Group).HandleEventsWith(handlerWithBarrier);

            EnsureTwoEventsProcessedAccordingToDependencies(countDownLatch, handler1, handler2);
        }

        [Test]
        public void ShouldThrowExceptionIfHandlerIsNotAlreadyConsuming()
        {
            Assert.Throws<ArgumentException>(() => _disruptor.After(CreateDelayedEventHandler()).HandleEventsWith(CreateDelayedEventHandler()));
        }

        [Test]
        public void ShouldTrackEventHandlersByIdentityNotEquality()
        {
            var handler1 = new EvilEqualsEventHandler();
            var handler2 = new EvilEqualsEventHandler();

            _disruptor.HandleEventsWith(handler1);

            // handler2.equals(handler1) but it hasn't yet been registered so should throw exception.
            Assert.Throws<ArgumentException>(() => _disruptor.After(handler2));
        }

        [Test]
        public void ShouldSupportSpecifyingAExceptionHandlerForEventProcessors()
        {
            var eventHandled = new AtomicReference<Exception>();
            IExceptionHandler<object> exceptionHandler = new StubExceptionHandler(eventHandled);
            var testException = new Exception();
            var handler = new ExceptionThrowingEventHandler(testException);

            _disruptor.HandleExceptionsWith(exceptionHandler);
            _disruptor.HandleEventsWith(handler);

            PublishEvent();

            var actualException = WaitFor(eventHandled);
            Assert.AreSame(testException, actualException);
        }

        [Test]
        public void ShouldOnlyApplyExceptionsHandlersSpecifiedViaHandleExceptionsWithOnNewEventProcessors()
        {
            var eventHandled = new AtomicReference<Exception>();
            IExceptionHandler<object> exceptionHandler = new StubExceptionHandler(eventHandled);
            var testException = new Exception();
            var handler = new ExceptionThrowingEventHandler(testException);

            _disruptor.HandleExceptionsWith(exceptionHandler);
            _disruptor.HandleEventsWith(handler);
            _disruptor.HandleExceptionsWith(new FatalExceptionHandler());

            PublishEvent();

            var actualException = WaitFor(eventHandled);
            Assert.AreSame(testException, actualException);
        }

        [Test]
        public void ShouldSupportSpecifyingADefaultExceptionHandlerForEventProcessors()
        {
            var eventHandled = new AtomicReference<Exception>();
            IExceptionHandler<object> exceptionHandler = new StubExceptionHandler(eventHandled);
            var testException = new Exception();
            var handler = new ExceptionThrowingEventHandler(testException);

            _disruptor.SetDefaultExceptionHandler(exceptionHandler);
            _disruptor.HandleEventsWith(handler);

            PublishEvent();

            var actualException = WaitFor(eventHandled);
            Assert.AreSame(testException, actualException);
        }

        [Test]
        public void ShouldApplyDefaultExceptionHandlerToExistingEventProcessors()
        {
            var eventHandled = new AtomicReference<Exception>();
            IExceptionHandler<object> exceptionHandler = new StubExceptionHandler(eventHandled);
            var testException = new Exception();
            var handler = new ExceptionThrowingEventHandler(testException);

            _disruptor.HandleEventsWith(handler);
            _disruptor.SetDefaultExceptionHandler(exceptionHandler);

            PublishEvent();

            var actualException = WaitFor(eventHandled);
            Assert.AreSame(testException, actualException);
        }

        [Test]
        public void ShouldBlockProducerUntilAllEventProcessorsHaveAdvanced()
        {
            var delayedEventHandler = CreateDelayedEventHandler();
            _disruptor.HandleEventsWith(delayedEventHandler);

            var ringBuffer = _disruptor.Start();
            delayedEventHandler.AwaitStart();

            var stubPublisher = new StubPublisher(ringBuffer);
            try
            {
                _executor.Execute(() => stubPublisher.Run());

                AssertProducerReaches(stubPublisher, 4, true);

                delayedEventHandler.ProcessEvent();
                delayedEventHandler.ProcessEvent();
                delayedEventHandler.ProcessEvent();
                delayedEventHandler.ProcessEvent();
                delayedEventHandler.ProcessEvent();

                AssertProducerReaches(stubPublisher, 5, false);
            }
            finally
            {
                stubPublisher.Halt();
            }
        }

        [Test]
        public void ShouldBeAbleToOverrideTheExceptionHandlerForAEventProcessor()
        {
            var testException = new Exception();
            var eventHandler = new ExceptionThrowingEventHandler(testException);
            _disruptor.HandleEventsWith(eventHandler);

            var reference = new AtomicReference<Exception>();
            var exceptionHandler = new StubExceptionHandler(reference);
            _disruptor.HandleExceptionsFor(eventHandler).With(exceptionHandler);

            PublishEvent();

            WaitFor(reference);
        }

        [Test]
        public void ShouldThrowExceptionWhenAddingEventProcessorsAfterTheProducerBarrierHasBeenCreated()
        {
            _executor.IgnoreExecutions();
            _disruptor.HandleEventsWith(new SleepingEventHandler());
            _disruptor.Start();

            Assert.Throws<InvalidOperationException>(() => _disruptor.HandleEventsWith(new SleepingEventHandler()));
        }

        [Test]
        public void ShouldThrowExceptionIfStartIsCalledTwice()
        {
            _executor.IgnoreExecutions();
            _disruptor.HandleEventsWith(new SleepingEventHandler());
            _disruptor.Start();

            Assert.Throws<InvalidOperationException>(() => _disruptor.Start());
        }

        [Test]
        public void ShouldSupportCustomProcessorsAsDependencies()
        {
            var ringBuffer = _disruptor.RingBuffer;

            var delayedEventHandler = CreateDelayedEventHandler();

            var countDownLatch = new CountdownEvent(2);
            IEventHandler<TestEvent> handlerWithBarrier = new EventHandlerStub<TestEvent>(countDownLatch);

            var processor = BatchEventProcessorFactory.Create(ringBuffer, ringBuffer.NewBarrier(), delayedEventHandler);
            _disruptor.HandleEventsWith(processor).Then(handlerWithBarrier);

            EnsureTwoEventsProcessedAccordingToDependencies(countDownLatch, delayedEventHandler);
        }

        [Test]
        public void ShouldSupportHandlersAsDependenciesToCustomProcessors()
        {
            var delayedEventHandler = CreateDelayedEventHandler();
            _disruptor.HandleEventsWith(delayedEventHandler);

            var ringBuffer = _disruptor.RingBuffer;
            var countDownLatch = new CountdownEvent(2);
            IEventHandler<TestEvent> handlerWithBarrier = new EventHandlerStub<TestEvent>(countDownLatch);

            var sequenceBarrier = _disruptor.After(delayedEventHandler).AsSequenceBarrier();
            var processor = BatchEventProcessorFactory.Create(ringBuffer, sequenceBarrier, handlerWithBarrier);
            _disruptor.HandleEventsWith(processor);

            EnsureTwoEventsProcessedAccordingToDependencies(countDownLatch, delayedEventHandler);
        }

        [Test]
        public void ShouldSupportCustomProcessorsAndHandlersAsDependencies()
        {
            var delayedEventHandler1 = CreateDelayedEventHandler();
            var delayedEventHandler2 = CreateDelayedEventHandler();
            _disruptor.HandleEventsWith(delayedEventHandler1);

            var ringBuffer = _disruptor.RingBuffer;
            var countDownLatch = new CountdownEvent(2);
            IEventHandler<TestEvent> handlerWithBarrier = new EventHandlerStub<TestEvent>(countDownLatch);

            var sequenceBarrier = _disruptor.After(delayedEventHandler1).AsSequenceBarrier();
            var processor = BatchEventProcessorFactory.Create(ringBuffer, sequenceBarrier, delayedEventHandler2);

            _disruptor.After(delayedEventHandler1).And(processor).HandleEventsWith(handlerWithBarrier);

            EnsureTwoEventsProcessedAccordingToDependencies(countDownLatch, delayedEventHandler1, delayedEventHandler2);
        }

        [Test]
        public void ShouldProvideEventsToWorkHandlers()
        {
            var workHandler1 = CreateTestWorkHandler();
            var workHandler2 = CreateTestWorkHandler();
            _disruptor.HandleEventsWithWorkerPool(workHandler1, workHandler2);

            PublishEvent();
            PublishEvent();

            workHandler1.ProcessEvent();
            workHandler2.ProcessEvent();
        }

        [Test]
        public void ShouldProvideEventsMultipleWorkHandlers()
        {
            var workHandler1 = CreateTestWorkHandler();
            var workHandler2 = CreateTestWorkHandler();
            var workHandler3 = CreateTestWorkHandler();
            var workHandler4 = CreateTestWorkHandler();
            var workHandler5 = CreateTestWorkHandler();
            var workHandler6 = CreateTestWorkHandler();
            var workHandler7 = CreateTestWorkHandler();
            var workHandler8 = CreateTestWorkHandler();

            _disruptor
                .HandleEventsWithWorkerPool(workHandler1, workHandler2)
                .ThenHandleEventsWithWorkerPool(workHandler3, workHandler4);
            _disruptor
                .HandleEventsWithWorkerPool(workHandler5, workHandler6)
                .ThenHandleEventsWithWorkerPool(workHandler7, workHandler8);
        }

        [Test]
        public void ShouldSupportUsingWorkerPoolAsDependency()
        {
            var workHandler1 = CreateTestWorkHandler();
            var workHandler2 = CreateTestWorkHandler();
            var delayedEventHandler = CreateDelayedEventHandler();
            _disruptor.HandleEventsWithWorkerPool(workHandler1, workHandler2).Then(delayedEventHandler);

            PublishEvent();
            PublishEvent();

            Assert.That(_disruptor.GetBarrierFor(delayedEventHandler).Cursor, Is.EqualTo(-1L));

            workHandler2.ProcessEvent();
            workHandler1.ProcessEvent();

            delayedEventHandler.ProcessEvent();
        }

        [Test]
        public void ShouldSupportUsingWorkerPoolAsDependencyAndProcessFirstEventAsSoonAsItIsAvailable()
        {
            var workHandler1 = CreateTestWorkHandler();
            var workHandler2 = CreateTestWorkHandler();
            var delayedEventHandler = CreateDelayedEventHandler();
            _disruptor.HandleEventsWithWorkerPool(workHandler1, workHandler2).Then(delayedEventHandler);

            PublishEvent();
            PublishEvent();

            workHandler1.ProcessEvent();
            delayedEventHandler.ProcessEvent();

            workHandler2.ProcessEvent();
            delayedEventHandler.ProcessEvent();
        }

        [Test]
        public void ShouldSupportUsingWorkerPoolWithADependency()
        {
            var workHandler1 = CreateTestWorkHandler();
            var workHandler2 = CreateTestWorkHandler();
            var delayedEventHandler = CreateDelayedEventHandler();
            _disruptor.HandleEventsWith(delayedEventHandler).ThenHandleEventsWithWorkerPool(workHandler1, workHandler2);

            PublishEvent();
            PublishEvent();

            delayedEventHandler.ProcessEvent();
            delayedEventHandler.ProcessEvent();

            workHandler1.ProcessEvent();
            workHandler2.ProcessEvent();
        }

        [Test]
        public void ShouldSupportCombiningWorkerPoolWithEventHandlerAsDependencyWhenNotPreviouslyRegistered()

        {
            var workHandler1 = CreateTestWorkHandler();
            var delayedEventHandler1 = CreateDelayedEventHandler();
            var delayedEventHandler2 = CreateDelayedEventHandler();
            _disruptor.HandleEventsWith(delayedEventHandler1).And(_disruptor.HandleEventsWithWorkerPool(workHandler1)).Then(
                delayedEventHandler2);

            PublishEvent();
            PublishEvent();

            delayedEventHandler1.ProcessEvent();
            delayedEventHandler1.ProcessEvent();

            workHandler1.ProcessEvent();
            delayedEventHandler2.ProcessEvent();

            workHandler1.ProcessEvent();
            delayedEventHandler2.ProcessEvent();
        }

        [Test]
        public void ShouldThrowTimeoutExceptionIfShutdownDoesNotCompleteNormally()
        {
            //Given
            var delayedEventHandler = CreateDelayedEventHandler();
            _disruptor.HandleEventsWith(delayedEventHandler);
            PublishEvent();

            //When
            Assert.Throws<TimeoutException>(() => _disruptor.Shutdown(TimeSpan.FromSeconds(1)));
        }

        [Test]
        public void ShouldTrackRemainingCapacity()
        {
            long[] remainingCapacity = { -1 };
            //Given
            IEventHandler<TestEvent> eventHandler = new TempEventHandler(_disruptor, remainingCapacity);

            _disruptor.HandleEventsWith(eventHandler);

            //When
            PublishEvent();

            //Then
            while (remainingCapacity[0] == -1)
            {
                Thread.Sleep(100);
            }
            Assert.That(remainingCapacity[0], Is.EqualTo(_ringBuffer.BufferSize - 1L));
            Assert.That(_disruptor.RingBuffer.GetRemainingCapacity(), Is.EqualTo(_ringBuffer.BufferSize - 0L));
        }

        private class TempEventHandler : IEventHandler<TestEvent>
        {
            private readonly Disruptor<TestEvent> _disruptor;
            private readonly long[] _remainingCapacity;

            public TempEventHandler(Disruptor<TestEvent> disruptor, long[] remainingCapacity)
            {
                _disruptor = disruptor;
                _remainingCapacity = remainingCapacity;
            }

            public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
            {
                _remainingCapacity[0] = _disruptor.RingBuffer.GetRemainingCapacity();
            }
        }

        [Test]
        public void ShouldAllowEventHandlerWithSuperType()
        {
            var latch = new CountdownEvent(2);
            IEventHandler<object> objectHandler = new EventHandlerStub<object>(latch);

            _disruptor.HandleEventsWith(objectHandler);

            EnsureTwoEventsProcessedAccordingToDependencies(latch);
        }

        [Test]
        public void ShouldAllowChainingEventHandlersWithSuperType()
        {
            var latch = new CountdownEvent(2);
            var delayedEventHandler = CreateDelayedEventHandler();
            IEventHandler<object> objectHandler = new EventHandlerStub<object>(latch);

            _disruptor.HandleEventsWith(delayedEventHandler).Then(objectHandler);

            EnsureTwoEventsProcessedAccordingToDependencies(latch, delayedEventHandler);
        }

        [Test]
        public void ShouldMakeEntriesAvailableToFirstCustomProcessorsImmediately()
        {
            var countDownLatch = new CountdownEvent(2);
            IEventHandler<TestEvent> eventHandler = new EventHandlerStub<TestEvent>(countDownLatch);

            _disruptor.HandleEventsWith(new EventProcessorFactory(_disruptor, eventHandler, 0));

            EnsureTwoEventsProcessedAccordingToDependencies(countDownLatch);
        }

        private class EventProcessorFactory : IEventProcessorFactory<TestEvent>
        {
            private readonly Disruptor<TestEvent> _disruptor;
            private readonly IEventHandler<TestEvent> _eventHandler;
            private readonly int _sequenceLength;

            public EventProcessorFactory(Disruptor<TestEvent> disruptor, IEventHandler<TestEvent> eventHandler, int sequenceLength)
            {
                _disruptor = disruptor;
                _eventHandler = eventHandler;
                _sequenceLength = sequenceLength;
            }

            public IEventProcessor CreateEventProcessor(RingBuffer<TestEvent> ringBuffer, ISequence[] barrierSequences)
            {
                Assert.AreEqual(_sequenceLength, barrierSequences.Length, "Should not have had any barrier sequences");
                return BatchEventProcessorFactory.Create(_disruptor.RingBuffer, ringBuffer.NewBarrier(barrierSequences), _eventHandler);
            }
        }

        [Test]
        public void ShouldHonourDependenciesForCustomProcessors()
        {
            var countDownLatch = new CountdownEvent(2);
            IEventHandler<TestEvent> eventHandler = new EventHandlerStub<TestEvent>(countDownLatch);
            var delayedEventHandler = CreateDelayedEventHandler();

            _disruptor.HandleEventsWith(delayedEventHandler).Then(new EventProcessorFactory(_disruptor, eventHandler, 1));

            EnsureTwoEventsProcessedAccordingToDependencies(countDownLatch, delayedEventHandler);
        }

        private TestWorkHandler CreateTestWorkHandler()
        {
            var testWorkHandler = new TestWorkHandler();
            _testWorkHandlers.Add(testWorkHandler);
            return testWorkHandler;
        }

        private void EnsureTwoEventsProcessedAccordingToDependencies(CountdownEvent countDownLatch, params DelayedEventHandler[] dependencies)
        {
            PublishEvent();
            PublishEvent();

            foreach (var dependency in dependencies)
            {
                AssertThatCountDownLatchEquals(countDownLatch, 2L);
                dependency.ProcessEvent();
                dependency.ProcessEvent();
            }

            AssertThatCountDownLatchIsZero(countDownLatch);
        }

        private static void AssertProducerReaches(StubPublisher stubPublisher, int expectedPublicationCount, bool strict)
        {
            var loopStart = DateTime.UtcNow;
            while (stubPublisher.GetPublicationCount() < expectedPublicationCount && DateTime.UtcNow - loopStart < TimeSpan.FromMilliseconds(5))
            {
                Thread.Yield();
            }

            if (strict)
            {
                Assert.That(stubPublisher.GetPublicationCount(), Is.EqualTo(expectedPublicationCount));
            }
            else
            {
                var actualPublicationCount = stubPublisher.GetPublicationCount();
                Assert.IsTrue(actualPublicationCount >= expectedPublicationCount, "Producer reached unexpected count. Expected at least " + expectedPublicationCount + " but only reached " + actualPublicationCount);
            }
        }

        private TestEvent PublishEvent()
        {
            if (_ringBuffer == null)
            {
                _ringBuffer = _disruptor.Start();

                foreach (var eventHandler in _delayedEventHandlers)
                {
                    eventHandler.AwaitStart();
                }
            }

            _disruptor.PublishEvent(new EventTranslator(this));

            return _lastPublishedEvent;
        }

        private class EventTranslator : IEventTranslator<TestEvent>
        {
            private readonly DisruptorTests _disruptorTests;

            public EventTranslator(DisruptorTests disruptorTests)
            {
                _disruptorTests = disruptorTests;
            }

            public void TranslateTo(TestEvent eventData, long sequence)
            {
                _disruptorTests._lastPublishedEvent = eventData;
            }
        }

        private static Exception WaitFor(AtomicReference<Exception> reference)
        {
            while (reference.Read() == null)
            {
                Thread.Yield();
            }

            return reference.Read();
        }

        private DelayedEventHandler CreateDelayedEventHandler()
        {
            var delayedEventHandler = new DelayedEventHandler();
            _delayedEventHandlers.Add(delayedEventHandler);
            return delayedEventHandler;
        }

        private void AssertThatCountDownLatchEquals(CountdownEvent countDownLatch, long expectedCountDownValue)
        {
            Assert.That(countDownLatch.CurrentCount, Is.EqualTo(expectedCountDownValue));
        }

        private void AssertThatCountDownLatchIsZero(CountdownEvent countDownLatch)
        {
            var released = countDownLatch.Wait(TimeSpan.FromSeconds(_timeoutInSeconds));
            Assert.IsTrue(released, "Batch handler did not receive entries: " + countDownLatch.CurrentCount);
        }
    }
}
