﻿using System;
using System.Collections.Generic;
using System.Threading;
using Zeus.CommunicationFramework.Messages;

namespace Zeus.CommunicationFramework.Messengers {

    /// <summary>
    ///     This class is a wrapper for IMessenger and is used
    ///     to synchronize message receiving operation.
    ///     It extends RequestReplyMessenger.
    ///     It is suitable to use in applications those want to receive
    ///     messages by synchronized method calls instead of asynchronous
    ///     MessageReceived event.
    /// </summary>
    public class SynchronizedMessenger<T> : RequestReplyMessenger<T> where T : IMessenger {

        /// <summary>
        ///     This object is used to synchronize/wait threads.
        /// </summary>
        private readonly ManualResetEventSlim _receiveWaiter;

        /// <summary>
        ///     A queue that is used to store receiving messages until Receive(...)
        ///     method is called to get them.
        /// </summary>
        private readonly Queue<IMessage> _receivingMessageQueue;

        /// <summary>
        ///     This boolean value indicates the running state of this class.
        /// </summary>
        private volatile bool _running;

        /// <summary>
        ///     Gets/sets capacity of the incoming message queue.
        ///     No message is received from remote application if
        ///     number of messages in internal queue exceeds this value.
        ///     Default value: int.MaxValue (2147483647).
        /// </summary>
        public int IncomingMessageQueueCapacity { get; set; }

        public SynchronizedMessenger(T messenger)
            : this(messenger, int.MaxValue) {

        }

        public SynchronizedMessenger(T messenger, int incomingMessageQueueCapacity)
            : base(messenger) {
            _receiveWaiter = new ManualResetEventSlim();
            _receivingMessageQueue = new Queue<IMessage>();
            IncomingMessageQueueCapacity = incomingMessageQueueCapacity;
        }

        /// <summary>
        ///     Starts the messenger.
        /// </summary>
        public override void Start() {
            lock (_receivingMessageQueue) {
                _running = true;
            }

            base.Start();
        }

        /// <summary>
        ///     Stops the messenger.
        /// </summary>
        public override void Stop() {
            base.Stop();

            lock (_receivingMessageQueue) {
                _running = false;
                _receiveWaiter.Set();
            }
        }

        /// <summary>
        ///     This method is used to receive a message from remote application.
        ///     It waits until a message is received.
        /// </summary>
        /// <returns>Received message</returns>
        public IMessage ReceiveMessage() {
            return ReceiveMessage(System.Threading.Timeout.Infinite);
        }

        /// <summary>
        ///     This method is used to receive a message from remote application.
        ///     It waits until a message is received or timeout occurs.
        /// </summary>
        /// <param name="timeout">
        ///     Timeout value to wait if no message is received.
        ///     Use -1 to wait indefinitely.
        /// </param>
        /// <returns>Received message</returns>
        /// <exception cref="TimeoutException">Throws TimeoutException if timeout occurs</exception>
        /// <exception cref="Exception">Throws Exception if SynchronizedMessenger stops before a message is received</exception>
        public IMessage ReceiveMessage(int timeout) {
            while (_running) {
                lock (_receivingMessageQueue) {
                    // Check if SynchronizedMessenger is running
                    if (!_running) {
                        throw new Exception("SynchronizedMessenger is stopped. Can not receive message.");
                    }

                    // Get a message immediately if any message does exists
                    if (_receivingMessageQueue.Count > 0) {
                        return _receivingMessageQueue.Dequeue();
                    }

                    _receiveWaiter.Reset();
                }

                // Wait for a message
                var signalled = _receiveWaiter.Wait(timeout);

                // If not signalled, throw exception
                if (!signalled) {
                    throw new TimeoutException("Timeout occured. Can not received any message");
                }
            }

            throw new Exception("SynchronizedMessenger is stopped. Can not receive message.");
        }

        /// <summary>
        ///     This method is used to receive a specific type of message from remote application.
        ///     It waits until a message is received.
        /// </summary>
        /// <returns>Received message</returns>
        public TMessage ReceiveMessage<TMessage>() where TMessage : IMessage {
            return ReceiveMessage<TMessage>(System.Threading.Timeout.Infinite);
        }

        /// <summary>
        ///     This method is used to receive a specific type of message from remote application.
        ///     It waits until a message is received or timeout occurs.
        /// </summary>
        /// <param name="timeout">
        ///     Timeout value to wait if no message is received.
        ///     Use -1 to wait indefinitely.
        /// </param>
        /// <returns>Received message</returns>
        public TMessage ReceiveMessage<TMessage>(int timeout) where TMessage : IMessage {
            var receivedMessage = ReceiveMessage(timeout);
            if (!(receivedMessage is TMessage)) {
                throw new Exception("Unexpected message received." +
                                    " Expected type: " + typeof(TMessage).Name +
                                    ". Received message type: " + receivedMessage.GetType().Name);
            }

            return (TMessage)receivedMessage;
        }

        /// <summary>
        ///     Overrides
        /// </summary>
        /// <param name="message"></param>
        protected override void OnMessageReceived(IMessage message) {
            lock (_receivingMessageQueue) {
                if (_receivingMessageQueue.Count < IncomingMessageQueueCapacity) {
                    _receivingMessageQueue.Enqueue(message);
                }

                _receiveWaiter.Set();
            }
        }

    }

}