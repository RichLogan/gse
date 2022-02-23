using System;

namespace gs.sharp.transceiver
{
    public interface IGameStateSender
    {
        /// <summary>
        /// Fires when this transceiver wants to send a message.
        /// </summary>
        event EventHandler<IMessage> MessageToSend;

        /// <summary>
        /// Retransmit if appropriate.
        /// </summary>
        void Retransmit();
    }

    public interface IGameStateReceiver
    {
        IMessage Remote { get; set; }
    }

    public interface IGameStateTransceiver : IGameStateReceiver, IGameStateSender { }

    /// <summary>
    /// Interface for a game state transciever, understanding local
    /// and remote updates, and allowing the renderable result to be
    /// set. Expected to be managed from a <see cref="GameStateManager"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IGameStateTransceiver<T> : IGameStateTransceiver where T : IMessage
    {
        /// <summary>
        /// The most recent local update, if any.
        /// </summary>
        T Local { get; set; }

        /// <summary>
        /// The update you should render at the moment
        /// this is called. Don't cache this.
        /// </summary>
        T Render { get; }
    }

    /// <summary>
    /// Base implementation of a <see cref="IGameStateTransceiver{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class GameStateTransceiver<T> : IGameStateTransceiver<T> where T : IMessage
    {
        /// <inheritdoc/>
        public event EventHandler<IMessage> MessageToSend;

        /// <inheritdoc/>
        public T Local
        {
            get => _local;
            set
            {
                // Only allow local updates to go forward in time.
                if (Local != null && value != null && Local.Timestamp > value.Timestamp)
                {
                    throw new ArgumentException("Local updates must move forward in time", nameof(value));
                }

                // Send it.
                MessageToSend?.Invoke(this, value);

                // Update.
                _local = value;
            }
        }


        /// <inheritdoc/>
        public IMessage Remote
        {
            get => _remote;
            set
            {
                _lastUpdateReceived = DateTime.UtcNow;
                _remote = (T)value;
            }
        }

        private T _render;
        public T Render
        {
            get
            {
                // Priority for the non null value.
                if (Local == null && _remote == null)
                {
                    _render = default;
                }
                else if (Local != null && _remote == null)
                {
                    _render = _local;
                }
                else if (_remote != null && Local == null)
                {
                    _render = _remote;
                }
                else
                {
                    _render = Local.Timestamp >= _remote.Timestamp ? Local : _remote;
                }
                return _render;
            }
        }

        // Data members.
        private T _local;
        private T _remote;

        // Retransmit members.
        private bool _retransmitting = false;
        private DateTime? _lastUpdateReceived = null;
        private DateTime? _lastRetransmit = null;

        /// <inheritdoc/>
        public void Retransmit()
        {
            if (Local.Timestamp > Remote.Timestamp)
            {
                // If we updated this last, we're responsible.
                _retransmitting = true;
            }
            else if (Remote.Timestamp >= Local.Timestamp)
            {
                // If we got an update recently, we're not responsible.
                _retransmitting = false;
            }
            else if (_lastRetransmit != null)
            {
                // We wait at least one whole check before assuming responsibility.
                if (Remote == null)
                {
                    // If there has been no remote update, we'll assume responsibility.
                    _retransmitting = true;
                }
                else if (_lastUpdateReceived != null && _lastRetransmit > _lastUpdateReceived)
                {
                    // If there has been no update since the last cycle, we'll assume responsibility.
                    _retransmitting = true;
                }
            }

            // Do the retransmit if appropriate.
            if (_retransmitting)
            {
                MessageToSend?.Invoke(this, Local);
            }

            // Record the point at which we retransmitted.
            _lastRetransmit = DateTime.UtcNow;
        }
    }
}
