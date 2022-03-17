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

    public readonly struct AuthoredObject
    {
        public readonly GSObject GSObject;
        public readonly uint Author;

        public AuthoredObject(GSObject gsObject, uint author)
        {
            GSObject = gsObject;
            Author = author;
        }
    }

    public interface IGameStateTransceiver
    {
        /// <summary>
        /// Log callback.
        /// </summary>
        event EventHandler<LogEventArgs> Log;

        /// <summary>
        /// Callback when the transceiver wishes to send a message.
        /// </summary>
        event EventHandler<AuthoredObject> MessageToSend;

        /// <summary>
        /// Mode of operation.
        /// </summary>
        /// <value></value>
        TransceiveType Type { get; }

        /// <summary>
        /// Retransmit if appropriate.
        /// </summary>
        /// <returns>True if retransmitted.</returns>
        bool Retransmit();

        /// <summary>
        /// A provided local update.
        /// </summary>
        AuthoredObject Local { set; }

        /// <summary>
        /// Represents a remote update.
        /// </summary>
        AuthoredObject Remote { set; }

        /// <summary>
        /// The update you should render at the moment
        /// this is called. Don't cache this. Returns null
        /// if nothing to do.
        /// </summary>
        AuthoredObject Render { get; }
    }

    public interface IRetransmitReasons
    {
        void YesExpiredRemote();
        void YesNoRemote();
        void YesNewerLocal();
        void NoNoLocal();
        void NoRecentRemote();
    }

    // <summary>
    /// Base implementation of a <see cref="IGameStateTransceiver{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class GameStateTransceiver : IGameStateTransceiver
    {
        private static IMessage IsMessage(AuthoredObject authored)
        {
            var gsObject = authored.GSObject;
            switch (gsObject.Type)
            {
                case (ulong)Tag.Head1:
                    return gsObject.Head1;
                case (ulong)Tag.Hand1:
                    return gsObject.Hand1;
                case (ulong)Tag.Hand2:
                    return gsObject.Hand2;
                case (ulong)Tag.Object1:
                    return gsObject.Object1;
                default:
                    return default;
            }
        }

        private static DateTimeOffset GetTimestamp(AuthoredObject authored) => GetTimestamp(authored.GSObject);

        private static DateTimeOffset GetTimestamp(GSObject gsObject)
        {
            switch (gsObject.Type)
            {
                case (ulong)Tag.Head1:
                    return gsObject.Head1.Timestamp;
                case (ulong)Tag.Hand1:
                    return gsObject.Hand1.Timestamp;
                case (ulong)Tag.Hand2:
                    return gsObject.Hand2.Timestamp;
                case (ulong)Tag.Object1:
                    return gsObject.Object1.Timestamp;
                default:
                    return DateTimeOffset.UtcNow;
            }
        }

        /// <inheritdoc/>
        public event EventHandler<AuthoredObject> MessageToSend;
        /// <inheritdoc/>
        public event EventHandler<LogEventArgs> Log;
        /// <inheritdoc/>
        public TransceiveType Type { get; }

        /// <inheritdoc/>
        public virtual AuthoredObject Local
        {
            set
            {
                // Receive only should not ever set Local.
                if (Type == TransceiveType.ReceiveOnly)
                    throw new InvalidOperationException("Receive only should not set Local");

                // Update.
                var timestamp = GetTimestamp(value);
                var now = DateTime.UtcNow;
                if (timestamp > now)
                {
                    throw new ArgumentException(
                        $"Local updates shouldn't be in the future. Now: {now}, New: {timestamp}");
                }

                lock (_localLock)
                {
                    // Only allow local updates to go forward in time.
                    if (_lastLocalTime > timestamp)
                    {
                        throw new ArgumentException($"Local updates must move forward in time. Existing: {_lastLocalTime}, New: {timestamp}", nameof(value));
                    }
                    _local = _lastLocal = value;
                    _lastLocalTime = timestamp;
                }

                // Send it.
                DoLog(LogType.Debug, "Set local update");
                MessageToSend?.Invoke(this, value);
            }
        }

        /// <inheritdoc/>
        public AuthoredObject Remote
        {
            set
            {
                if (Type == TransceiveType.SendOnly)
                    throw new InvalidOperationException("Send only should not set Remote");
                lock (_remoteLock)
                {
                    _lastUpdateReceived = DateTime.UtcNow;
                    _lastRemoteTime = GetTimestamp(value);
                    _remote = _lastRemote = value;
                }
                DoLog(LogType.Debug, "Received remote update");
            }
        }

        /// <inheritdoc/>
        public virtual AuthoredObject Render
        {
            get
            {
                lock (_localLock)
                {
                    lock (_remoteLock)
                    {
                        AuthoredObject result;
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
                                    if (Default.Is(_local) && Default.Is(_remote))
                                    {
                                        // If local and remote are empty, there's nothing to do.
                                        result = default;
                                    }
                                    else if (!Default.Is(_local) && Default.Is(_remote))
                                    {
                                        // If local has data, but remote doesn't, use local.
                                        DoLog(LogType.Debug, "Rendered local as no remote update seen");
                                        result = _local;
                                    }
                                    else if (!Default.Is(_remote) && Default.Is(_local))
                                    {
                                        // If remote has data, but local doesn't, use remote.
                                        DoLog(LogType.Debug, "Rendered remote as no local update seen");
                                        result = _remote;
                                    }
                                    else
                                    {
                                        // Both have data, so we take most recent set.
                                        Debug.Assert(!Default.Is(_local), nameof(_local) + " != null");
                                        Debug.Assert(!Default.Is(_remote), nameof(_remote) + " != null");
                                        result = _lastRemoteTime > _lastLocalTime ? _remote : _local;
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
        protected AuthoredObject _local;
        protected AuthoredObject _remote;

        // Retransmit members.
        protected AuthoredObject _lastLocal;
        protected AuthoredObject _lastRemote;
        protected DateTimeOffset _lastLocalTime;
        protected DateTimeOffset _lastRemoteTime;
        protected DateTimeOffset _lastUpdateReceived;
        protected DateTime? _lastRetransmitCheck;

        // Internals.
        protected readonly int _expiryMs;
        protected readonly bool _debugging;
        protected readonly IRetransmitReasons _reasons;

        // Locks.
        protected readonly object _localLock = new object();
        protected readonly object _remoteLock = new object();

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
                lock (_localLock)
                {
                    if (!Default.Is(_lastLocal))
                    {
                        DoLog(LogType.Debug, "Retransmitting");
                        MessageToSend?.Invoke(this, _lastLocal);
                        retransmitted = true;
                    }
                }
            }

            // Record the point at which we checked for retransmission.
            _lastRetransmitCheck = DateTime.UtcNow;
            return retransmitted;
        }

        protected virtual bool ShouldRetransmit()
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
                    if (!Default.Is(_lastUpdateReceived) && _lastUpdateReceived < expired)
                    {
                        Debug.Assert(!Default.Is(_lastRemote));
                        if (Default.Is(_lastLocal) || _lastLocalTime < _lastUpdateReceived)
                        {
                            // Take it over.
                            _reasons?.YesExpiredRemote();
                            DoLog(LogType.Debug, $"Retransmitting (expired remote update)");
                            _local = _lastLocal = _lastRemote;
                            _lastLocalTime = GetTimestamp(_lastRemote);
                            _lastRemote = default;
                            _lastRemoteTime = default;
                            _lastUpdateReceived = default;
                            return true;
                        }
                    }

                    // If there's no local at this point, there's nothing to do.
                    if (Default.Is(_lastLocal))
                    {
                        _reasons?.NoNoLocal();
                        return false;
                    }

                    // From here we do have a local update.
                    Debug.Assert(!Default.Is(_lastLocal));

                    if (Default.Is(_lastRemote))
                    {
                        // There's a local but no remote, assume responsibility.
                        _reasons?.YesNoRemote();
                        DoLog(LogType.Debug, $"Retransmitting (no remote)");
                        return true;
                    }

                    // Cases where we have both.
                    Debug.Assert(!Default.Is(_lastLocal));
                    Debug.Assert(!Default.Is(_lastLocalTime));
                    Debug.Assert(!Default.Is(_lastRemote));
                    Debug.Assert(!Default.Is(_lastRemoteTime));
                    Debug.Assert(!Default.Is(_lastUpdateReceived));

                    // Retransmit if local more recently got that remote.
                    if (_lastLocalTime >= _lastRemoteTime)
                    {
                        DoLog(LogType.Debug, $"Retransmitting (local newer {_lastLocalTime} > {_lastRemoteTime})");
                        _reasons?.YesNewerLocal();
                        return true;
                    }

                    if (_lastRemoteTime > _lastLocalTime)
                    {
                        // We got a recent remote update so it's not our responsibility.
                        _reasons?.NoRecentRemote();
                        DoLog(LogType.Debug, $"Not retransmitting (remote newer {_lastRemoteTime} >= {_lastLocalTime})");
                        return false;
                    }
                }
            }
            throw new InvalidOperationException("Could not resolve retransmit state");
        }

        // private bool ShouldRetransmit(IMessage local, IMessage remote)
        // {

        //     lock (_localLock)
        //     {
        //         lock (_remoteLock)
        //         {
        //             // If there is an expired remote update, we might take it over.
        //             if (_lastUpdateReceived < expired)
        //             {
        //                 Debug.Assert(_lastUpdateReceived.HasValue);
        //                 Debug.Assert(!Default.Is(_lastRemote));
        //                 if (Default.Is(_lastLocal) || _lastLocal.Timestamp < _lastRemote.Timestamp)
        //                 {
        //                     // Take it over.
        //                     _reasons?.YesExpiredRemote();
        //                     DoLog(LogType.Debug, $"Retransmitting (expired remote update)");
        //                     _local = _lastLocal = _lastRemote;
        //                     _lastRemote = default;
        //                     _lastUpdateReceived = null;
        //                     return true;
        //                 }
        //             }

        //             // If there's no local at this point, there's nothing to do.
        //             if (Default.Is(_lastLocal))
        //             {
        //                 _reasons?.NoNoLocal();
        //                 return false;
        //             }

        //             // From here we do have a local update.
        //             Debug.Assert(!Default.Is(_lastLocal));

        //             if (Default.Is(_lastRemote))
        //             {
        //                 // There's a local but no remote, assume responsibility.
        //                 _reasons?.YesNoRemote();
        //                 DoLog(LogType.Debug, $"Retransmitting (no remote)");
        //                 return true;
        //             }

        //             // Cases where we have both.
        //             Debug.Assert(!Default.Is(_lastLocal));
        //             Debug.Assert(!Default.Is(_lastRemote));
        //             Debug.Assert(_lastUpdateReceived.HasValue);

        //             // Standard contention case: If the local update is more recent, assume responsibility.
        //             // TODO: Shouldn't this compare to when we RECEIVED the last local? Latency comes in here.
        //             if (_lastLocal.Timestamp > _lastRemote.Timestamp)
        //             {
        //                 _reasons?.YesNewerLocal();
        //                 DoLog(LogType.Debug, $"Retransmitting (local newer {_lastLocal.Timestamp} > {_lastRemote.Timestamp})");
        //                 return true;
        //             }

        //             if (_lastRemote.Timestamp >= _lastLocal.Timestamp)
        //             {
        //                 // We got a recent remote update so it's not our responsibility.
        //                 _reasons?.NoRecentRemote();
        //                 DoLog(LogType.Debug, $"Not retransmitting (remote newer {_lastRemote.Timestamp} >= {_lastLocal.Timestamp})");
        //                 return false;
        //             }
        //         }
        //     }
        //     throw new InvalidOperationException("Could not resolve retransmit state");
        // }

        protected void DoLog(LogType level, string message)
        {
            if (level == LogType.Debug && !_debugging) return;
            Log?.Invoke(this, new LogEventArgs() { LogType = level, Message = message });
        }
    }
}
