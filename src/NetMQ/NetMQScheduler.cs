﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using NetMQ.zmq;

namespace NetMQ
{
    public class NetMQScheduler : TaskScheduler, IDisposable
    {
        /// <summary>
        /// True if we own m_poller (that is, it was created within the NetMQScheduler constructor
        /// as opposed to being passed-in from the caller).
        /// </summary>
        private readonly bool m_ownPoller;

        private readonly Poller m_poller;

        private static int s_schedulerCounter;

        private readonly NetMQSocket m_serverSocket;
        private readonly NetMQSocket m_clientSocket;

        private readonly ThreadLocal<bool> m_schedulerThread;

        private readonly ConcurrentQueue<Task> m_tasksQueue;

        private readonly object m_syncObject;

        private EventHandler<NetMQSocketEventArgs> m_currentMessageHandler;

        /// <summary>
        /// Create a new NetMQScheduler object within the given context, and optionally using the given poller.
        /// </summary>
        /// <param name="context">the NetMQContext to create this NetMQScheduler within</param>
        /// <param name="poller">(optional)the Poller for this Net to use</param>
        public NetMQScheduler([NotNull] NetMQContext context, [CanBeNull] Poller poller = null)
        {
            if (poller == null)
            {
                m_ownPoller = true;
                m_poller = new Poller();
            }
            else
            {
                m_ownPoller = false;
                m_poller = poller;
            }

            m_tasksQueue = new ConcurrentQueue<Task>();
            m_syncObject = new object();

            var schedulerId = Interlocked.Increment(ref s_schedulerCounter);

            var address = string.Format("{0}://scheduler-{1}", Address.InProcProtocol, schedulerId);

            m_serverSocket = context.CreatePullSocket();
            m_serverSocket.Options.Linger = TimeSpan.Zero;
            m_serverSocket.Bind(address);

            m_currentMessageHandler = OnMessageFirstTime;

            m_serverSocket.ReceiveReady += m_currentMessageHandler;

            m_poller.AddSocket(m_serverSocket);

            m_clientSocket = context.CreatePushSocket();
            m_clientSocket.Connect(address);

            m_schedulerThread = new ThreadLocal<bool>(() => false);

            if (m_ownPoller)
            {
                m_poller.PollTillCancelledNonBlocking();
            }
        }

        private void OnMessageFirstTime(object sender, NetMQSocketEventArgs e)
        {
            // set the current thread as the scheduler thread, this only happens the first time a message arrived and is important for the TryExecuteTaskInline
            m_schedulerThread.Value = true;

            // stop calling the OnMessageFirstTime and start calling OnMessage
            m_serverSocket.ReceiveReady -= m_currentMessageHandler;
            m_currentMessageHandler = OnMessage;
            m_serverSocket.ReceiveReady += m_currentMessageHandler;

            OnMessage(sender, e);
        }

        private void OnMessage(object sender, NetMQSocketEventArgs e)
        {
            // remove the awake command from the queue
            m_serverSocket.Receive();

            Task task;

            while (m_tasksQueue.TryDequeue(out task))
            {
                TryExecuteTask(task);
            }
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            return m_schedulerThread.Value && TryExecuteTask(task);
        }

        public override int MaximumConcurrencyLevel
        {
            get { return 1; }
        }

        public void Dispose()
        {
            if (!m_ownPoller && !m_poller.IsStarted)
            {
                DisposeSynced();
                return;
            }

            // disposing on the scheduler thread
            var task = new Task(DisposeSynced);
            task.Start(this);
            task.Wait();

            // poller cannot be stopped from poller thread
            if (m_ownPoller)
            {
                m_poller.CancelAndJoin();
                m_poller.Dispose();
            }
            m_schedulerThread.Dispose();
        }

        private void DisposeSynced()
        {
            Thread.MemoryBarrier();

            m_poller.RemoveSocket(m_serverSocket);

            m_serverSocket.ReceiveReady -= m_currentMessageHandler;

            m_serverSocket.Dispose();
            m_clientSocket.Dispose();
        }

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            // this is not supported, also it's only important for debug propose and doesn't get called in real time
            throw new NotSupportedException();
        }

        protected override void QueueTask(Task task)
        {
            m_tasksQueue.Enqueue(task);

            lock (m_syncObject)
            {
                // awake the scheduler
                m_clientSocket.Send("");
            }
        }
    }
}
