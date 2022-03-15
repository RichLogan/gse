using System;
using System.Collections.Generic;
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
        /// Logs.
        /// </summary>
        public event EventHandler<LogEventArgs> Log;

        protected readonly Dictionary<IObject, IGameStateTransceiver<IMessage>> _transceivers = new Dictionary<IObject, IGameStateTransceiver<IMessage>>(new IObjectEqualityComparer());
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
        public void Register(IObject id, IGameStateTransceiver<IMessage> transceiver)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (transceiver == null) throw new ArgumentNullException(nameof(transceiver));
            transceiver.MessageToSend += Transceiver_MessageToSend;
            _transceivers.Add(id, transceiver);
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
            foreach (var transceiver in _transceivers.Values)
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

        private void Transceiver_MessageToSend<T>(object sender, T e)
        {
            try
            {
                // TODO: Is this correct to do per message?
                // Encode.
                var encoder = new Encoder(1500);
                encoder.Encode(e);

                // Send.
                var encodedMessage = new EncodedMessage(encoder.DataBuffer, encoder.GetDataLength());
                _transport.Send(encodedMessage);
            }
            catch (Exception exception)
            {
                DoLog(LogType.Error, $"Exception sending message: {exception.Message}");
            }
        }

        private void Transport_OnMessageReceived(object sender, EncodedMessage encoded)
        {
            DoLog(LogType.Debug, $"Got message of length {encoded.Length} from transport");
            try
            {
                // Decode.
                var decoder = new Decoder(encoded.Length, encoded.Buffer);
                (object decoded, Type type)? result = decoder.Decode();
                if (!result.HasValue)
                {
                    // Failed to decode anything from this message, but we are expecting to find something at this point.
                    DoLog(LogType.Error, "Undecodable message");
                    return;
                }

                // First, unknowns get handled.
                if (result.Value.type == typeof(UnknownObject))
                {
                    // Not implemented.
                    DoLog(LogType.Debug, "UnknownObject message");
                    return;
                }

                // Then IMessage/IObject.
                if (result.Value.decoded is IMessage message)
                {
                    // Pass to transceivers.
                    DoLog(LogType.Debug, $"[{message.ID}] Got timestamped message {message.Timestamp}");
                    if (_transceivers.TryGetValue(message, out IGameStateTransceiver<IMessage> transceiver))
                    {
                        // Pass the message to the transceiver.
                        transceiver.Remote = message;
                        return;
                    }

                    // If we don't have a transceiver for this, fire the unknown event.
                    OnUnregisteredUpdate?.Invoke(this, message);
                    return;
                }

                // Plain objects next.
                if (result.Value.decoded is IObject obj)
                {
                    // TODO: Not implemented.
                    DoLog(LogType.Debug, $"[{obj.ID}] Got message with no timestamp");
                    return;
                }
            }
            catch (Exception exception)
            {
                DoLog(LogType.Error, $"Exception handling message: {exception.Message}");
            }
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
