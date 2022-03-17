using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace gs.sharp.transceiver
{
    public enum LogType { Info, Error, Debug, }
    public class LogEventArgs : EventArgs
    {
        public LogType LogType { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Manages <see cref="IGameStateTransceiver"/> through an <see cref="IGameStateTransport"/>,
    /// sending, receiving and updating the render property for each transciever automatically.
    /// </summary>
    public class GameStateManager : IDisposable
    {
        /// <summary>
        /// Fired when we receive an update for an object
        /// we're not tracking.
        /// </summary>
        public event EventHandler<IMessage> OnUnregisteredUpdate;

        /// <summary>
        /// Fired when we receive an UnknownObject update
        /// for a tag we're not tracking.
        /// </summary>
        public event EventHandler<UnknownObject> OnUnregisteredUnknown;

        /// <summary>
        /// Logs.
        /// </summary>
        public event EventHandler<LogEventArgs> Log;

        private readonly Dictionary<IObject, IGameStateTransceiver> _transceivers = new Dictionary<IObject, IGameStateTransceiver>(new IObjectEqualityComparer());
        private readonly Dictionary<ulong, IGameStateTransceiver> _unknowns = new Dictionary<ulong, IGameStateTransceiver>();
        private readonly HashSet<IGameStateTransceiver> _allTransceivers = new HashSet<IGameStateTransceiver>();
        private readonly IGameStateTransport _transport;
        private readonly bool _debugging;
        private bool _disposedValue;

        /// <summary>
        /// Create a new GameStateManager object.
        /// </summary>
        /// <param name="transport">Transport to use.</param>
        /// <param name="debugging">True to enable debug logs.</param>
        public GameStateManager(IGameStateTransport transport, bool debugging = false)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _debugging = debugging;
            _transport.OnMessageReceived += Transport_OnMessageReceived;
        }

        /// <summary>
        /// Register a transceiver to this manager.
        /// </summary>
        /// <param name="id">Unique identifier.</param>
        /// <param name="transceiver">The transceiver to managed.</param>
        public void Register(IObject id, IGameStateTransceiver transceiver)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            SetupTransceiver(transceiver);
            _transceivers.Add(id, transceiver);
        }

        public void Register(ulong tag, IGameStateTransceiver transceiver)
        {
            SetupTransceiver(transceiver);
            _unknowns.Add(tag, transceiver);
        }

        private void SetupTransceiver(IGameStateTransceiver transceiver)
        {
            if (transceiver == null)
            {
                throw new ArgumentNullException(nameof(transceiver));
            }
            transceiver.MessageToSend += Transceiver_MessageToSend;
            _allTransceivers.Add(transceiver);
        }

        /// <summary>
        /// Unregister a transceiver from this manager.
        /// </summary>
        /// <param name="id">Unique identifier.</param>
        public void Unregister(IObject id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (_transceivers.TryGetValue(id, out var transceiver))
            {
                transceiver.MessageToSend -= Transceiver_MessageToSend;
                _transceivers.Remove(id);
            }
        }

        /// <summary>
        /// Get all transceivers to check retransmission.
        /// </summary>
        public void RetransmitAll()
        {
            // Process all transceivers.
            foreach (var transceiver in _allTransceivers)
            {
                try
                {
                    transceiver.Retransmit();
                }
                catch (Exception exception)
                {
                    DoLog(LogType.Error, $"Exception during retransmit: {exception.Message}");
                    continue;
                }
            }
        }

        public void Dispose() => Dispose(true);

        private void DoLog(LogType level, string message)
        {
            if (level == LogType.Debug && !_debugging) return;
            Log?.Invoke(this, new LogEventArgs() { LogType = level, Message = message });
        }

        private void Transceiver_MessageToSend(object sender, GSObject e)
        {
            Encoder encoder;
            try
            {
                // TODO: Is this correct to do per message?
                encoder = new Encoder(1500);
                encoder.Encode(e);
            }
            catch (Exception exception)
            {
                DoLog(LogType.Error, $"Exception on encode: {exception.Message}");
                return;
            }

            // Send.
            try
            {
                var encodedMessage = new EncodedMessage(encoder.DataBuffer, encoder.GetDataLength());
                _transport.Send(encodedMessage);
            }
            catch (Exception exception)
            {
                DoLog(LogType.Error, $"Exception sending message: {exception.Message}");
                return;
            }
        }

        private void Transport_OnMessageReceived(object sender, EncodedMessage encoded)
        {
            DoLog(LogType.Debug, $"Got message of length {encoded.Length} from transport");
            try
            {
                // Decode.
                var decoder = new Decoder(encoded.Length, encoded.Buffer);
                GSObject result = decoder.Decode();
                if (Default.Is(result))
                {
                    // Failed to decode anything from this message, but we are expecting to find something at this point.
                    DoLog(LogType.Error, "Undecodable message");
                    return;
                }

                Tag type = (Tag)result.Type;
                IGameStateTransceiver transceiver;
                switch (type)
                {
                    case Tag.Invalid:
                        throw new InvalidOperationException("Invalid tag");
                    case Tag.Hand1:
                        transceiver = Handle(result.Hand1);
                        break;
                    case Tag.Hand2:
                        transceiver = Handle(result.Hand2);
                        break;
                    case Tag.Head1:
                        transceiver = Handle(result.Head1);
                        break;
                    case Tag.Object1:
                        transceiver = Handle(result.Head1);
                        break;
                    default:
                        transceiver = Handle(result.UnknownObject);
                        break;
                }

                if (transceiver != null)
                {
                    transceiver.Remote = result;
                }
            }
            catch (Exception exception)
            {
                DoLog(LogType.Error, $"Exception handling message: {exception.Message}");
            }
        }

        private IGameStateTransceiver Handle(UnknownObject unknown)
        {
            DoLog(LogType.Debug, $"[Tag {unknown.Tag}] Got unknown object");
            if (_unknowns.TryGetValue(unknown.Tag, out var transceiver))
            {
                return transceiver;
            }
            OnUnregisteredUnknown?.Invoke(this, unknown);
            return null;
        }

        private IGameStateTransceiver Handle(IMessage message)
        {
            // Pass to transceivers.
            DoLog(LogType.Debug, $"[{message.ID}] Got timestamped message {message.Timestamp}");
            if (_transceivers.TryGetValue(message, out var transceiver))
            {
                // Pass the message to the transceiver.
                return transceiver;
            }

            // If we don't have a transceiver for this, fire the unknown event.
            OnUnregisteredUpdate?.Invoke(this, message);
            return null;
        }

        // Protected implementation of Dispose pattern.
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    foreach (var receiver in _transceivers.Values)
                    {
                        receiver.MessageToSend -= Transceiver_MessageToSend;
                    }
                    _transceivers.Clear();
                }

                _disposedValue = true;
            }
        }
    }

    /// <summary>
    /// Derivative of <see cref="GameStateManager"/> that calculates
    /// on a timer.
    /// </summary>
    public class TimedGameStateManager : GameStateManager
    {
        private readonly Timer _timer;

        /// <summary>
        /// Create a new GameStateManager object.
        /// </summary>
        /// <param name="transport">Transport to use.</param>
        /// <param name="minInterval">Min interval at which to process all transceivers.</param>
        /// <param name="maxInterval">Max interval at which to process all transceivers.</param>
        /// <param name="debugging">True to enable debug logs.</param>
        public TimedGameStateManager(int minInterval, int maxInterval, IGameStateTransport transport, bool debugging = false) : base(transport, debugging)
        {
            if (minInterval <= 0) throw new ArgumentException("Minimum interval should be >0");
            if (maxInterval < minInterval) throw new ArgumentException("Maximum interval should greater than minimum interval");
            var interval = new Random().Next(minInterval, maxInterval);
            _timer = new Timer(OnTimerElapsed, null, 0, interval);
        }

        // To detect redundant calls
        private bool _disposedValue;

        // Protected implementation of Dispose pattern.
        protected override void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _timer.Dispose();
                }

                _disposedValue = true;
            }

            // Call base class implementation.
            base.Dispose(disposing);
        }

        private void OnTimerElapsed(object state) => RetransmitAll();
    }
}
