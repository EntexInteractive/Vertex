// Copyright 2026 Entex Interactive

using System.Net;
using Vertex.Core.Data;

namespace Vertex.Core.Security
{
    public enum InstanceType
    {
        Server,
        Client
    }


    /// <summary>
    /// Represents the authority within Vertex. This class cannot be inherited.
    /// </summary>
    public static class Authority
    {
        /// <summary>
        /// Gets the current instance name.
        /// </summary>
        /// <returns>Server if server, otherwise Client.</returns>
        public static InstanceType GetInstanceType()
        {
            return IsServer() ? InstanceType.Server : InstanceType.Client;
        }

        /// <summary>
        /// Checks if a <see cref="VUser"/> has a authoritative perrmission.
        /// </summary>
        /// <param name="user"></param>
        /// <param name="permission"></param>
        /// <returns>True if authoritative, false otherwise.</returns>
        public static bool HasAuthority(VUser user, int permission)
        {
            return user.Permission >= permission;
        }

        /// <summary>
        /// Checks if the instance is the authoritative server.
        /// </summary>
        /// <returns>True if server, false otherwise.</returns>
        public static bool IsServer()
        {
            return AppDomain.CurrentDomain.FriendlyName.Contains("server", StringComparison.OrdinalIgnoreCase);
        }

        /*/// <summary>
        /// Verifies if the incoming address is allowed connection to the server.
        /// </summary>
        /// <param name="endPoint">The incoming connection endpoint.</param>
        /// <returns>True if allowed, false if denied.</returns>
        /// <exception cref="ClientUnauthorizedException"></exception>
        public static bool VerifyAddress(IPEndPoint endPoint)
        {
            UnauthorizedException.ThrowIfClient();
            if (ServerConfig.IsWhitelisting)
            {
                return Array.IndexOf(ServerConfig.AddressList, endPoint.Address.ToString()) > -1;
            }
            else
            {
                return Array.IndexOf(ServerConfig.AddressList, endPoint.Address.ToString()) == -1;
            }
        }*/
    }
}