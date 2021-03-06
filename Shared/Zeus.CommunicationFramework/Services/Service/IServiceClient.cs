﻿using System;
using Zeus.CommunicationFramework.EndPoints;

namespace Zeus.CommunicationFramework.Services.Service {

    /// <summary>
    ///     Represents a client that uses a SDS service.
    /// </summary>
    public interface IServiceClient {

        /// <summary>
        ///     Unique identifier for this client.
        /// </summary>
        long ClientId { get; }

        /// <summary>
        ///     Gets the communication state of the Client.
        /// </summary>
        CommunicationStates CommunicationState { get; }

        /// <summary>
        ///     Gets endpoint of remote application.
        /// </summary>
        ProtocolEndPointBase RemoteEndPoint { get; }

        /// <summary>
        ///     This event is raised when client is disconnected from service.
        /// </summary>
        event EventHandler Disconnected;

        /// <summary>
        ///     Closes client connection.
        /// </summary>
        void Disconnect();

        /// <summary>
        ///     Gets the client proxy interface that provides calling client methods remotely.
        /// </summary>
        /// <typeparam name="T">Type of client interface</typeparam>
        /// <returns>Client interface</returns>
        T GetClientProxy<T>() where T : class;

    }

}