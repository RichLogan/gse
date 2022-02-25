using System;
using System.Collections.Generic;
using System.Threading;

namespace gs.sharp.transceiver
{
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

        protected const int MIN_START_TIME = 500;
        protected const int MAX_START_TIME = 1000;

        protected readonly Dictionary<IObject, IGameStateTransceiver> _transcievers = new Dictionary<IObject, IGameStateTransceiver>();
        private readonly IGameStateTransport _transport;
        private bool _disposedValue;

        /// <summary>
        /// Create a new GameStateManager object.
        /// </summary>
        /// <param name="transport">Transport to use.</param>
        public GameStateManager(in IGameStateTransport transport)
        {
            _transport = transport;
            _transport.OnMessageReceived += Transport_OnMessageReceived;
        }

        /// <summary>
        /// Register a transciever to this manager.
        /// </summary>
        /// <param name="id">Unique identifier.</param>
        /// <param name="transciever">The transciever to managed.</param>
        public void Register(in IObject id, in IGameStateTransceiver transciever)
        {
            transciever.MessageToSend += Transciever_MessageToSend;
            _transcievers.Add(id, transciever);
        }

        /// <summary>
        /// Get all transceivers to check retransmission.
        /// </summary>
        public void RetransmitAll()
        {
            // Process all transcievers.
            foreach (var transciever in _transcievers.Values)
            {
                transciever.Retransmit();
            }
        }

        public void Dispose() => Dispose(true);

        private void Transciever_MessageToSend(object sender, IMessage e)
        {
            // TODO: Is this correct to do per message?
            // Encode.
            var encoder = new Encoder(1500);
            encoder.Encode(e);

            // Send.
            var encodedMessage = new EncodedMessage(encoder.DataBuffer, encoder.GetDataLength());
            _transport.Send(encodedMessage);
        }

        private void Transport_OnMessageReceived(object sender, EncodedMessage encoded)
        {
            // Decode.
            var decoder = new Decoder(encoded.Length, encoded.Buffer);
            (object decoded, Type type)? result = decoder.Decode();

            if (!result.HasValue)
            {
                // Not good!
                throw new InvalidOperationException("Undecodable message");
            }

            // First, unknowns get handled.

            if (result.Value.type == typeof(UnknownObject))
            {
                throw new NotImplementedException();
            }

            // Then IMessage/IObject.
            if (result.Value.decoded is IMessage message)
            {
                // Pass to transceivers.
                if (_transcievers.TryGetValue(message, out IGameStateTransceiver transceiver))
                {
                    // Pass the message to the transceiver.
                    transceiver.Remote = message;
                    return;
                }

                // If we don't have a transceiver for this, fire the unknown event.
                OnUnregisteredUpdate?.Invoke(this, message);
            }
            else if (result.Value.decoded is IObject obj)
            {
                // Just an object.
                throw new NotImplementedException();
            }
            else
            {
                throw new InvalidOperationException("Uncastable message");
            }
        }

        // Protected implementation of Dispose pattern.
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    foreach (var receiver in _transcievers.Values)
                    {
                        receiver.MessageToSend -= Transciever_MessageToSend;
                    }
                    _transcievers.Clear();
                }

                _disposedValue = true;
            }
        }
    }

    /// <summary>
    /// Derivative of <see cref="GameStateManager"/> that calculates
    /// on a timer.
    /// </summary>
    public class TimedGameStateManager : GameStateManager, IDisposable
    {
        private readonly Timer _timer;

        /// <summary>
        /// Create a new GameStateManager object.
        /// </summary>
        /// <param name="transport">Transport to use.</param>
        /// <param name="interval">Interval at which to process all transcievers.</param>
        public TimedGameStateManager(in IGameStateTransport transport, int interval) : base(transport)
        {
            _timer = new Timer(OnTimerElapsed, null, new Random().Next(MIN_START_TIME, MAX_START_TIME), interval);
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
