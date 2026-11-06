using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace RealitySE
{
    /// <summary>
    /// The engine side of automatic error capture. Subscribes to
    /// Application.logMessageReceivedThreaded, AppDomain.UnhandledException
    /// and TaskScheduler.UnobservedTaskException exactly while
    /// <see cref="RavensightClient.IsErrorCaptureActive"/> is true. Each
    /// handler may run on any thread, possibly in parallel, so it only copies
    /// strings into a lock-guarded inbox; the component drains it on the main
    /// thread. The handlers never log, never throw, and observe only: other
    /// subscribers still run, and an unobserved task exception is not marked
    /// observed.
    /// </summary>
    internal sealed class RavensightErrorHooks
    {
        private readonly RavensightErrorInbox _inbox = new RavensightErrorInbox();
        private readonly int _mainThreadId;
        private bool _installed;
        private volatile bool _draining;

        [ThreadStatic] private static bool _inHandler;

        public RavensightErrorHooks()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool Installed { get { return _installed; } }

        /// <summary>Main thread: installs or removes the hooks to match the client, then drains.</summary>
        public void Update(RavensightClient client)
        {
            bool wanted = client != null && client.IsErrorCaptureActive;
            if (wanted && !_installed) Install();
            else if (!wanted && _installed) Uninstall();
            if (!_installed) return;

            _inbox.WantTrail = client.IsErrorTrailActive;
            if (_inbox.Count == 0) return;

            List<RavensightLogEntry> entries = _inbox.Drain();
            // Anything the SDK logs on the main thread while it turns these
            // into events is its own, and is dropped by the handler.
            _draining = true;
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    try
                    {
                        client.ProcessLog(entries[i]);
                    }
                    catch (Exception)
                    {
                        // A failure in the capture path is dropped, never reported through itself.
                    }
                }
            }
            finally
            {
                _draining = false;
            }
        }

        public void Uninstall()
        {
            if (!_installed) return;
            _installed = false;
            _inbox.Accepting = false;
            Application.logMessageReceivedThreaded -= OnLogMessage;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            _inbox.Clear();
        }

        private void Install()
        {
            _installed = true;
            _inbox.Accepting = true;
            Application.logMessageReceivedThreaded += OnLogMessage;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private static RavensightLogLevel LevelOf(LogType type)
        {
            switch (type)
            {
                case LogType.Exception: return RavensightLogLevel.Exception;
                case LogType.Error: return RavensightLogLevel.Error;
                case LogType.Assert: return RavensightLogLevel.Assert;
                case LogType.Warning: return RavensightLogLevel.Warning;
                default: return RavensightLogLevel.Info;
            }
        }

        // Any thread. Copies and returns.
        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (_inHandler) return;
            _inHandler = true;
            try
            {
                if (_draining && Thread.CurrentThread.ManagedThreadId == _mainThreadId) return;
                RavensightLogEntry entry = new RavensightLogEntry();
                entry.Level = LevelOf(type);
                entry.Text = condition;
                entry.Stack = stackTrace;
                _inbox.Offer(entry);
            }
            catch (Exception)
            {
                // Never throw into the engine's logger.
            }
            finally
            {
                _inHandler = false;
            }
        }

        private void OfferException(Exception exception, string source)
        {
            if (exception == null || _inHandler) return;
            _inHandler = true;
            try
            {
                RavensightLogEntry entry = new RavensightLogEntry();
                entry.Level = RavensightLogLevel.Exception;
                entry.Source = source;
                entry.Type = exception.GetType().Name;
                entry.Text = exception.Message;
                entry.Stack = exception.StackTrace;
                _inbox.Offer(entry);
            }
            catch (Exception)
            {
                // Observe only.
            }
            finally
            {
                _inHandler = false;
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            OfferException(args == null ? null : args.ExceptionObject as Exception, RavensightErrorRules.SourceUncaught);
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
        {
            // Observed only: SetObserved is never called, so the runtime's own
            // policy for the exception is unchanged.
            if (args == null) return;
            Exception exception = args.Exception;
            AggregateException aggregate = exception as AggregateException;
            if (aggregate != null && aggregate.InnerExceptions.Count == 1) exception = aggregate.InnerExceptions[0];
            OfferException(exception, RavensightErrorRules.SourceUnhandledRejection);
        }
    }
}
