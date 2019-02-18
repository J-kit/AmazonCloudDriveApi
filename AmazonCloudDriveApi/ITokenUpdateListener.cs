// <copyright file="ITokenUpdateListener.cs" company="Rambalac">
// Copyright (c) Rambalac. All rights reserved.
// </copyright>

using System;
using System.Threading.Tasks;

namespace Azi.Amazon.CloudDrive
{
    /// <summary>
    /// Listener for Authentication Token updates
    /// </summary>
    public interface ITokenUpdateListener
    {
        /// <summary>
        /// Called when Authentication Token updated
        /// </summary>
        /// <param name="accessToken">Authentication token</param>
        /// <param name="refreshToken">Authentication token refresh token</param>
        /// <param name="expiresIn">Authentication token expiration time</param>
        /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
        Task OnTokenUpdated(string accessToken, string refreshToken, DateTime expiresIn);
    }
}