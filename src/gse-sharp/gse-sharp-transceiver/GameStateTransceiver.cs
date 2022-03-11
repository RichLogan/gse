using System;
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
    /// Sender of <see cref="IMessage"/> messages.
    /// </summary>
    public interface IGameStateSender
    {
        /// <summary>
        /// Fires when this transceiver wants to send a message.
        /// </summary>
        event EventHandler<IMessage> MessageToSend;

        /// <summary>
        /// Retransmit if appropriate.
        /// </summary>
        /// <returns>True if retransmitted.</returns>
        bool Retransmit();
    }

    /// <summary>
    /// Receiver of <see cref="IMessage"/> messages.
    /// </summary>
    public interface IGameStateReceiver
    {
        /// <summary>
        /// Represents a remote update.
        /// </summary>
        IMessage Remote { set; }
    }

    /// <summary>
    /// Transceiver of <see cref="IMessage"/> messages.
    /// </summary>
    public interface IGameStateTransceiver : IGameStateReceiver, IGameStateSender
    {
        /// <summary>
        /// Log callback.
        /// </summary>
        event EventHandler<LogEventArgs> Log;
    }

    /// <summary>
    /// Interface for a game state transceiver, understanding local
    /// and remote updates, and allowing the renderable result to be
    /// set. Expected to be managed from a <see cref="GameStateManager"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IGameStateTransceiver<T> : IGameStateTransceiver where T : struct, IMessage
    {
        /// <summary>
        /// A provided local update.
        /// </summary>
        T Local { set; }

        /// <summary>
        /// The update you should render at the moment
        /// this is called. Don't cache this. Returns null
        /// if nothing to do.
        /// </summary>
        T? Render { get; }

        /// <summary>
        /// Mode of operation.
        /// </summary>
        TransceiveType Type { get; }
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
    public class GameStateTransceiver<T> : IGameStateTransceiver<T> where T : struct, IMessage
    {
        /// <inheritdoc/>
        public event EventHandler<IMessage> MessageToSend;
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

                // Only allow local updates to go forward in time.
                if (_local.HasValue && _local.Value.Timestamp > value.Timestamp)
                {
                    throw new ArgumentException($"Local updates must move forward in time. Existing: {_local.Value.Timestamp}, New: {value.Timestamp}", nameof(value));
                }

                // Send it.
                MessageToSend?.Invoke(this, value);

                // Update.
                _local = _lastLocal = value;
                DoLog(LogType.Debug, $"[{value.ID}] Set local update");
            }
        }

        /// <inheritdoc/>
        public IMessage Remote
        {
            set
            {
                if (Type == TransceiveType.SendOnly)
                    throw new InvalidOperationException("Send only should not set Remote");
                _lastUpdateReceived = DateTime.UtcNow;
                _remote = _lastRemote = (T)value;
                DoLog(LogType.Debug, $"[{value.ID}] Received remote update");
            }
        }

        /// <inheritdoc/>
        public T? Render
        {
            get
            {
                T? result;
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
                            if (_local == null && _remote == null)
                            {
                                // If local and remote are empty, there's nothing to do.
                                result = null;
                            }
                            else if (_local != null && _remote == null)
                            {
                                // If local has data, but remote doesn't, use local.
                                DoLog(LogType.Debug, $"[{_local.Value.ID}] Rendered local as no remote update seen");
                                result = _local;
                            }
                            else if (_remote != null && _local == null)
                            {
                                // If remote has data, but local doesn't, use remote.
                                DoLog(LogType.Debug, $"[{_remote.Value.ID}] Rendered remote as no local update seen");
                                result = _remote;
                            }
                            else
                            {
                                // Both have data, so we take the newest.
                                Debug.Assert(_local != null, nameof(_local) + " != null");
                                Debug.Assert(_remote != null, nameof(_remote) + " != null");
                                if (_local.Value.Timestamp >= _remote.Value.Timestamp)
                                {
                                    DoLog(LogType.Debug, $"[{_local.Value.ID}] Rendered local as newer");
                                    result = _local;
                                }
                                else
                                {
                                    DoLog(LogType.Debug, $"[{_remote.Value.ID}] Rendered remote as newer");
                                    result = _remote;
                                }
                            }
                            break;
                        }
                    default:
                        throw new InvalidOperationException("Unsupported mode of operation");
                }

                // Remove old data, return the result.
                _local = null;
                _remote = null;
                return result;
            }
        }

        // Data members.
        private T? _local;
        private T? _remote;

        // Retransmit members.
        private T? _lastLocal;
        private T? _lastRemote;
        private DateTime? _lastUpdateReceived;
        private DateTime? _lastRetransmitCheck;

        // Internals.
        private readonly int _expiryMs;
        private readonly bool _debugging;
        private readonly IRetransmitReasons _reasons;

        /// <summary>
        /// Create a new transceiver.
        /// </summary>
        /// <param name="expiryMs">Time in milliseconds after which updates
        /// should be considered expired.</param>
        /// <param name="debugging">Try to log at the debug level.</param>
        public GameStateTransceiver(int expiryMs, bool debugging = false, IRetransmitReasons retransmitLog = null, TransceiveType type = TransceiveType.Bidirectional)
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
                if (_lastLocal != null)
                {
                    DoLog(LogType.Debug, $"[{_lastLocal.Value.ID}] Retransmitting");
                    MessageToSend?.Invoke(this, _lastLocal);
                    retransmitted = true;
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

            // If there is an expired remote update, we might take it over.
            if (_lastUpdateReceived < expired)
            {
                Debug.Assert(_lastUpdateReceived.HasValue);
                Debug.Assert(_lastRemote.HasValue);
                if (_lastLocal == null || _lastLocal.Value.Timestamp < _lastRemote.Value.Timestamp)
                {
                    // Take it over.
                    _reasons?.YesExpiredRemote();
                    DoLog(LogType.Debug, $"Retransmitting (expired remote update)");
                    _local = _lastLocal = _lastRemote;
                    _lastRemote = null;
                    _lastUpdateReceived = null;
                    return true;
                }
            }

            // If there's no local at this point, there's nothing to do.
            if (_lastLocal == null)
            {
                _reasons?.NoNoLocal();
                return false;
            }

            // From here we do have a local update.
            Debug.Assert(_lastLocal.HasValue);

            if (_lastRemote == null)
            {
                // There's a local but no remote, assume responsibility.
                _reasons?.YesNoRemote();
                DoLog(LogType.Debug, $"Retransmitting (no remote)");
                return true;
            }

            // Cases where we have both.
            Debug.Assert(_lastLocal.HasValue);
            Debug.Assert(_lastRemote.HasValue);
            Debug.Assert(_lastUpdateReceived.HasValue);

            // Standard contention case: If the local update is more recent, assume responsibility.
            // TODO: Shouldn't this compare to when we RECEIVED the last local? Latency comes in here.
            if (_lastLocal.Value.Timestamp > _lastRemote.Value.Timestamp)
            {
                _reasons?.YesNewerLocal();
                DoLog(LogType.Debug, $"Retransmitting (local newer {_lastLocal.Value.Timestamp} > {_lastRemote.Value.Timestamp})");
                return true;
            }

            if (_lastRemote.Value.Timestamp >= _lastLocal.Value.Timestamp)
            {
                // We got a recent remote update so it's not our responsibility.
                _reasons?.NoRecentRemote();
                DoLog(LogType.Debug, $"Not retransmitting (remote newer {_lastRemote.Value.Timestamp} >= {_lastLocal.Value.Timestamp})");
                return false;
            }

            throw new InvalidOperationException("Could not resolve retransmit state");
        }

        private void DoLog(LogType level, string message)
        {
            if (level == LogType.Debug && !_debugging) return;
            Log?.Invoke(this, new LogEventArgs() { LogType = level, Message = message });
        }
    }
}
