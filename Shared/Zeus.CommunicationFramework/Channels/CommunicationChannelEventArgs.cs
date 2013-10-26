﻿using System;

namespace Zeus.CommunicationFramework.Channels {

    /// <summary>
    ///     Stores communication channel information to be used by an event.
    /// </summary>
    public class CommunicationChannelEventArgs : EventArgs {

        /// <summary>
        ///     Communication channel that is associated with this event.
        /// </summary>
        public ICommunicationChannel Channel { get; private set; }

        /// <summary>
        ///     Creates a new CommunicationChannelEventArgs object.
        /// </summary>
        /// <param name="channel">Communication channel that is associated with this event</param>
        public CommunicationChannelEventArgs(ICommunicationChannel channel) {
            Channel = channel;
        }

    }

}