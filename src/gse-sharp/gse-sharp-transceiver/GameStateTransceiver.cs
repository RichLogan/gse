using System;
using System.Diagnostics;

namespace gs.sharp.transceiver
{
    public enum Algorithm
    {
        Timestamp,
        Latest
    }

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

        public AuthoredObject(in GSObject gsObject, uint author)
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

    public abstract class BaseGameStateTransceiver : IGameStateTransceiver
    {
        /// <inheritdoc/>
        public event EventHandler<AuthoredObject> MessageToSend;
        /// <inheritdoc/>
        public event EventHandler<LogEventArgs> Log;
        /// <inheritdoc/>
        public TransceiveType Type { get; }

        public abstract AuthoredObject Local { set; }
        public abstract AuthoredObject Remote { set; }
        public abstract AuthoredObject Render { get; }

        // Retransmit members.
        protected AuthoredObject _lastLocal;
        protected DateTime? _lastRetransmitCheck;

        // Internals.
        protected readonly int _expiryMs;
        protected readonly bool _debugging;
        protected readonly IRetransmitReasons _reasons;
        protected readonly bool _prerendered;

        // Locks.
        protected readonly object _localLock = new object();
        protected readonly object _remoteLock = new object();

        /// <summary>
        /// Create a new transceiver.
        /// </summary>
        /// <param name="expiryMs">Time in milliseconds after which updates
        /// should be considered expired.</param>
        /// <param name="debugging">Try to log at the debug level.</param>
        protected BaseGameStateTransceiver(int expiryMs, bool debugging = false, IRetransmitReasons retransmitLog = null, TransceiveType type = TransceiveType.Bidirectional, bool prerendered = false)
        {
            _expiryMs = expiryMs;
            _debugging = debugging;
            _reasons = retransmitLog;
            Type = type;
            _prerendered = prerendered;
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

        protected abstract bool ShouldRetransmit();

        protected void OnMessageToSend(in AuthoredObject toSend) => MessageToSend?.Invoke(this, toSend);

        protected void DoLog(LogType level, string message)
        {
            if (level == LogType.Debug && !_debugging) return;
            Log?.Invoke(this, new LogEventArgs() { LogType = level, Message = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}" });
        }
    }

    /// <summary>
    /// Base implementation of a <see cref="IGameStateTransceiver{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class TimestampedGameStateTransceiver : BaseGameStateTransceiver
    {
        private static DateTimeOffset GetTimestamp(in AuthoredObject authored) => GetTimestamp(authored.GSObject);

        private static DateTimeOffset GetTimestamp(in GSObject gsObject)
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
        public override AuthoredObject Local
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
                        $"Local updates shouldn't be in the future. Now: {now:HH:mm:ss.fff}, New: {timestamp:HH:mm:ss.fff}");
                }

                lock (_localLock)
                {
                    // Only allow local updates to go forward in time.
                    if (_lastLocalTime > timestamp)
                    {
                        throw new ArgumentException($"Local updates must move forward in time. Existing: {_lastLocalTime:HH:mm:ss.fff}, New: {timestamp:HH:mm:ss.fff}", nameof(value));
                    }
                    _local = _lastLocal = value;
                    _lastLocalTime = timestamp;
                }

