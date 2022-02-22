using System;

namespace gs.sharp.transceiver
{
    public interface IGameStateSender
    {
        /// <summary>
        /// Fires when this transceiver wants to send a message.
        /// </summary>
        event EventHandler<IMessage> MessageToSend;
    }

    public interface IGameStateReceiver
    {
        IMessage Remote { get; set; }
    }

    public interface IGameStateTransceiver : IGameStateReceiver, IGameStateSender
    {
        /// <summary>
        /// Calculate the render output to the given value.
        /// You should not call this directly, a <see cref="GameStateManager"/>
        /// should manage this value on your behalf.
        /// </summary>
        void CalculateRender();
    }

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
        /// this is called. Don't cache this. Consumed on use.
        /// </summary>
        T Render { get; set; }
    }

    /// <summary>
    /// Base implementation of a <see cref="IGameStateTransceiver{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class GameStateTransceiver<T> : IGameStateTransceiver<T> where T : IMessage
    {
        public event EventHandler<IMessage> MessageToSend;

        private T _local;
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
        public IMessage Remote { get; set; }

        private T _render;
        public T Render
        {
            get
            {
                var tmp = _render;
                _render = default;
                return tmp;

            }
            set => _render = value;
        }

        public void CalculateRender()
        {
            // If we have a local update, and no remote update,
            // take local.
            if (Local != null && Remote == null)
            {
                Render = Local;
                Local = default;
                return;
            }

            // If we have a remote update, and no local update,
            // take remote.
            if (Remote != null && Local == null)
            {
                Render = (T)Remote;
                Remote = null;
                return;
            }

            // If we have a local update newer than a remote
            // update, take local and send to peers.
            if (Local.Timestamp >= Remote.Timestamp)
            {
                // Update the data to be rendered.
                Render = Local;
                // Send this update out.
                MessageToSend?.Invoke(this, Local);

                Local = default;
                return;
            }


            // If we have a newer remote update, apply it.
            if (Remote.Timestamp >= Local.Timestamp)
            {
                // Update the data to be rendered.
                Render = (T)Remote;
                Remote = null;
                return;
            }
        }
    }
}
