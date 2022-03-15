using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace gs.sharp.transceiver
{
    public enum TransceiveType
    {
        Bidirectional,
        ReceiveOnly,
        SendOnly
    }

    /// <summary>
    /// Interface for a game state transceiver, understanding local
    /// and remote updates, and allowing the renderable result to be
    /// set. Expected to be managed from a <see cref="GameStateManager"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IGameStateTransceiver<T>
    {
        /// <summary>
        /// Log callback.
        /// </summary>
        event EventHandler<LogEventArgs> Log;

        /// <summary>
        /// A provided local update.
        /// </summary>
        T Local { set; }

        /// <summary>
        /// Represents a remote update.
        /// </summary>
        T Remote { set; }

        /// <summary>
        /// The update you should render at the moment
        /// this is called. Don't cache this. Returns null
        /// if nothing to do.
        /// </summary>
        T Render { get; }

        /// <summary>
        /// Mode of operation.
        /// </summary>
        TransceiveType Type { get; }

        /// <summary>
        /// Fires when this transceiver wants to send a message.
        /// </summary>
        event EventHandler<T> MessageToSend;

        /// <summary>
        /// Retransmit if appropriate.
        /// </summary>
        /// <returns>True if retransmitted.</returns>
        bool Retransmit();
    }

    public interface IRetransmitReasons
    {
        void YesExpiredRemote();
        void YesNoRemote();
        void YesNewerLocal();
        void NoNoLocal();
        void NoRecentRemote();
    }

    /// <summary>
    /// Base implementation of a <see cref="IGameStateTransceiver{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class GameStateMesageTransceiver<T> : IGameStateTransceiver<T>
        where T : IMessage
    {
        /// <inheritdoc/>
        public event EventHandler<T> MessageToSend;
        /// <inheritdoc/>
        public event EventHandler<LogEventArgs> Log;
        /// <inheritdoc/>
        public TransceiveType Type { get; }

        /// <inheritdoc/>
        public T Local
        {
            set
            {
                // Receive only should not ever set Local.
                if (Type == TransceiveType.ReceiveOnly)
                    throw new InvalidOperationException("Receive only should not set Local");

                // Local updates shouldn't be in the future.
                var now = DateTimeOffset.UtcNow;
                if (value.Timestamp > now)
                {
                    throw new ArgumentException(
                        $"Local updates shouldn't be in the future. Now: {now}, New: {value.Timestamp}");
                }

                lock (_localLock)
                {
                    // Only allow local updates to go forward in time.
                    if (!IsDefault(_local) && _local.Timestamp > value.Timestamp)
                    {
                        throw new ArgumentException($"Local updates must move forward in time. Existing: {_local.Timestamp}, New: {value.Timestamp}", nameof(value));
                    }

                    // Update.
                    _local = _lastLocal = value;
                }
                DoLog(LogType.Debug, $"[{value.ID}] Set local update");

                // Send it.
                MessageToSend?.Invoke(this, value);
            }
        }

        /// <inheritdoc/>
        public T Remote
        {
            set
            {
                if (Type == TransceiveType.SendOnly)
                    throw new InvalidOperationException("Send only should not set Remote");
                lock (_remoteLock)
                {
                    _lastUpdateReceived = DateTime.UtcNow;
                    _remote = _lastRemote = (T)value;
                }
                DoLog(LogType.Debug, $"[{value.ID}] Received remote update");
            }
        }

        /// <inheritdoc/>
        public T Render
        {
            get
            {
                lock (_localLock)
                {
                    lock (_remoteLock)
                    {
                        T result;
                        switch (Type)
                        {
                            case TransceiveType.SendOnly:
                                result = _local;
                                break;
                            case TransceiveType.ReceiveOnly:
                                result = _remote;
                                break;
                            case TransceiveType.Bidirectional:
                                {
                                    // Priority for the non null value.
                                    if (IsDefault(_local) && IsDefault(_remote))
                                    {
                                        // If local and remote are empty, there's nothing to do.
                                        result = default;
                                    }
                                    else if (!IsDefault(_local) && IsDefault(_remote))
                                    {
                                        // If local has data, but remote doesn't, use local.
                                        DoLog(LogType.Debug, $"[{_local.ID}] Rendered local as no remote update seen");
                                        result = _local;
                                    }
                                    else if (!IsDefault(_remote) && IsDefault(_local))
                                    {
                                        // If remote has data, but local doesn't, use remote.
                                        DoLog(LogType.Debug, $"[{_remote.ID}] Rendered remote as no local update seen");
                                        result = _remote;
                                    }
                                    else
                                    {
                                        // Both have data, so we take the newest.
                                        Debug.Assert(!IsDefault(_local), nameof(_local) + " != null");
                                        Debug.Assert(!IsDefault(_remote), nameof(_remote) + " != null");
                                        if (_local.Timestamp >= _remote.Timestamp)
                                        {
                                            DoLog(LogType.Debug, $"[{_local.ID}] Rendered local as newer");
                                            result = _local;
                                        }
                                        else
                                        {
                                            DoLog(LogType.Debug, $"[{_remote.ID}] Rendered remote as newer");
                                            result = _remote;
                                        }
                                    }
                                    break;
                                }
                            default:
                                throw new InvalidOperationException("Unsupported mode of operation");
                        }

                        // Remove old data, return the result.
                        _local = default;
                        _remote = default;
                        return result;
                    }
                }
            }
        }

        // Data members.
        private T _local;
        private T _remote;

        // Retransmit members.
        private T _lastLocal;
        private T _lastRemote;
        private DateTime? _lastUpdateReceived;
        private DateTime? _lastRetransmitCheck;

        // Internals.
        private readonly int _expiryMs;
        private readonly bool _debugging;
        private readonly IRetransmitReasons _reasons;

        // Locks.
        private readonly object _localLock = new object();
        private readonly object _remoteLock = new object();

        /// <summary>
        /// Create a new transceiver.
        /// </summary>
        /// <param name="expiryMs">Time in milliseconds after which updates
        /// should be considered expired.</param>
        /// <param name="debugging">Try to log at the debug level.</param>
        public GameStateMesageTransceiver(int expiryMs, bool debugging = false, IRetransmitReasons retransmitLog = null, TransceiveType type = TransceiveType.Bidirectional)
        {
            _expiryMs = expiryMs;
            _debugging = debugging;
            _reasons = retransmitLog;
            Type = type;
        }

        /// <inheritdoc/>
        public bool Retransmit()
        {
            // Do the retransmit if appropriate.
            var retransmitted = false;
            if (ShouldRetransmit())
            {
                lock (_localLock)
                {
                    if (!IsDefault(_lastLocal))
                    {
                        DoLog(LogType.Debug, $"[{_lastLocal.ID}] Retransmitting");
                        MessageToSend?.Invoke(this, _lastLocal);
                        retransmitted = true;
                    }
                }
            }

            // Record the point at which we checked for retransmission.
            _lastRetransmitCheck = DateTime.UtcNow;
            return retransmitted;
        }

        private bool ShouldRetransmit()
        {
            // Some modes override retransmit behaviour.
            switch (Type)
            {
                case TransceiveType.ReceiveOnly:
                    DoLog(LogType.Debug, $"Receiver never retransmits");
                    return false;
                case TransceiveType.SendOnly:
                    DoLog(LogType.Debug, $"Sender always retransmits");
                    return true;
            }

            var expired = DateTime.UtcNow.Subtract(TimeSpan.FromMilliseconds(_expiryMs));

            if (_lastRetransmitCheck == null)
            {
                // In this case, we'll wait one cycle for any remote updates to land.
                DoLog(LogType.Debug, $"Waiting before retransmitting");
                return false;
            }

            lock (_localLock)
            {
                lock (_remoteLock)
                {
                    // If there is an expired remote update, we might take it over.
                    if (_lastUpdateReceived < expired)
                    {
                        Debug.Assert(_lastUpdateReceived.HasValue);
                        Debug.Assert(!IsDefault(_lastRemote));
                        if (IsDefault(_lastLocal) || _lastLocal.Timestamp < _lastRemote.Timestamp)
                        {
                            // Take it over.
                            _reasons?.YesExpiredRemote();
                            DoLog(LogType.Debug, $"Retransmitting (expired remote update)");
                            _local = _lastLocal = _lastRemote;
                            _lastRemote = default;
                            _lastUpdateReceived = null;
                            return true;
                        }
                    }

                    // If there's no local at this point, there's nothing to do.
                    if (IsDefault(_lastLocal))
                    {
                        _reasons?.NoNoLocal();
                        return false;
                    }

                    // From here we do have a local update.
                    Debug.Assert(!IsDefault(_lastLocal));

                    if (IsDefault(_lastRemote))
                    {
                        // There's a local but no remote, assume responsibility.
                        _reasons?.YesNoRemote();
                        DoLog(LogType.Debug, $"Retransmitting (no remote)");
                        return true;
                    }

                    // Cases where we have both.
                    Debug.Assert(!IsDefault(_lastLocal));
                    Debug.Assert(!IsDefault(_lastRemote));
                    Debug.Assert(_lastUpdateReceived.HasValue);

                    // Standard contention case: If the local update is more recent, assume responsibility.
                    // TODO: Shouldn't this compare to when we RECEIVED the last local? Latency comes in here.
                    if (_lastLocal.Timestamp > _lastRemote.Timestamp)
                    {
                        _reasons?.YesNewerLocal();
                        DoLog(LogType.Debug, $"Retransmitting (local newer {_lastLocal.Timestamp} > {_lastRemote.Timestamp})");
                        return true;
                    }

                    if (_lastRemote.Timestamp >= _lastLocal.Timestamp)
                    {
                        // We got a recent remote update so it's not our responsibility.
                        _reasons?.NoRecentRemote();
                        DoLog(LogType.Debug, $"Not retransmitting (remote newer {_lastRemote.Timestamp} >= {_lastLocal.Timestamp})");
                        return false;
                    }
                }
            }
            throw new InvalidOperationException("Could not resolve retransmit state");
        }

        private void DoLog(LogType level, string message)
        {
            if (level == LogType.Debug && !_debugging) return;
            Log?.Invoke(this, new LogEventArgs() { LogType = level, Message = message });
        }

        /// <summary>
        /// Boxless comparison to "empty".
        /// </summary>
        /// <param name="value">Value</param>
        /// <returns>True for empty/null/default, false if a reasonable value.</returns>
        private static bool IsDefault(T value) => EqualityComparer<T>.Default.Equals(value, default);
    }
}