                // Send it.
                DoLog(LogType.Debug, "Set local update");
                OnMessageToSend(value);
            }
        }

        /// <inheritdoc/>
        public override AuthoredObject Remote
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
        public override AuthoredObject Render
        {
            get
            {
                lock (_localLock)
                {
                    lock (_remoteLock)
                    {
                        AuthoredObject result;
                        bool localResult = false;
                        switch (Type)
                        {
                            case TransceiveType.SendOnly:
                                localResult = true;
                                result = _local;
                                break;
                            case TransceiveType.ReceiveOnly:
                                result = _remote;
                                break;
                            case TransceiveType.Bidirectional:
                                {
                                    var localIsDefault = Default.Is(in _local);
                                    var remoteIsDefault = Default.Is(in _remote);

                                    // Priority for the non null value.
                                    if (localIsDefault && remoteIsDefault)
                                    {
                                        // If local and remote are empty, there's nothing to do.
                                        result = default;
                                    }
                                    else if (!localIsDefault && remoteIsDefault)
                                    {
                                        // If local has data, but remote doesn't, use local.
                                        // However, if local older than the last remote update we
                                        // saw, we should ignore that.
                                        if (!Default.Is(_lastRemoteTime) && _lastLocalTime < _lastRemoteTime)
                                        {
                                            DoLog(LogType.Debug, $"[{DateTime.UtcNow}] Ignoring local only as older than previous remote: {_lastLocalTime:HH:mm:ss.fff} < {_lastRemoteTime:HH:mm:ss.fff}");
                                            result = default;
                                        }
                                        else
                                        {
                                            DoLog(LogType.Debug, "Rendered local as no remote update seen");
                                            localResult = true;
                                            result = _local;
                                        }
                                    }
                                    else if (!remoteIsDefault && localIsDefault)
                                    {
                                        // If remote has data, but local doesn't, use remote.
                                        // However, if remote older than the last local update we
                                        // saw, we should ignore that.
                                        if (!Default.Is(_lastLocalTime) && _lastRemoteTime < _lastLocalTime)
                                        {
                                            DoLog(LogType.Debug, $"Ignoring remote only as older than previous local: {_lastRemoteTime:HH:mm:ss.fff} < {_lastLocalTime:HH:mm:ss.fff}");
                                            result = default;
                                        }
                                        else
                                        {
                                            DoLog(LogType.Debug, "Rendered remote as no local update seen");
                                            result = _remote;
                                        }
                                    }
                                    else
                                    {
                                        // Both have data, so we take most recent set.
                                        Debug.Assert(!localIsDefault, nameof(_local) + " != null");
                                        Debug.Assert(!remoteIsDefault, nameof(_remote) + " != null");
                                        if (_lastRemoteTime > _lastLocalTime)
                                        {
                                            DoLog(LogType.Debug, $"Rendered remote as newer {_lastRemoteTime:HH:mm:ss.fff} > {_lastLocalTime:HH:mm:ss.fff}");
                                            result = _remote;
                                        }
                                        else
                                        {
                                            DoLog(LogType.Debug, $"Rendered local as newer {_lastLocalTime:HH:mm:ss.fff} > {_lastRemoteTime:HH:mm:ss.fff}");
                                            localResult = true;
                                            result = _local;
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
                        return _prerendered && localResult ? default : result;
                    }
                }
            }
        }

        // Data members.
        protected AuthoredObject _local;
        protected AuthoredObject _remote;

        // Retransmit members.
        protected AuthoredObject _lastRemote;
        protected DateTimeOffset _lastLocalTime;
        protected DateTimeOffset _lastRemoteTime;
        protected DateTimeOffset _lastUpdateReceived;

        /// <summary>
        /// Create a new transceiver.
        /// </summary>
        /// <param name="expiryMs">Time in milliseconds after which updates
        /// should be considered expired.</param>
        /// <param name="debugging">Try to log at the debug level.</param>
        public TimestampedGameStateTransceiver(int expiryMs, bool debugging = false,
            IRetransmitReasons retransmitLog = null, TransceiveType type = TransceiveType.Bidirectional,
            bool prerendered = false) : base(expiryMs, debugging, retransmitLog, type, prerendered)
        {
        }

        protected override bool ShouldRetransmit()
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
                    var lastLocalIsDefault = Default.Is(in _lastLocal);
                    var lastRemoteIsDefault = Default.Is(in _lastRemote);

                    // If there is an expired remote update, we might take it over.
                    if (!Default.Is(_lastUpdateReceived) && _lastUpdateReceived < expired)
                    {
                        Debug.Assert(!lastRemoteIsDefault);
                        if (lastLocalIsDefault || _lastLocalTime < _lastUpdateReceived)
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
                    if (lastLocalIsDefault)
                    {
                        _reasons?.NoNoLocal();
                        return false;
                    }

                    // From here we do have a local update.
                    Debug.Assert(!lastLocalIsDefault);

                    if (lastRemoteIsDefault)
                    {
                        // There's a local but no remote, assume responsibility.
                        _reasons?.YesNoRemote();
                        DoLog(LogType.Debug, $"Retransmitting (no remote)");
                        return true;
                    }

                    // Cases where we have both.
                    Debug.Assert(!lastLocalIsDefault);
                    Debug.Assert(!Default.Is(_lastLocalTime));
                    Debug.Assert(!lastRemoteIsDefault);
                    Debug.Assert(!Default.Is(_lastRemoteTime));
                    Debug.Assert(!Default.Is(_lastUpdateReceived));

                    // Retransmit if local more recently got that remote.
                    if (_lastLocalTime > _lastRemoteTime)
                    {
                        DoLog(LogType.Debug,
                            $"Retransmitting (local newer {_lastLocalTime:HH:mm:ss.fff} > {_lastRemoteTime:HH:mm:ss.fff})");
                        _reasons?.YesNewerLocal();
                        return true;
                    }

                    if (_lastRemoteTime >= _lastLocalTime)
                    {
                        // We got a recent remote update so it's not our responsibility.
                        _reasons?.NoRecentRemote();
                        DoLog(LogType.Debug,
                            $"Not retransmitting (remote newer {_lastRemoteTime:HH:mm:ss.fff} >= {_lastLocalTime:HH:mm:ss.fff})");
                        return false;
                    }
                }
            }

            throw new InvalidOperationException("Could not resolve retransmit state");
        }
    }

    /// <summary>
    /// Base implementation of a <see cref="IGameStateTransceiver{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class LastMessageGameStateTransceiver : BaseGameStateTransceiver
    {
        /// <inheritdoc/>
        public override AuthoredObject Local
        {
            set
            {
                // Receive only should not ever set Local.
                if (Type == TransceiveType.ReceiveOnly)
                    throw new InvalidOperationException("Receive only should not set Local");

                lock (_localLock)
                {
                    _local = _lastLocal = value;
                    _lastLocalTime = DateTimeOffset.UtcNow;
                }

                // Send it.
                DoLog(LogType.Debug, "Set local update");
                OnMessageToSend(value);
            }
        }

        /// <inheritdoc/>
        public override AuthoredObject Remote
        {
            set
            {
                if (Type == TransceiveType.SendOnly)
                    throw new InvalidOperationException("Send only should not set Remote");
                lock (_remoteLock)
                {
                    _lastUpdateReceived = DateTime.UtcNow;
                    _remote = _lastRemote = value;
                }
                DoLog(LogType.Debug, "Received remote update");
            }
        }

        /// <inheritdoc/>
        public override AuthoredObject Render
        {
            get
            {
                lock (_localLock)
                {
                    lock (_remoteLock)
                    {
                        AuthoredObject result;
                        bool localResult = false;
                        switch (Type)
                        {
                            case TransceiveType.SendOnly:
                                localResult = true;
                                result = _local;
                                break;
                            case TransceiveType.ReceiveOnly:
                                result = _remote;
                                break;
                            case TransceiveType.Bidirectional:
                                {
                                    var localIsDefault = Default.Is(in _local);
                                    var remoteIsDefault = Default.Is(in _remote);

                                    // Priority for the non null value.
                                    if (localIsDefault && remoteIsDefault)
                                    {
                                        // If local and remote are empty, there's nothing to do.
                                        result = default;
                                    }
                                    else if (!localIsDefault && remoteIsDefault)
                                    {
                                        // If local has data, but remote doesn't, use local.
                                        // However, if local older than the last remote update we
                                        // saw, we should ignore that.
                                        if (!Default.Is(_lastUpdateReceived) && _lastLocalTime < _lastUpdateReceived)
                                        {
                                            DoLog(LogType.Debug, $"[{DateTime.UtcNow}] Ignoring local only as older than previous remote: {_lastLocalTime:HH:mm:ss.fff} < {_lastUpdateReceived:HH:mm:ss.fff}");
                                            result = default;
                                        }
                                        else
                                        {
                                            DoLog(LogType.Debug, "Rendered local as no remote update seen");
                                            localResult = true;
                                            result = _local;
                                        }
                                    }
                                    else if (!remoteIsDefault && localIsDefault)
                                    {
                                        // If remote has data, but local doesn't, use remote.
                                        // However, if remote older than the last local update we
                                        // saw, we should ignore that.
                                        if (!Default.Is(_lastLocalTime) && _lastUpdateReceived < _lastLocalTime)
                                        {
                                            DoLog(LogType.Debug, $"Ignoring remote only as older than previous local: {_lastUpdateReceived:HH:mm:ss.fff} < {_lastLocalTime:HH:mm:ss.fff}");
                                            result = default;
                                        }
                                        else
                                        {
                                            DoLog(LogType.Debug, "Rendered remote as no local update seen");
                                            result = _remote;
                                        }
                                    }
                                    else
                                    {
                                        // Both have data, so we take most recent set.
                                        Debug.Assert(!localIsDefault, nameof(_local) + " != null");
                                        Debug.Assert(!remoteIsDefault, nameof(_remote) + " != null");
                                        if (_lastUpdateReceived > _lastLocalTime)
                                        {
                                            DoLog(LogType.Debug, $"Rendered remote as newer {_lastUpdateReceived:HH:mm:ss.fff} > {_lastLocalTime:HH:mm:ss.fff}");
                                            result = _remote;
                                        }
                                        else
                                        {
                                            DoLog(LogType.Debug, $"Rendered local as newer {_lastLocalTime:HH:mm:ss.fff} > {_lastUpdateReceived:HH:mm:ss.fff}");
                                            localResult = true;
                                            result = _local;
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
                        return _prerendered && localResult ? default : result;
                    }
                }
            }
        }

        // Data members.
        protected AuthoredObject _local;
        protected AuthoredObject _remote;

        // Retransmit members.
        protected AuthoredObject _lastRemote;
        protected DateTimeOffset _lastLocalTime;
        protected DateTimeOffset _lastUpdateReceived;

        /// <summary>
        /// Create a new transceiver.
        /// </summary>
        /// <param name="expiryMs">Time in milliseconds after which updates
        /// should be considered expired.</param>
        /// <param name="debugging">Try to log at the debug level.</param>
        public LastMessageGameStateTransceiver(int expiryMs, bool debugging = false,
            IRetransmitReasons retransmitLog = null, TransceiveType type = TransceiveType.Bidirectional,
            bool prerendered = false) : base(expiryMs, debugging, retransmitLog, type, prerendered)
        {
        }

        protected override bool ShouldRetransmit()
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
                    var lastLocalIsDefault = Default.Is(in _lastLocal);
                    var lastRemoteIsDefault = Default.Is(in _lastRemote);

                    // If there is an expired remote update, we might take it over.
                    if (!Default.Is(_lastUpdateReceived) && _lastUpdateReceived < expired)
                    {
                        Debug.Assert(!lastRemoteIsDefault);
                        if (lastLocalIsDefault || _lastLocalTime < _lastUpdateReceived)
                        {
                            // Take it over.
                            _reasons?.YesExpiredRemote();
                            DoLog(LogType.Debug, $"Retransmitting (expired remote update)");
                            _local = _lastLocal = _lastRemote;
                            _lastLocalTime = DateTimeOffset.UtcNow;
                            _lastRemote = default;
                            _lastUpdateReceived = default;
                            return true;
                        }
                    }

                    // If there's no local at this point, there's nothing to do.
                    if (lastLocalIsDefault)
                    {
                        _reasons?.NoNoLocal();
                        return false;
                    }

                    // From here we do have a local update.
                    Debug.Assert(!lastLocalIsDefault);

                    if (lastRemoteIsDefault)
                    {
                        // There's a local but no remote, assume responsibility.
                        _reasons?.YesNoRemote();
                        DoLog(LogType.Debug, $"Retransmitting (no remote)");
                        return true;
                    }

                    // Cases where we have both.
                    Debug.Assert(!lastLocalIsDefault);
                    Debug.Assert(!Default.Is(_lastLocalTime));
                    Debug.Assert(!lastRemoteIsDefault);
                    Debug.Assert(!Default.Is(_lastUpdateReceived));

                    // Retransmit if local more recently got that remote.
                    if (_lastLocalTime > _lastUpdateReceived)
                    {
                        DoLog(LogType.Debug,
                            $"Retransmitting (local newer {_lastLocalTime:HH:mm:ss.fff} > {_lastUpdateReceived:HH:mm:ss.fff})");
                        _reasons?.YesNewerLocal();
                        return true;
                    }

                    if (_lastUpdateReceived >= _lastLocalTime)
                    {
                        // We got a recent remote update so it's not our responsibility.
                        _reasons?.NoRecentRemote();
                        DoLog(LogType.Debug,
                            $"Not retransmitting (remote newer {_lastUpdateReceived:HH:mm:ss.fff} >= {_lastLocalTime:HH:mm:ss.fff})");
                        return false;
                    }
                }
            }

            throw new InvalidOperationException("Could not resolve retransmit state");
        }
    }

    public class IGameStateTransceiverFactory
    {
        public IGameStateTransceiver Create(Algorithm algorithm, int expiryMs, bool debugging = false, IRetransmitReasons reasons = null, TransceiveType type = TransceiveType.Bidirectional, bool prerendered = false)
        {
            switch (algorithm)
            {
                case Algorithm.Timestamp:
                    return new TimestampedGameStateTransceiver(expiryMs, debugging, reasons, type, prerendered);
                case Algorithm.Latest:
                    return new LastMessageGameStateTransceiver(expiryMs, debugging, reasons, type, prerendered);
                default:
                    throw new ArgumentException($"Unsupported algorithm: {algorithm}", nameof(algorithm));
            }
        }
    }
}
