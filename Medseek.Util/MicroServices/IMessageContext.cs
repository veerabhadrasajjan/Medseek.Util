﻿namespace Medseek.Util.MicroServices
{
    using Medseek.Util.Messaging;

    /// <summary>
    /// Interface for types that provide information about the current context 
    /// for executing micro-service operations.
    /// </summary>
    public interface IMessageContext
    {
        /// <summary>
        /// Gets the message properties.
        /// </summary>
        IMessageProperties Properties
        {
            get;
        }

        /// <summary>
        /// Creates an independent copy of the message context.
        /// </summary>
        /// <returns>
        /// The new message context that was created from the original.
        /// </returns>
        IMessageContext Clone();
    }
}