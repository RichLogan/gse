using System;
using System.Diagnostics;

namespace gs.sharp.transceiver
{
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
        public T Local
        {
            set
            {
                // Only allow local updates to go forward in time.
                if (_local.HasValue && _local.Value.Timestamp > value.Timestamp)
                {
                    throw new ArgumentException($"Local updates must move forward in time. Existing: {_local.Value.Timestamp}, New: {value.Timestamp}", nameof(value));
                }

                // Send it.
                MessageToSend?.Invoke(this, value);

                // Update.
                _local = _lastLocal = value;
            }
        }

        /// <inheritdoc/>
        public IMessage Remote
        {
            set
            {
                _lastUpdateReceived = DateTime.UtcNow;
                _remote = _lastRemote = (T)value;
                DoLog(LogType.Debug, $"[{value.ID}] Applied remote update");
            }
        }

        /// <inheritdoc/>
        public T? Render
        {
            get
            {
                // Priority for the non null value.
                T? result;
                if (_local == null && _remote == null)
                {
                    // If local and remote are empty, there's nothing to do.
                    DoLog(LogType.Debug, $"[?] Nothing to render");
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

                // Remove old data, return the result.
                _local = null;
                _remote = null;
                return result;
            }
        }

        // Data members.
        private T? _local = null;
        private T? _remote = null;

        // Retransmit members.
        private T? _lastLocal = null;
        private T? _lastRemote = null;
        private DateTime? _lastUpdateReceived = null;
        private DateTime? _lastRetransmitCheck = null;

        // Internals.
        private readonly int _expiryMs = 0;
        private readonly bool _debugging = false;


        /// <summary>
        /// Create a new transceiver.
        /// </summary>
        /// <param name="expiryMs">Time in milliseconds after which updates
        /// should be considered expired.</param>
        /// <param name="debugging">Try to log at the debug level.</param>
        public GameStateTransceiver(int expiryMs, bool debugging = false)
        {
            _expiryMs = expiryMs;
            _debugging = debugging;
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
                    // Take it over
                    DoLog(LogType.Debug, $"Retransmitting (expired remote update)");
                    _local = _lastLocal = _lastRemote;
                    return true;
                }
            }

            // If there's no local at this point, there's nothing to do.
            if (_lastLocal == null)
            {
                return false;
            }

            // From here we do have a local update.
            Debug.Assert(_lastLocal.HasValue);

            if (_lastRemote == null)
            {
                // There's a local but no remote, assume responsibility.
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
                DoLog(LogType.Debug, $"Retransmitting (local newer {_lastLocal.Value.Timestamp} > {_lastRemote.Value.Timestamp})");
                return true;
            }

            // Otherwise, we got a recent remote update so it's not our responsibility.
            DoLog(LogType.Debug, $"Not retransmitting (recent remote update)");
            return false;
        }

        private void DoLog(LogType level, string message)
        {
            if (level == LogType.Debug && !_debugging) return;
            Log?.Invoke(this, new LogEventArgs() { LogType = level, Message = message });
        }
    }
}
